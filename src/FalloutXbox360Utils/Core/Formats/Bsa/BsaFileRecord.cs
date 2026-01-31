namespace FalloutXbox360Utils.Core.Formats.Bsa;

/// <summary>
///     BSA file record structure.
/// </summary>
public record BsaFileRecord
{
    /// <summary>64-bit hash of file name.</summary>
    public required ulong NameHash { get; init; }

    /// <summary>File size (bit 30 toggles compression from default).</summary>
    public required uint RawSize { get; init; }

    /// <summary>Offset to file data from start of archive.</summary>
    public required uint Offset { get; init; }

    /// <summary>File name (populated during parsing).</summary>
    public string? Name { get; set; }

    /// <summary>Parent folder (populated during parsing).</summary>
    public BsaFolderRecord? Folder { get; set; }

    /// <summary>Actual file size (without compression toggle bit).</summary>
    public uint Size => RawSize & 0x3FFFFFFF;

    /// <summary>Whether compression is toggled from archive default.</summary>
    public bool CompressionToggle => (RawSize & 0x40000000) != 0;

    /// <summary>Full path (folder + filename).</summary>
    public string FullPath => Folder?.Name is not null && Name is not null
        ? $"{Folder.Name}\\{Name}"
        : Name ?? $"unknown_{NameHash:X16}";
}
