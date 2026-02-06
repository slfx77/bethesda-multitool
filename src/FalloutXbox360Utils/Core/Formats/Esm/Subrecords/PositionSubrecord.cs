namespace FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

/// <summary>
///     DATA subrecord with position and rotation.
///     24 bytes: 3 floats (position) + 3 floats (rotation)
/// </summary>
public record PositionSubrecord(
    float X,
    float Y,
    float Z,
    float RotX,
    float RotY,
    float RotZ,
    long Offset,
    bool IsBigEndian);
