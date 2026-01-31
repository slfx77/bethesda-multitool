namespace FalloutXbox360Utils.Core.Formats.Nif.Geometry;

/// <summary>
///     Groups geometry flags for conversion methods.
/// </summary>
internal readonly record struct GeometryFlags(
    byte OrigHasNormals,
    byte NewHasNormals,
    ushort OrigBsDataFlags,
    ushort NewBsDataFlags);
