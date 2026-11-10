using System.Runtime.InteropServices;

namespace Libcamera.Models;

public sealed class VideoCaptureDevice : IDisposable
{
    private readonly object _sync = new();

    private IntPtr _manager;
    private IntPtr _camera;
    private IntPtr _config;
    private IntPtr _allocator;
    private IntPtr _request;
    private IntPtr _bufferHandle;

    private bool _disposed;
    private bool _isApplied;
    private bool _isPreviewing;
    private bool _isCapturing;
    private bool _isStreaming;
    // Set while libcamera is being stopped. Its stop waits for the frame callback to return, and the stop is made
    // holding _sync: a callback that waited for _sync then would wait for ever, and the stop with it.
    private volatile bool _stopping;

    private bool _reconfigureNeeded = true;
    private bool _controlsDirty = true;

    private LibcameraNative.RequestCompletedCallback? _callback;

    private readonly ManualResetEventSlim _stillWait = new(false);
    private byte[]? _pendingStillBytes;
    private int _pendingStillSkipFrames;
    private bool _stillRequested;

    private int _frameCounter;
    private byte[][] _frameBuffers = [];
    private int _frameBufferNext;
    private int _frameBufferCount = 8;
    private MemoryStream? _captureStream;
    private byte[]? _latestCaptureBytes;

    private VideoCaptureConfiguration _desiredConfig;
    private VideoCaptureConfiguration _appliedConfig;

    // Controls set by name. Guarded by _sync.
    // Replaced whole, never changed, so read without the lock: a frame is handed over holding it.
    private volatile IReadOnlyDictionary<string, CameraControl> _controls;
    private Dictionary<string, double[]> _named = new(StringComparer.Ordinal);
    private readonly List<(string Name, double[] Values)> _once = [];
    private volatile string? _controlError;
    private string[] _watched = [];
    private IReadOnlyDictionary<string, double[]> _metadata = new Dictionary<string, double[]>();
    private readonly double[] _metadataBuffer = new double[16];

    public string CameraId { get; }
    public string Model { get; }
    public CameraCapabilities Capabilities { get; }

    public bool IsInitialized => _camera != IntPtr.Zero;
    public bool IsApplied => _isApplied;
    public bool IsPreviewing => _isPreviewing;
    public bool IsCapturing => _isCapturing;
    public bool IsStreaming => _isStreaming;

    /// <summary>
    /// Every control the camera takes, by its libcamera name. Read again each time the camera is configured, as
    /// some ranges depend on the sensor mode. Empty from a wrapper built before controls could be listed.
    /// </summary>
    public IReadOnlyDictionary<string, CameraControl> Controls
    {
        get => _controls;
    }

    /// <summary>Why the last control set by name was refused, or null when it was taken.</summary>
    public string? ControlError
    {
        get => _controlError;
    }

    /// <summary>
    /// What the newest frame was taken with, for each name given to <see cref="WatchMetadata"/> that it carried:
    /// ExposureTime, AnalogueGain, ColourTemperature, LensPosition. A new dictionary each frame, never changed after.
    /// </summary>
    public IReadOnlyDictionary<string, double[]> Metadata => Volatile.Read(ref _metadata);

    public VideoCaptureConfiguration DesiredConfiguration => _desiredConfig;
    public VideoCaptureConfiguration AppliedConfiguration => _appliedConfig;

    /// <summary>
    /// How many frames are kept. Frames are copied into a ring of this many buffers, so the
    /// <see cref="VideoFrameAvailableEventArgs.Data"/> of a frame stays as it was until this many more
    /// frames have arrived, and is then written over. Whoever wants a frame for longer copies it.
    /// </summary>
    public int FrameBufferCount
    {
        get => _frameBufferCount;
        set => _frameBufferCount = Math.Max(value, 2);
    }

    public event EventHandler<VideoFrameAvailableEventArgs>? PreviewFrameAvailable;
    public event EventHandler<VideoFrameAvailableEventArgs>? CaptureFrameAvailable;

    private VideoCaptureDevice(
        IntPtr manager,
        IntPtr camera,
        string cameraId,
        string model,
        CameraCapabilities capabilities)
    {
        _manager = manager;
        _camera = camera;
        CameraId = cameraId;
        Model = model;
        Capabilities = capabilities;

        var defaultFormat = capabilities.PixelFormats.FirstOrDefault()
            ?? throw new InvalidOperationException("No pixel formats reported by camera.");

        var defaultSize = defaultFormat.SupportedSizes.FirstOrDefault();
        if (defaultSize.Width == 0 || defaultSize.Height == 0)
            defaultSize = defaultFormat.MaxSize;

        _desiredConfig = new VideoCaptureConfiguration
        {
            FrameSize = defaultSize,
            FourCcFormat = defaultFormat.FourCc,
            ExposureUs = 0,
            Gain = 1.0f,
            Brightness = 0f,
            Contrast = 1.0f,
            AutoExposure = true,
            AutoWhiteBalance = true
        };

        _appliedConfig = _desiredConfig;
        _callback = OnRequestCompleted;
        _controls = LibcameraNative.GetControls(camera);
    }

    public static bool TryCreate(out VideoCaptureDevice? device, out string? error)
    {
        device = null;
        error = null;

        IntPtr manager = IntPtr.Zero;
        IntPtr camera = IntPtr.Zero;

        try
        {
            manager = LibcameraNative.lc_manager_create();
            if (manager == IntPtr.Zero)
            {
                error = "Failed to create libcamera manager.";
                return false;
            }

            int rc = LibcameraNative.lc_manager_start(manager);
            if (rc < 0)
            {
                error = $"Failed to start libcamera manager. Status={rc}.";
                Cleanup(manager, camera);
                return false;
            }

            int count = LibcameraNative.lc_manager_camera_count(manager);
            if (count < 0)
            {
                error = $"Failed to enumerate cameras. Status={count}.";
                Cleanup(manager, camera);
                return false;
            }

            if (count == 0)
            {
                error = "No cameras detected.";
                Cleanup(manager, camera);
                return false;
            }

            string cameraId = LibcameraNative.GetCameraId(manager, 0);

            camera = LibcameraNative.lc_camera_open(manager, cameraId);
            if (camera == IntPtr.Zero)
            {
                error = "Failed to open first camera.";
                Cleanup(manager, camera);
                return false;
            }

            rc = LibcameraNative.lc_camera_acquire(camera);
            if (rc < 0)
            {
                error = $"Failed to acquire camera. Status={rc}.";
                Cleanup(manager, camera);
                return false;
            }

            string model;
            try
            {
                model = LibcameraNative.GetCameraModel(camera);
            }
            catch
            {
                model = "Unknown";
            }

            CameraCapabilities capabilities = LoadCapabilities(camera);

            device = new VideoCaptureDevice(
                manager,
                camera,
                cameraId,
                model,
                capabilities);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Cleanup(manager, camera);
            return false;
        }
    }

    public void SetCapability(FrameSize size, string fourCcFormat)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(fourCcFormat) || fourCcFormat.Length != 4)
            throw new ArgumentException("FourCC format must be exactly 4 characters.", nameof(fourCcFormat));

        var format = Capabilities.PixelFormats.FirstOrDefault(p => p.FourCc == fourCcFormat);
        if (format is null)
            throw new InvalidOperationException($"Format '{fourCcFormat}' is not supported.");

        bool exactSizeMatch = format.SupportedSizes.Any(s => s.Width == size.Width && s.Height == size.Height);
        if (!exactSizeMatch)
            throw new InvalidOperationException(
                $"Resolution {size.Width}x{size.Height} is not supported for format {fourCcFormat}.");

        lock (_sync)
        {
            _desiredConfig = _desiredConfig with
            {
                FrameSize = size,
                FourCcFormat = fourCcFormat
            };
            _reconfigureNeeded = true;
        }
    }

    /// <summary>
    /// Takes the picture from this sensor mode - its output size and bit depth - rather than the one libcamera would
    /// pick for the picture's size. A small picture otherwise can come from a crop of the middle of the sensor: on the
    /// IMX219, 640x480 is its middle 1280x960 binned, a third of the view; 1640x1232 is the whole of it.
    /// </summary>
    public void SetSensorMode(FrameSize size, uint bitDepth = 10)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            _desiredConfig = _desiredConfig with { SensorSize = size, SensorBitDepth = bitDepth };
            _reconfigureNeeded = true;
        }
    }

    public void SetExposure(int exposureUs)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            _desiredConfig = _desiredConfig with { ExposureUs = exposureUs };
            _controlsDirty = true;
        }
    }

    public void SetGain(float gain)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            _desiredConfig = _desiredConfig with { Gain = gain };
            _controlsDirty = true;
        }
    }

    public void SetBrightness(float brightness)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            _desiredConfig = _desiredConfig with { Brightness = brightness };
            _controlsDirty = true;
        }
    }

    public void SetContrast(float contrast)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            _desiredConfig = _desiredConfig with { Contrast = contrast };
            _controlsDirty = true;
        }
    }

    public void SetAutoExposure(bool enabled)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            _desiredConfig = _desiredConfig with { AutoExposure = enabled };
            _controlsDirty = true;
        }
    }

    public void SetAutoWhiteBalance(bool enabled)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            _desiredConfig = _desiredConfig with { AutoWhiteBalance = enabled };
            _controlsDirty = true;
        }
    }

    /// <summary>
    /// Sets controls by name, every one wanted at once: they replace any set this way before, and from then on the
    /// plain setters (exposure, gain, brightness, contrast, auto exposure and white balance) are no longer sent, so
    /// the two never fight. A name the camera does not take is passed over. Reaches the next frame.
    /// </summary>
    public void SetControls(IReadOnlyDictionary<string, double[]> controls)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            _named = new Dictionary<string, double[]>(controls, StringComparer.Ordinal);
            _controlsDirty = true;
        }
    }

    /// <summary>Sends a control once, with the next frame, and never again: AfTrigger to focus now.</summary>
    public void Trigger(string name, params double[] values)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            _once.Add((name, values));
            _controlsDirty = true;
        }
    }

    /// <summary>The names of what each frame reports that <see cref="Metadata"/> is to keep.</summary>
    public void WatchMetadata(params string[] names)
    {
        lock (_sync)
            _watched = names;
    }

    /// <summary>Mirrors the picture across, turns it upside down, or both (half a turn). Reaches the camera at <see cref="Apply"/>.</summary>
    public void SetOrientation(bool hflip, bool vflip)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            if (_desiredConfig.HFlip == hflip && _desiredConfig.VFlip == vflip)
                return;
            _desiredConfig = _desiredConfig with { HFlip = hflip, VFlip = vflip };
            _reconfigureNeeded = true;
        }
    }

    public void Apply()
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            bool resumePreview = _isPreviewing;
            bool resumeCapture = _isCapturing;
            bool wasStreaming = _isStreaming;

            if (wasStreaming)
                StopStreamingInternal();

            if (_reconfigureNeeded)
            {
                DestroyPipelineHandles();

                _config = LibcameraNative.lc_camera_generate_configuration(_camera);
                LibcameraNative.ThrowIfNull(_config, nameof(LibcameraNative.lc_camera_generate_configuration));

                LibcameraNative.ThrowIfFailed(
                    LibcameraNative.lc_config_set_size(_config, _desiredConfig.FrameSize.Width, _desiredConfig.FrameSize.Height),
                    nameof(LibcameraNative.lc_config_set_size));

                LibcameraNative.ThrowIfFailed(
                    LibcameraNative.lc_config_set_pixel_format(_config, LibcameraNative.FourCc(_desiredConfig.FourCcFormat)),
                    nameof(LibcameraNative.lc_config_set_pixel_format));

                if (_desiredConfig.SensorSize is { } sensor)
                    LibcameraNative.ThrowIfFailed(
                        LibcameraNative.lc_config_set_sensor_mode(_config, sensor.Width, sensor.Height, _desiredConfig.SensorBitDepth),
                        nameof(LibcameraNative.lc_config_set_sensor_mode));

                if (_desiredConfig.HFlip || _desiredConfig.VFlip)
                    LibcameraNative.ThrowIfFailed(
                        LibcameraNative.lc_config_set_orientation(_config, _desiredConfig.HFlip ? 1 : 0, _desiredConfig.VFlip ? 1 : 0),
                        nameof(LibcameraNative.lc_config_set_orientation));

                int validateStatus = LibcameraNative.lc_config_validate(_config);
                LibcameraNative.ThrowIfFailed(validateStatus >= 0 ? 0 : validateStatus, nameof(LibcameraNative.lc_config_validate));

                LibcameraNative.ThrowIfFailed(
                    LibcameraNative.lc_camera_configure(_camera, _config),
                    nameof(LibcameraNative.lc_camera_configure));

                // Some ranges (exposure, the crop) follow the sensor mode just chosen.
                _controls = LibcameraNative.GetControls(_camera);

                _allocator = LibcameraNative.lc_allocator_create(_camera);
                LibcameraNative.ThrowIfNull(_allocator, nameof(LibcameraNative.lc_allocator_create));

                LibcameraNative.ThrowIfFailed(
                    LibcameraNative.lc_allocator_allocate(_allocator),
                    nameof(LibcameraNative.lc_allocator_allocate));

                int bufferCount = LibcameraNative.lc_allocator_buffer_count(_allocator);
                LibcameraNative.ThrowIfFailed(bufferCount >= 0 ? 0 : bufferCount, nameof(LibcameraNative.lc_allocator_buffer_count));
                if (bufferCount == 0)
                    throw new InvalidOperationException("No camera buffers were allocated.");

                LibcameraNative.ThrowIfFailed(
                    LibcameraNative.lc_allocator_get_buffer(_allocator, 0, out _bufferHandle),
                    nameof(LibcameraNative.lc_allocator_get_buffer));

                _request = LibcameraNative.lc_request_create(_camera);
                LibcameraNative.ThrowIfNull(_request, nameof(LibcameraNative.lc_request_create));

                LibcameraNative.ThrowIfFailed(
                    LibcameraNative.lc_request_attach_buffer(_request, _bufferHandle),
                    nameof(LibcameraNative.lc_request_attach_buffer));

                LibcameraNative.ThrowIfFailed(
                    LibcameraNative.lc_camera_set_request_completed_callback(_camera, _callback!, IntPtr.Zero),
                    nameof(LibcameraNative.lc_camera_set_request_completed_callback));

                _appliedConfig = _desiredConfig;
                _reconfigureNeeded = false;
                _controlsDirty = true;
                _isApplied = true;
            }

            if (resumePreview || resumeCapture)
            {
                StartStreamingInternal();
                _isPreviewing = resumePreview;
                _isCapturing = resumeCapture;
            }
        }
    }

    public void StartPreview()
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            EnsureApplied();
            if (!_isStreaming)
                StartStreamingInternal();

            _isPreviewing = true;
        }
    }

    public void StopPreview()
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            _isPreviewing = false;
            if (!_isCapturing && _isStreaming)
                StopStreamingInternal();
        }
    }

    public void StartCapture()
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            EnsureApplied();

            _captureStream?.Dispose();
            _captureStream = new MemoryStream();
            _latestCaptureBytes = null;
            _frameCounter = 0;

            if (!_isStreaming)
                StartStreamingInternal();

            _isCapturing = true;
        }
    }

    public void StopCapture()
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            _isCapturing = false;

            if (_captureStream is not null)
            {
                _latestCaptureBytes = _captureStream.ToArray();
                _captureStream.Dispose();
                _captureStream = null;
            }

            if (!_isPreviewing && _isStreaming)
                StopStreamingInternal();
        }
    }

    public Stream GetLatestCaptureStream()
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            if (_isCapturing)
                throw new InvalidOperationException("Stop capture before requesting the latest capture stream.");

            if (_latestCaptureBytes is null || _latestCaptureBytes.Length == 0)
                throw new InvalidOperationException("No capture is available.");

            return new MemoryStream(_latestCaptureBytes, writable: false);
        }
    }

    public byte[] GetLatestCaptureBytes()
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            if (_isCapturing)
                throw new InvalidOperationException("Stop capture before requesting latest capture bytes.");

            if (_latestCaptureBytes is null || _latestCaptureBytes.Length == 0)
                throw new InvalidOperationException("No capture is available.");

            return (byte[])_latestCaptureBytes.Clone();
        }
    }

    public byte[] CaptureStillBytes(int settleFrames = 8, int timeoutMs = 5000)
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            EnsureApplied();

            _pendingStillBytes = null;
            _stillRequested = true;
            _pendingStillSkipFrames = Math.Max(0, settleFrames);
            _stillWait.Reset();

            bool stopAfter = false;
            if (!_isStreaming)
            {
                StartStreamingInternal();
                stopAfter = true;
            }

            Monitor.Exit(_sync);
            try
            {
                if (!_stillWait.Wait(timeoutMs))
                    throw new TimeoutException("Timed out waiting for still frame.");
            }
            finally
            {
                Monitor.Enter(_sync);

                if (stopAfter && !_isPreviewing && !_isCapturing && _isStreaming)
                    StopStreamingInternal();
            }

            if (_pendingStillBytes is null || _pendingStillBytes.Length == 0)
                throw new InvalidOperationException("Still capture failed.");

            return _pendingStillBytes;
        }
    }

    public Stream CaptureStillStream(int settleFrames = 8, int timeoutMs = 5000)
    {
        byte[] bytes = CaptureStillBytes(settleFrames, timeoutMs);
        return new MemoryStream(bytes, writable: false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                if (_isStreaming)
                    StopStreamingInternal();
            }
            catch
            {
            }

            DestroyPipelineHandles();

            if (_camera != IntPtr.Zero)
            {
                try { LibcameraNative.lc_camera_release(_camera); } catch { }
                try { LibcameraNative.lc_camera_close(_camera); } catch { }
                _camera = IntPtr.Zero;
            }

            if (_manager != IntPtr.Zero)
            {
                try { LibcameraNative.lc_manager_stop(_manager); } catch { }
                try { LibcameraNative.lc_manager_destroy(_manager); } catch { }
                _manager = IntPtr.Zero;
            }

            _captureStream?.Dispose();
            _captureStream = null;
        }

        GC.SuppressFinalize(this);
    }

    ~VideoCaptureDevice()
    {
        Dispose();
    }

    private void EnsureApplied()
    {
        if (!_isApplied || _reconfigureNeeded)
            Apply();
    }

    private void StartStreamingInternal()
    {
        if (_isStreaming)
            return;

        LibcameraNative.ThrowIfFailed(
            LibcameraNative.lc_camera_start(_camera),
            nameof(LibcameraNative.lc_camera_start));

        // A request that has been through the camera once - it has, on every start after the first - is spent,
        // and queuing it again is refused as invalid. Reusing it clears that and keeps its buffer.
        LibcameraNative.ThrowIfFailed(
            LibcameraNative.lc_request_reuse(_request),
            nameof(LibcameraNative.lc_request_reuse));

        QueueRequestWithCurrentControls();

        _isStreaming = true;
    }

    private void StopStreamingInternal()
    {
        if (!_isStreaming)
            return;

        _stopping = true;
        try
        {
            LibcameraNative.ThrowIfFailed(
                LibcameraNative.lc_camera_stop(_camera),
                nameof(LibcameraNative.lc_camera_stop));
        }
        finally
        {
            _stopping = false;
        }

        _isStreaming = false;
    }

    private void QueueRequestWithCurrentControls()
    {
        ApplyControlsToRequest();
        LibcameraNative.ThrowIfFailed(
            LibcameraNative.lc_camera_queue_request(_camera, _request),
            nameof(LibcameraNative.lc_camera_queue_request));
    }

    private void ApplyControlsToRequest()
    {
        if (_named.Count > 0 || _once.Count > 0)
        {
            ApplyNamedControls();
            return;
        }

        LibcameraNative.ThrowIfFailed(
            LibcameraNative.lc_request_set_control_ae(_request, _desiredConfig.AutoExposure ? 1 : 0),
            nameof(LibcameraNative.lc_request_set_control_ae));

        LibcameraNative.ThrowIfFailed(
            LibcameraNative.lc_request_set_control_awb(_request, _desiredConfig.AutoWhiteBalance ? 1 : 0),
            nameof(LibcameraNative.lc_request_set_control_awb));

        if (!_desiredConfig.AutoExposure && _desiredConfig.ExposureUs > 0)
        {
            LibcameraNative.ThrowIfFailed(
                LibcameraNative.lc_request_set_control_exposure(_request, _desiredConfig.ExposureUs),
                nameof(LibcameraNative.lc_request_set_control_exposure));
        }

        LibcameraNative.ThrowIfFailed(
            LibcameraNative.lc_request_set_control_gain(_request, _desiredConfig.Gain),
            nameof(LibcameraNative.lc_request_set_control_gain));

        LibcameraNative.ThrowIfFailed(
            LibcameraNative.lc_request_set_control_brightness(_request, _desiredConfig.Brightness),
            nameof(LibcameraNative.lc_request_set_control_brightness));

        LibcameraNative.ThrowIfFailed(
            LibcameraNative.lc_request_set_control_contrast(_request, _desiredConfig.Contrast),
            nameof(LibcameraNative.lc_request_set_control_contrast));

        _appliedConfig = _desiredConfig;
        _controlsDirty = false;
    }

    /// <summary>
    /// Every control set by name, all of them each time, and the ones sent once. Sent whole, not only what changed:
    /// a request's controls are cleared when it is reused, and a camera started again starts from its own defaults.
    /// </summary>
    private void ApplyNamedControls()
    {
        string? error = null;
        foreach (var (name, values) in _named.Concat(_once.Select(o => new KeyValuePair<string, double[]>(o.Name, o.Values))))
        {
            if (!_controls.ContainsKey(name))
                continue;
            int rc = LibcameraNative.lc_request_set_control(_request, name, values, values.Length);
            if (rc < 0)
                error = $"{name} ({string.Join(", ", values)}) was refused: {rc}";
        }
        _once.Clear();
        _controlError = error;
        _appliedConfig = _desiredConfig;
        _controlsDirty = false;
    }

    private void ReadMetadata(IntPtr request)
    {
        if (_watched.Length == 0)
            return;
        var found = new Dictionary<string, double[]>(_watched.Length, StringComparer.Ordinal);
        foreach (var name in _watched)
        {
            int n = LibcameraNative.lc_request_get_metadata(request, name, _metadataBuffer, _metadataBuffer.Length);
            if (n > 0)
                found[name] = _metadataBuffer[..n];
        }
        Volatile.Write(ref _metadata, found);
    }

    private void DestroyPipelineHandles()
    {
        if (_request != IntPtr.Zero)
        {
            try { LibcameraNative.lc_request_destroy(_request); } catch { }
            _request = IntPtr.Zero;
        }

        if (_allocator != IntPtr.Zero)
        {
            try { LibcameraNative.lc_allocator_destroy(_allocator); } catch { }
            _allocator = IntPtr.Zero;
        }

        if (_config != IntPtr.Zero)
        {
            try { LibcameraNative.lc_config_destroy(_config); } catch { }
            _config = IntPtr.Zero;
        }

        _bufferHandle = IntPtr.Zero;
        _isApplied = false;
    }

    /// <summary>The next buffer of the ring, which is remade if the size of a frame has changed.</summary>
    private byte[] NextFrameBuffer(int length)
    {
        int count = Math.Max(_frameBufferCount, 2);
        if (_frameBuffers.Length != count || _frameBuffers[0].Length != length)
        {
            _frameBuffers = new byte[count][];
            for (int i = 0; i < count; i++)
                _frameBuffers[i] = new byte[length];
            _frameBufferNext = 0;
        }

        byte[] buffer = _frameBuffers[_frameBufferNext];
        _frameBufferNext = (_frameBufferNext + 1) % count;
        return buffer;
    }

    private void OnRequestCompleted(IntPtr request, IntPtr userData)
    {
        // Waits for _sync, except while the camera is being stopped by whoever holds it: then the frame is let go,
        // so that the stop, which waits for this to return, can finish.
        var held = false;
        try
        {
            while (!(held = Monitor.TryEnter(_sync, 10)))
            {
                if (_stopping)
                    return;
            }

            if (_disposed || !_isStreaming || _stopping)
                return;

            LibcameraNative.ThrowIfFailed(
                LibcameraNative.lc_request_get_frame_info(request, out var frameInfo),
                nameof(LibcameraNative.lc_request_get_frame_info));

            LibcameraNative.ThrowIfFailed(
                LibcameraNative.lc_request_get_plane_data(request, _allocator, 0, out IntPtr data, out UIntPtr length),
                nameof(LibcameraNative.lc_request_get_plane_data));

            // Frames are copied into a ring of buffers that is made once and gone round for ever: a camera
            // at thirty frames a second otherwise leaves tens of megabytes a second for the collector.
            int available = checked((int)length);
            byte[] bytes = NextFrameBuffer(available);
            Marshal.Copy(data, bytes, 0, available);

            _frameCounter++;

            var args = new VideoFrameAvailableEventArgs(
                frameInfo,
                bytes,
                _desiredConfig.FrameSize,
                _desiredConfig.FourCcFormat);

            if (_isPreviewing)
                PreviewFrameAvailable?.Invoke(this, args);

            if (_isCapturing)
            {
                _captureStream?.Write(bytes, 0, bytes.Length);
                CaptureFrameAvailable?.Invoke(this, args);
            }

            if (_stillRequested)
            {
                if (_pendingStillSkipFrames > 0)
                {
                    _pendingStillSkipFrames--;
                }
                else
                {
                    // A still is kept by whoever asked for it, so it cannot be a buffer that will be written over.
                    _pendingStillBytes = (byte[])bytes.Clone();
                    _stillRequested = false;
                    _stillWait.Set();
                }
            }

            ReadMetadata(request);

            LibcameraNative.ThrowIfFailed(
                LibcameraNative.lc_request_reuse(request),
                nameof(LibcameraNative.lc_request_reuse));

            if (_controlsDirty)
                ApplyControlsToRequest();

            LibcameraNative.ThrowIfFailed(
                LibcameraNative.lc_camera_queue_request(_camera, request),
                nameof(LibcameraNative.lc_camera_queue_request));
        }
        catch
        {
            _stillRequested = false;
            _stillWait.Set();
        }
        finally
        {
            if (held)
                Monitor.Exit(_sync);
        }
    }

    private static CameraCapabilities LoadCapabilities(IntPtr camera)
    {
        var sensor = LibcameraNative.GetPixelArraySize(camera);
        uint[] formats = LibcameraNative.GetSupportedPixelFormats(camera);

        var pixelFormats = new List<PixelFormatCapability>(formats.Length);

        foreach (uint fmt in formats)
        {
            string fourCc = LibcameraNative.FourCcToString(fmt);
            var sizes = LibcameraNative.GetSupportedSizes(camera, fmt)
                .Select(s => new FrameSize(s.Width, s.Height))
                .OrderBy(s => s.Width)
                .ThenBy(s => s.Height)
                .ToArray();

            var range = LibcameraNative.GetFormatSizeRange(camera, fmt);

            pixelFormats.Add(new PixelFormatCapability(
                FourCc: fourCc,
                FourCcValue: fmt,
                MinSize: new FrameSize(range.MinWidth, range.MinHeight),
                MaxSize: new FrameSize(range.MaxWidth, range.MaxHeight),
                SupportedSizes: sizes));
        }

        return new CameraCapabilities(
            SensorSize: new FrameSize(sensor.Width, sensor.Height),
            PixelFormats: pixelFormats);
    }

    private static void Cleanup(IntPtr manager, IntPtr camera)
    {
        if (camera != IntPtr.Zero)
        {
            try { LibcameraNative.lc_camera_release(camera); } catch { }
            try { LibcameraNative.lc_camera_close(camera); } catch { }
        }

        if (manager != IntPtr.Zero)
        {
            try { LibcameraNative.lc_manager_stop(manager); } catch { }
            try { LibcameraNative.lc_manager_destroy(manager); } catch { }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VideoCaptureDevice));
    }
}

public sealed record CameraCapabilities(
    FrameSize SensorSize,
    IReadOnlyList<PixelFormatCapability> PixelFormats);

public sealed record PixelFormatCapability(
    string FourCc,
    uint FourCcValue,
    FrameSize MinSize,
    FrameSize MaxSize,
    IReadOnlyList<FrameSize> SupportedSizes);

public readonly record struct FrameSize(uint Width, uint Height)
{
    public override string ToString() => $"{Width}x{Height}";
}

public sealed record VideoCaptureConfiguration
{
    public FrameSize FrameSize { get; init; }
    public string FourCcFormat { get; init; } = "YUYV";

    /// <summary>The sensor mode to take the picture from, by its size; none to let libcamera choose.</summary>
    public FrameSize? SensorSize { get; init; }

    /// <summary>The sensor mode's bit depth, with <see cref="SensorSize"/>.</summary>
    public uint SensorBitDepth { get; init; } = 10;
    public int ExposureUs { get; init; }
    public float Gain { get; init; } = 1.0f;
    public float Brightness { get; init; }
    public float Contrast { get; init; } = 1.0f;
    public bool AutoExposure { get; init; } = true;
    public bool AutoWhiteBalance { get; init; } = true;
    public bool HFlip { get; init; }
    public bool VFlip { get; init; }
}

public sealed class VideoFrameAvailableEventArgs : EventArgs
{
    public LibcameraNative.LcFrameInfo FrameInfo { get; }
    public byte[] Data { get; }
    public FrameSize FrameSize { get; }
    public string FourCcFormat { get; }

    public VideoFrameAvailableEventArgs(
        LibcameraNative.LcFrameInfo frameInfo,
        byte[] data,
        FrameSize frameSize,
        string fourCcFormat)
    {
        FrameInfo = frameInfo;
        Data = data;
        FrameSize = frameSize;
        FourCcFormat = fourCcFormat;
    }
}