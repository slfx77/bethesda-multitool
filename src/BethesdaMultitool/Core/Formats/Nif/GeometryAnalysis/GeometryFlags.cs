namespace BethesdaMultitool.Core.Formats.Nif.GeometryAnalysis;

/// <summary>
///     Groups geometry flags for conversion methods.
/// </summary>
internal readonly record struct GeometryFlags(
    byte OrigHasNormals,
    byte NewHasNormals,
    ushort OrigBsDataFlags,
    ushort NewBsDataFlags);
