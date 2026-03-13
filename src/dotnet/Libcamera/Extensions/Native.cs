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