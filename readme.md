# libcamera .NET Wrapper for Raspberry Pi

A lightweight **.NET wrapper for libcamera** designed to make Raspberry Pi camera access simple for .NET developers.

libcamera replaced the legacy V4L2 camera stack on Raspberry Pi, but most tooling around it is still focused on **C++ and Python**. This project demonstrates how to build a **clean .NET PInvoke wrapper** around libcamera using a small **ABI-safe C layer**.

The goal of this project is to provide a **working reference implementation** that developers can use as a starting point for their own camera integrations.

---

# Project Architecture

libcamera is a C++ library, which means it cannot be directly consumed by .NET.
This project solves that by introducing a thin C wrapper layer.

Architecture:

libcamera
↓
C wrapper (ABI safe)
↓
.NET PInvoke layer
↓
VideoCaptureDevice API
↓
Consumer application

This design keeps the native boundary **stable and minimal**, making the .NET layer easy to extend.

## Testing
Testing was done using libcamera v0.7 on a Raspberry Pi 5.

## Building the wrapper

The wrapper must be built against the Pi's own libcamera, and rebuilt whenever Raspberry Pi's libcamera changes what it exports - not only on a new version: their October 2026 build of 0.7.2 dropped `properties::PixelArraySize`, and a wrapper built before it no longer loads ("undefined symbol: _ZN9libcamera10properties14PixelArraySizeE"). On the Pi, with `libcamera-dev` installed:

```bash
g++ -shared -fPIC -O2 -std=c++20 libcamera_wrapper.cpp -o liblibcamera_wrapper.so $(pkg-config --cflags --libs libcamera)
```

C++20: libcamera's headers have needed it since that build.

---

# Features

Current capabilities include:

• Camera discovery and initialization
• Camera capability enumeration
• Preview streaming
• Manual exposure control
• Gain control
• Contrast and brightness control
• Raw frame access
• Still image capture
• Blazor UI integration example
• Image conversion example using ImageSharp
• Sensor mode selection (the whole sensor, binned, instead of a crop of its middle)
• Every control the camera has, listed with its range, default and choices, and set by name
• Per-frame metadata: the exposure, gain, colour gains, colour temperature, lux and lens position each frame was taken with
• One-shot controls (AfTrigger, to focus now)
• Horizontal and vertical flip
• A fixed ring of frame buffers, so streaming leaves nothing for the garbage collector

---

# Basic Usage

Create a camera device:

VideoCaptureDevice.TryCreate(out var device, out var error);

Configure camera parameters:

device.SetCapability(size, VideoFormats.YUYV);
device.SetExposure(20000);
device.SetGain(2.0f);
device.SetAutoExposure(false);

Apply the configuration:

device.Apply();

Start preview streaming:

device.StartPreview();

Subscribe to preview frames:

device.PreviewFrameAvailable += frameHandler;

Stop preview when finished:

device.StopPreview();

---

# Preview Frames

Preview frames are delivered as **raw camera buffers**.

Most Raspberry Pi camera configurations will return frames in **YUYV format**.

Applications can process the raw frame data however they choose. This repository includes a simple example that converts YUYV frames into JPEG images using **SixLabors.ImageSharp**.

---

# Video Capture

Video capture is intentionally **raw frame only**.

This library does not attempt to provide video encoding. Applications are expected to encode frames themselves using tools such as:

• FFmpeg
• hardware encoders
• custom pipelines

This keeps the wrapper focused on **camera access rather than media processing**.

---

# Sensor Mode

By default libcamera takes the picture from the sensor mode nearest its size, which for a small picture can be a crop of the middle of the sensor (640x480 on the IMX219 is its middle 1280x960, about a third of the view). Choose the mode to get the whole sensor, binned and scaled down:

```csharp
device.SetCapability(new FrameSize(640, 480), VideoFormats.YUYV);
device.SetSensorMode(new FrameSize(1640, 1232));
device.Apply();
```

---

# Any Control, By Name

`device.Controls` lists every control the camera takes, by its libcamera name, with its range, its default and, for a choice, the names of the values it allows. Some ranges depend on the sensor mode, so they are read again at each `Apply()`.

```csharp
foreach (var c in device.Controls.Values)
    Console.WriteLine($"{c.Name}: {string.Join(",", c.Min)} to {string.Join(",", c.Max)}");

device.SetControls(new Dictionary<string, double[]>
{
    ["AeEnable"] = [0],
    ["ExposureTime"] = [10000],
    ["AnalogueGain"] = [2],
    ["AwbMode"] = [device.Controls["AwbMode"].Choices["AwbDaylight"]],
    ["ScalerCrop"] = [x, y, width, height],
    ["FrameDurationLimits"] = [33333, 33333],
});

device.Trigger("AfTrigger", 0);   // sent once, with the next frame
```

Values are doubles: one for a number, a flag or a choice, two for a pair (`ColourGains`, `FrameDurationLimits`) or a size, four for a rectangle. `SetControls` replaces every control set by name before; once any is set, the plain setters below are no longer sent, so the two never fight. A control the camera refuses is reported in `device.ControlError`.

What each frame was taken with:

```csharp
device.WatchMetadata("ExposureTime", "AnalogueGain", "ColourGains", "ColourTemperature", "Lux", "LensPosition", "AfState");
var exposure = device.Metadata["ExposureTime"][0];
```

Flipping the picture reconfigures the camera, so it takes an `Apply()`:

```csharp
device.SetOrientation(hflip: true, vflip: true);   // half a turn
device.Apply();
```

---

# Threads

Frames arrive on libcamera's own thread, and `PreviewFrameAvailable` is raised while the device holds its lock. Calls that change the camera (`SetControls`, `Trigger`, `SetOrientation`, `Apply`, starting and stopping) take that same lock. So never make one of those calls while holding a lock of your own that your frame handler also takes: each would wait for the other for good. Take what you need under your lock, let it go, then call the device. `Controls`, `ControlError` and `Metadata` take no lock and are safe to read anywhere, a frame handler included.

---

# Camera Controls

Typical control ranges:

Exposure
100 µs → ~1,000,000 µs

Examples

10000 = 10 ms
20000 = 20 ms
33000 ≈ 30 FPS frame duration

Gain
1.0 → ~16.0

Brightness
-1.0 → +1.0

Contrast
0.0 → ~2.0

Manual exposure requires disabling auto exposure:

device.SetAutoExposure(false)

---

# Configuration Model

Camera configuration is **mutable**, then applied using Apply().

Resolution or pixel format changes trigger a **pipeline rebuild**.

Exposure, gain, brightness, and contrast changes are applied **live without restarting streaming**.

This behavior mirrors how libcamera pipelines are designed to operate.

---

# Project Status

This project is a **working prototype and reference implementation**.

Implemented:

• Camera initialization
• Capability discovery
• Preview streaming
• Exposure and gain controls
• Raw frame access
• Image conversion example
• Blazor integration sample

Remaining work includes:

• documentation improvements
• additional examples
• code cleanup

---

# Project Goals

This project aims to:

• provide a clear example of wrapping libcamera for .NET
• demonstrate safe PInvoke patterns
• give .NET developers a starting point for libcamera projects

---

# Maintenance

This repository is released primarily as a **starter/reference project**.

It may receive occasional improvements, but it **might not become a fully maintained camera framework**.

Developers are encouraged to copy, adapt, and extend the code for their own applications.

---

# License

MIT License
