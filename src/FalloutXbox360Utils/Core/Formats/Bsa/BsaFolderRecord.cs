namespace FalloutXbox360Utils.Core.Formats.Bsa;

/// <summary>
///     BSA folder record structure.
/// </summary>
public record BsaFolderRecord
{
    /// <summary>64-bit hash of folder path.</summary>
    public required ulong NameHash { get; init; }

    /// <summary>Number of files in this folder.</summary>
    public required uint FileCount { get; init; }

    /// <summary>Offset to file records for this folder.</summary>
    public required uint Offset { get; init; }

    /// <summary>Folder name (populated during parsing).</summary>
    public string? Name { get; set; }

    /// <summary>Files in this folder (populated during parsing).</summary>
    public List<BsaFileRecord> Files { get; } = [];
}
