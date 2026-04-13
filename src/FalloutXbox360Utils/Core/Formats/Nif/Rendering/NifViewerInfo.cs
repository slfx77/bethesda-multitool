namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Info about a parsed NIF file for display in the viewer.
/// </summary>
internal sealed class NifViewerInfo
{
    public required string FileName { get; init; }
    public required int BlockCount { get; init; }
    public required string Format { get; init; }
    public required uint BsVersion { get; init; }
    public required uint UserVersion { get; init; }
    public required IReadOnlyList<string> BlockTypeNames { get; init; }
    public required long FileSize { get; init; }
}
