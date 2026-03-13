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

    private bool _reconfigureNeeded = true;
    private bool _controlsDirty = true;

    private LibcameraNative.RequestCompletedCallback? _callback;

    private readonly ManualResetEventSlim _stillWait = new(false);
    private byte[]? _pendingStillBytes;
    private int _pendingStillSkipFrames;
    private bool _stillRequested;

    private int _frameCounter;
    private MemoryStream? _captureStream;
    private byte[]? _latestCaptureBytes;

    private VideoCaptureConfiguration _desiredConfig;
    private VideoCaptureConfiguration _appliedConfig;

    public string CameraId { get; }
    public string Model { get; }
    public CameraCapabilities Capabilities { get; }

    public bool IsInitialized => _camera != IntPtr.Zero;
    public bool IsApplied => _isApplied;
    public bool IsPreviewing => _isPreviewing;
    public bool IsCapturing => _isCapturing;
    public bool IsStreaming => _isStreaming;

    public VideoCaptureConfiguration DesiredConfiguration => _desiredConfig;
    public VideoCaptureConfiguration AppliedConfiguration => _appliedConfig;

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

                int validateStatus = LibcameraNative.lc_config_validate(_config);
                LibcameraNative.ThrowIfFailed(validateStatus >= 0 ? 0 : validateStatus, nameof(LibcameraNative.lc_config_validate));

                LibcameraNative.ThrowIfFailed(
                    LibcameraNative.lc_camera_configure(_camera, _config),
                    nameof(LibcameraNative.lc_camera_configure));

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

        QueueRequestWithCurrentControls();

        _isStreaming = true;
    }

    private void StopStreamingInternal()
    {
        if (!_isStreaming)
            return;

        LibcameraNative.ThrowIfFailed(
            LibcameraNative.lc_camera_stop(_camera),
            nameof(LibcameraNative.lc_camera_stop));

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

    private void OnRequestCompleted(IntPtr request, IntPtr userData)
    {
        try
        {
            lock (_sync)
            {
                if (_disposed || !_isStreaming)
                    return;

                LibcameraNative.ThrowIfFailed(
                    LibcameraNative.lc_request_get_frame_info(request, out var frameInfo),
                    nameof(LibcameraNative.lc_request_get_frame_info));

                LibcameraNative.ThrowIfFailed(
                    LibcameraNative.lc_request_get_plane_data(request, _allocator, 0, out IntPtr data, out UIntPtr length),
                    nameof(LibcameraNative.lc_request_get_plane_data));

                int available = checked((int)length);
                byte[] bytes = new byte[available];
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
                        _pendingStillBytes = bytes;
                        _stillRequested = false;
                        _stillWait.Set();
                    }
                }

                LibcameraNative.ThrowIfFailed(
                    LibcameraNative.lc_request_reuse(request),
                    nameof(LibcameraNative.lc_request_reuse));

                if (_controlsDirty)
                    ApplyControlsToRequest();

                LibcameraNative.ThrowIfFailed(
                    LibcameraNative.lc_camera_queue_request(_camera, request),
                    nameof(LibcameraNative.lc_camera_queue_request));
            }
        }
        catch
        {
            _stillRequested = false;
            _stillWait.Set();
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
    public int ExposureUs { get; init; }
    public float Gain { get; init; } = 1.0f;
    public float Brightness { get; init; }
    public float Contrast { get; init; } = 1.0f;
    public bool AutoExposure { get; init; } = true;
    public bool AutoWhiteBalance { get; init; } = true;
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