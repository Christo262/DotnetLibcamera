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
