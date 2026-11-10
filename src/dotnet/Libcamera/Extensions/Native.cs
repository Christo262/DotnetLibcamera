using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Libcamera;

public static class LibcameraNative
{
    private const string DllName = "libcamera_wrapper";

    [StructLayout(LayoutKind.Sequential)]
    public struct LcFrameInfo
    {
        public uint Width;
        public uint Height;
        public uint Stride;
        public uint PixelFormat;
        public ulong TimestampUs;
        public uint Sequence;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void RequestCompletedCallback(IntPtr request, IntPtr userData);

    // --- Manager ---

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr lc_manager_create();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void lc_manager_destroy(IntPtr manager);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_manager_start(IntPtr manager);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_manager_stop(IntPtr manager);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_manager_camera_count(IntPtr manager);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lc_manager_get_camera_id(
        IntPtr manager,
        int index,
        byte[] buffer,
        UIntPtr bufferSize);

    // --- Camera lifecycle ---

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr lc_camera_open(IntPtr manager, string cameraId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void lc_camera_close(IntPtr camera);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_camera_acquire(IntPtr camera);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_camera_release(IntPtr camera);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_camera_start(IntPtr camera);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_camera_stop(IntPtr camera);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_camera_queue_request(IntPtr camera, IntPtr request);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_camera_set_request_completed_callback(
        IntPtr camera,
        RequestCompletedCallback callback,
        IntPtr userData);

    // --- Camera capabilities / properties ---

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lc_camera_get_model(
        IntPtr camera,
        byte[] buffer,
        UIntPtr bufferSize);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_camera_get_pixel_array_size(
        IntPtr camera,
        out uint width,
        out uint height);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_camera_get_supported_pixel_format_count(IntPtr camera);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_camera_get_supported_pixel_format(
        IntPtr camera,
        int index,
        out uint fourcc);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_camera_get_supported_size_count(
        IntPtr camera,
        uint fourcc);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_camera_get_supported_size(
        IntPtr camera,
        uint fourcc,
        int index,
        out uint width,
        out uint height);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_camera_get_format_size_range(
        IntPtr camera,
        uint fourcc,
        out uint minWidth,
        out uint minHeight,
        out uint maxWidth,
        out uint maxHeight);

    // --- Configuration ---

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr lc_camera_generate_configuration(IntPtr camera);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void lc_config_destroy(IntPtr config);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_config_set_size(IntPtr config, uint width, uint height);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_config_set_pixel_format(IntPtr config, uint fourcc);

    /// <summary>
    /// The sensor mode to take the picture from, by its output size and bit depth. Without it libcamera picks the mode
    /// nearest the picture's size, which for a small picture can be a crop of the middle of the sensor.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_config_set_sensor_mode(IntPtr config, uint width, uint height, uint bitDepth);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_config_validate(IntPtr config);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_camera_configure(IntPtr camera, IntPtr config);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_config_get_stride(IntPtr config, out uint stride);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_config_get_frame_size(IntPtr config, out uint frameSize);

    // --- Buffer allocator ---

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr lc_allocator_create(IntPtr camera);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void lc_allocator_destroy(IntPtr allocator);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_allocator_allocate(IntPtr allocator);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_allocator_buffer_count(IntPtr allocator);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_allocator_get_buffer(
        IntPtr allocator,
        int index,
        out IntPtr bufferHandle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_allocator_get_buffer_plane_info(
        IntPtr allocator,
        IntPtr bufferHandle,
        int planeIndex,
        out IntPtr data,
        out UIntPtr length);

    // --- Request ---

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr lc_request_create(IntPtr camera);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void lc_request_destroy(IntPtr request);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_request_attach_buffer(IntPtr request, IntPtr bufferHandle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_request_reuse(IntPtr request);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_request_get_frame_info(IntPtr request, out LcFrameInfo info);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_request_get_plane_data(
        IntPtr request,
        IntPtr allocator,
        int planeIndex,
        out IntPtr data,
        out UIntPtr length);

    // --- Controls ---

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_request_set_control_exposure(IntPtr request, int exposureUs);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_request_set_control_gain(IntPtr request, float gain);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_request_set_control_brightness(IntPtr request, float brightness);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_request_set_control_contrast(IntPtr request, float contrast);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_request_set_control_awb(IntPtr request, int enable);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_request_set_control_ae(IntPtr request, int enable);

    // --- Any control, by its libcamera name ---

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_camera_control_count(IntPtr camera);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lc_camera_control_name(IntPtr camera, int index, byte[] buffer, UIntPtr bufferSize);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_control_type(string name, out int type, out int isArray);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_camera_control_limit(IntPtr camera, string name, int which, double[] values, int capacity);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_camera_control_value_count(IntPtr camera, string name);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_camera_control_value(IntPtr camera, string name, int index, out double value);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_control_enum_count(string name);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int lc_control_enum(string name, int index, out int value, byte[] buffer, UIntPtr bufferSize);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_request_set_control(IntPtr request, string name, double[] values, int count);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_request_get_metadata(IntPtr request, string name, double[] values, int capacity);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lc_config_set_orientation(IntPtr config, int hflip, int vflip);

    /// <summary>
    /// Every control the camera takes, with its range, what it starts at, and for a choice the names of the values
    /// it allows. Empty from a wrapper built before controls could be listed.
    /// </summary>
    public static IReadOnlyDictionary<string, Models.CameraControl> GetControls(IntPtr camera)
    {
        var found = new Dictionary<string, Models.CameraControl>(StringComparer.Ordinal);
        int count;
        try
        {
            count = lc_camera_control_count(camera);
        }
        catch (EntryPointNotFoundException)
        {
            return found;
        }

        var buffer = new byte[128];
        var values = new double[16];
        for (int i = 0; i < count; i++)
        {
            if (lc_camera_control_name(camera, i, buffer, (UIntPtr)buffer.Length) < 0)
                continue;
            string name = Text(buffer);
            if (lc_control_type(name, out int type, out int isArray) < 0)
                continue;

            double[] Limit(int which)
            {
                int n = lc_camera_control_limit(camera, name, which, values, values.Length);
                return n > 0 ? values[..n] : [];
            }

            // A choice's names, kept to the values the camera allows when it says which those are.
            var allowed = new HashSet<int>();
            int allowedCount = lc_camera_control_value_count(camera, name);
            for (int j = 0; j < allowedCount; j++)
                if (lc_camera_control_value(camera, name, j, out double value) > 0)
                    allowed.Add((int)value);
            var choices = new Dictionary<string, int>(StringComparer.Ordinal);
            int enums = lc_control_enum_count(name);
            for (int j = 0; j < enums; j++)
                if (lc_control_enum(name, j, out int value, buffer, (UIntPtr)buffer.Length) >= 0 && (allowed.Count == 0 || allowed.Contains(value)))
                    choices[Text(buffer)] = value;

            found[name] = new Models.CameraControl(name, (Models.ControlType)type, isArray != 0, Limit(0), Limit(1), Limit(2), choices);
        }
        return found;
    }

    private static string Text(byte[] buffer)
    {
        int nullIndex = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, nullIndex >= 0 ? nullIndex : buffer.Length);
    }

    // --- Helpers ---

    public static string GetCameraId(IntPtr manager, int index, int bufferSize = 256)
    {
        var buffer = new byte[bufferSize];
        int rc = lc_manager_get_camera_id(manager, index, buffer, (UIntPtr)buffer.Length);
        ThrowIfFailed(rc, nameof(lc_manager_get_camera_id));

        int nullIndex = Array.IndexOf(buffer, (byte)0);
        int len = nullIndex >= 0 ? nullIndex : buffer.Length;
        return Encoding.UTF8.GetString(buffer, 0, len);
    }

    public static string GetCameraModel(IntPtr camera, int bufferSize = 256)
    {
        var buffer = new byte[bufferSize];
        int rc = lc_camera_get_model(camera, buffer, (UIntPtr)buffer.Length);
        ThrowIfFailed(rc, nameof(lc_camera_get_model));

        int nullIndex = Array.IndexOf(buffer, (byte)0);
        int len = nullIndex >= 0 ? nullIndex : buffer.Length;
        return Encoding.UTF8.GetString(buffer, 0, len);
    }

    public static (uint Width, uint Height) GetPixelArraySize(IntPtr camera)
    {
        int rc = lc_camera_get_pixel_array_size(camera, out uint width, out uint height);
        ThrowIfFailed(rc, nameof(lc_camera_get_pixel_array_size));
        return (width, height);
    }

    public static uint[] GetSupportedPixelFormats(IntPtr camera)
    {
        int count = lc_camera_get_supported_pixel_format_count(camera);
        ThrowIfFailed(count >= 0 ? 0 : count, nameof(lc_camera_get_supported_pixel_format_count));

        var formats = new uint[count];
        for (int i = 0; i < count; i++)
        {
            int rc = lc_camera_get_supported_pixel_format(camera, i, out uint fourcc);
            ThrowIfFailed(rc, nameof(lc_camera_get_supported_pixel_format));
            formats[i] = fourcc;
        }

        return formats;
    }

    public static (uint Width, uint Height)[] GetSupportedSizes(IntPtr camera, uint fourcc)
    {
        int count = lc_camera_get_supported_size_count(camera, fourcc);
        ThrowIfFailed(count >= 0 ? 0 : count, nameof(lc_camera_get_supported_size_count));

        var sizes = new (uint Width, uint Height)[count];
        for (int i = 0; i < count; i++)
        {
            int rc = lc_camera_get_supported_size(camera, fourcc, i, out uint width, out uint height);
            ThrowIfFailed(rc, nameof(lc_camera_get_supported_size));
            sizes[i] = (width, height);
        }

        return sizes;
    }

    public static (uint MinWidth, uint MinHeight, uint MaxWidth, uint MaxHeight) GetFormatSizeRange(
        IntPtr camera,
        uint fourcc)
    {
        int rc = lc_camera_get_format_size_range(
            camera,
            fourcc,
            out uint minWidth,
            out uint minHeight,
            out uint maxWidth,
            out uint maxHeight);

        ThrowIfFailed(rc, nameof(lc_camera_get_format_size_range));
        return (minWidth, minHeight, maxWidth, maxHeight);
    }

    public static string FourCcToString(uint fourcc)
    {
        Span<char> chars = stackalloc char[4];
        chars[0] = (char)(fourcc & 0xFF);
        chars[1] = (char)((fourcc >> 8) & 0xFF);
        chars[2] = (char)((fourcc >> 16) & 0xFF);
        chars[3] = (char)((fourcc >> 24) & 0xFF);
        return new string(chars);
    }

    public static uint FourCc(string s)
    {
        if (s is null || s.Length != 4)
            throw new ArgumentException("FourCC must be exactly 4 characters.", nameof(s));

        return (uint)s[0]
             | ((uint)s[1] << 8)
             | ((uint)s[2] << 16)
             | ((uint)s[3] << 24);
    }

    public static (IntPtr Data, nuint Length) GetAllocatorPlaneInfo(
        IntPtr allocator,
        IntPtr bufferHandle,
        int planeIndex)
    {
        int rc = lc_allocator_get_buffer_plane_info(
            allocator,
            bufferHandle,
            planeIndex,
            out var data,
            out var length);

        ThrowIfFailed(rc, nameof(lc_allocator_get_buffer_plane_info));
        return (data, (nuint)length);
    }

    public static (IntPtr Data, nuint Length) GetRequestPlaneData(
        IntPtr request,
        IntPtr allocator,
        int planeIndex)
    {
        int rc = lc_request_get_plane_data(
            request,
            allocator,
            planeIndex,
            out var data,
            out var length);

        ThrowIfFailed(rc, nameof(lc_request_get_plane_data));
        return (data, (nuint)length);
    }

    public static void ThrowIfFailed(int status, string operation)
    {
        if (status < 0)
            throw new InvalidOperationException($"{operation} failed with status {status}");
    }

    public static void ThrowIfNull(IntPtr ptr, string operation)
    {
        if (ptr == IntPtr.Zero)
            throw new InvalidOperationException($"{operation} returned null");
    }
}