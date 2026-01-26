namespace EsmAnalyzer.Core;

/// <summary>
///     Simple RGBA color struct (replaces ImageSharp dependency).
/// </summary>
public readonly record struct Rgba32(byte R, byte G, byte B, byte A)
{
    public static Rgba32 Black => new(0, 0, 0, 255);
    public static Rgba32 White => new(255, 255, 255, 255);
    public static Rgba32 Transparent => new(0, 0, 0, 0);
}