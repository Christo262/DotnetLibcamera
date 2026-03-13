using Libcamera;
using Libcamera.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using System.Drawing;

Console.WriteLine("Hello, World!");

if(!VideoCaptureDevice.TryCreate(out var device, out string? error))
{
    Console.WriteLine($"ERROR: {error}");
    return;
}

using (device)
{

    Console.WriteLine($"Sensor: {device!.Capabilities.SensorSize.Width} x {device.Capabilities.SensorSize.Height}");
    Console.WriteLine("Pixel Formats:");
    foreach(var item in device.Capabilities.PixelFormats)
    {
        Console.WriteLine($"\tFormat: {item.FourCc}");
        foreach(var fs in item.SupportedSizes)
        {
            Console.WriteLine($"\t\t{fs.Width} x {fs.Height}");
        }
    }

    var frameSize = new FrameSize(640, 480);
    device.SetAutoExposure(true);
    device.SetAutoWhiteBalance(true);
    device.SetCapability(frameSize, VideoFormats.YUYV);

    Console.WriteLine("Camera configured:");
    Console.WriteLine("\t Exposure: Auto");
    Console.WriteLine("\t White Balance: Auto");
    Console.WriteLine("\t Frame Size: 640 x 480");



    byte[] yuyv_image = device.CaptureStillBytes();
    byte[] jpeg = ConvertYuyvToJpeg(
                    yuyv_image,
                    (int)frameSize.Width,
                    (int)frameSize.Height);

    File.WriteAllBytes("./output.jpg", jpeg);
}


static byte[] ConvertYuyvToJpeg(byte[] yuyv, int width, int height)
{
    int expected = width * height * 2;
    if (yuyv.Length < expected)
        throw new InvalidOperationException(
            $"YUYV buffer too small. Got {yuyv.Length}, expected at least {expected}");

    using var image = new Image<Rgb24>(width, height);

    image.ProcessPixelRows(accessor =>
    {
        for (int y = 0; y < accessor.Height; y++)
        {
            Span<Rgb24> row = accessor.GetRowSpan(y);
            int rowOffset = y * width * 2;

            for (int x = 0; x < width; x += 2)
            {
                int i = rowOffset + x * 2;

                int y0 = yuyv[i + 0];
                int u = yuyv[i + 1];
                int y1 = yuyv[i + 2];
                int v = yuyv[i + 3];

                row[x] = YuvToRgb(y0, u, v);

                if (x + 1 < width)
                    row[x + 1] = YuvToRgb(y1, u, v);
            }
        }
    });

    using var ms = new MemoryStream();
    image.SaveAsJpeg(ms, new JpegEncoder { Quality = 90 });
    return ms.ToArray();
}

static Rgb24 YuvToRgb(int y, int u, int v)
{
    int c = y - 16;
    int d = u - 128;
    int e = v - 128;

    if (c < 0)
        c = 0;

    int r = (298 * c + 409 * e + 128) >> 8;
    int g = (298 * c - 100 * d - 208 * e + 128) >> 8;
    int b = (298 * c + 516 * d + 128) >> 8;

    return new Rgb24(
        (byte)ClampToByte(r),
        (byte)ClampToByte(g),
        (byte)ClampToByte(b));
}

static int ClampToByte(int value)
{
    if (value < 0) return 0;
    if (value > 255) return 255;
    return value;
}