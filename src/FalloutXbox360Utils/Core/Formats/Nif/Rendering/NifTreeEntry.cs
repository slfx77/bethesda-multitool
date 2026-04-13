namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Entry in the NIF file tree (either a directory/folder or a NIF file).
/// </summary>
internal sealed class NifTreeEntry
{
    public required string DisplayName { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }
    public List<NifTreeEntry> Children { get; } = [];
}
