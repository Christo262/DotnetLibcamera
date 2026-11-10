namespace Libcamera.Models;

/// <summary>The type of a control's value, numbered as libcamera numbers them.</summary>
public enum ControlType
{
    None = 0,
    Bool = 1,
    Byte = 2,
    Int32 = 3,
    Int64 = 4,
    Float = 5,
    String = 6,
    Rectangle = 7,
    Size = 8,
}

/// <summary>
/// A control the camera takes, as libcamera describes it: <c>AnalogueGain</c>, <c>AfMode</c>, <c>ScalerCrop</c>.
/// Values are doubles: one for a number, a flag or a choice, two for a size or a pair, four for a rectangle
/// (x, y, width, height). <see cref="Min"/>, <see cref="Max"/> and <see cref="Default"/> are empty when the camera
/// does not say. Some of them depend on the sensor mode, so they are read again each time the camera is configured.
/// </summary>
/// <param name="Choices">For a control that is a choice, the name of each value it allows: AfMode's <c>AfModeAuto</c> is 1.</param>
public sealed record CameraControl(
    string Name,
    ControlType Type,
    bool IsArray,
    double[] Min,
    double[] Max,
    double[] Default,
    IReadOnlyDictionary<string, int> Choices);
