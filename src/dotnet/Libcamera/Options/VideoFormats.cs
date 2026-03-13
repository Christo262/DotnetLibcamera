namespace Libcamera;

public static class VideoFormats
{
    public const string YUYV = "YUYV";
    public const string MJPG = "MJPG";
    public const string RGB3 = "RGB3";
    public const string BGR3 = "BGR3";
    public const string NV12 = "NV12";
    public const string YU12 = "YU12";
    public const string YV12 = "YV12";
    public const string GREY = "GREY";

    public static uint ToFourCc(string format)
    {
        if (format == null || format.Length != 4)
            throw new ArgumentException("FourCC format must be 4 characters.", nameof(format));

        return (uint)format[0]
             | ((uint)format[1] << 8)
             | ((uint)format[2] << 16)
             | ((uint)format[3] << 24);
    }

    public static string FromFourCc(uint fourCc)
    {
        char a = (char)(fourCc & 0xFF);
        char b = (char)((fourCc >> 8) & 0xFF);
        char c = (char)((fourCc >> 16) & 0xFF);
        char d = (char)((fourCc >> 24) & 0xFF);

        return new string(new[] { a, b, c, d });
    }
}