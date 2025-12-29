namespace Xbox360MemoryCarver.Core.FileTypes;

/// <summary>
///     Category for grouping file types in the UI.
/// </summary>
public enum FileCategory
{
    Texture,
    Image,
    Audio,
    Model,
    Module,
    Script,
    Xbox,
    Plugin
}

/// <summary>
///     Extraction status for a carved file instance.
/// </summary>
public enum CarveStatus
{
    NotExtracted,
    Extracted,
    Failed,
    Skipped
}

/// <summary>
///     Defines a file type signature (magic bytes) and variant information.
/// </summary>
public sealed class FileSignature
{
    /// <summary>
    ///     Unique identifier for this signature (e.g., "ddx_3xdo", "xui_scene").
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     Magic bytes to match at the start of the file.
    /// </summary>
    public required byte[] MagicBytes { get; init; }

    /// <summary>
    ///     Human-readable description of this specific variant.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    ///     Function to generate a display description for the file table.
    ///     Takes optional metadata and returns the description string.
    /// </summary>
    public Func<FileInstance?, string>? DisplayDescriptionFunc { get; init; }

    /// <summary>
    ///     Gets the display description, using the function if provided, otherwise the static description.
    /// </summary>
    public string GetDisplayDescription(FileInstance? instance = null)
    {
        return DisplayDescriptionFunc?.Invoke(instance) ?? Description;
    }
}

/// <summary>
///     Base class defining a file type that can be carved from memory dumps.
///     Each supported file type should have one definition registered in the FileTypeRegistry.
/// </summary>
public class FileTypeDefinition
{
    /// <summary>
    ///     Unique identifier for this file type (e.g., "ddx", "xma", "xex").
    /// </summary>
    public required string TypeId { get; init; }

    /// <summary>
    ///     Display name for UI (e.g., "DDX", "XMA Audio", "Module").
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    ///     File extension including the dot (e.g., ".ddx", ".xma", ".xex").
    /// </summary>
    public required string Extension { get; init; }

    /// <summary>
    ///     Category for grouping and coloring in the hex viewer.
    /// </summary>
    public required FileCategory Category { get; init; }

    /// <summary>
    ///     Output folder name for carved files of this type.
    /// </summary>
    public string OutputFolder { get; init; } = "";

    /// <summary>
    ///     Minimum valid file size in bytes.
    /// </summary>
    public int MinSize { get; init; } = 16;

    /// <summary>
    ///     Maximum valid file size in bytes.
    /// </summary>
    public int MaxSize { get; init; } = 64 * 1024 * 1024;

    /// <summary>
    ///     All signatures (magic byte patterns) that identify this file type.
    ///     A file type may have multiple signatures for different variants.
    /// </summary>
    public required IReadOnlyList<FileSignature> Signatures { get; init; }

    /// <summary>
    ///     Parser type to use for analyzing files of this type.
    ///     Null if no parser is available.
    /// </summary>
    public Type? ParserType { get; init; }

    /// <summary>
    ///     Priority for overlap resolution in hex viewer. Lower = higher priority.
    ///     Textures/Audio = 1, Models = 2, Scripts/Xbox = 3, Modules = 4.
    /// </summary>
    public int DisplayPriority { get; init; } = 5;

    /// <summary>
    ///     Whether this file type should be shown in the extraction filter UI.
    /// </summary>
    public bool ShowInFilterUI { get; init; } = true;

    /// <summary>
    ///     Gets all signature IDs for this file type.
    /// </summary>
    public IEnumerable<string> SignatureIds => Signatures.Select(s => s.Id);

    /// <summary>
    ///     Gets a signature by ID.
    /// </summary>
    public FileSignature? GetSignature(string signatureId)
    {
        return Signatures.FirstOrDefault(s => s.Id.Equals(signatureId, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
///     Represents a specific instance of a file found in a memory dump.
///     Contains the file's location, metadata, and extraction status.
/// </summary>
public class FileInstance
{
    /// <summary>
    ///     Reference to the file type definition.
    /// </summary>
    public required FileTypeDefinition TypeDefinition { get; init; }

    /// <summary>
    ///     The specific signature that matched this file.
    /// </summary>
    public required FileSignature MatchedSignature { get; init; }

    /// <summary>
    ///     Byte offset in the source dump file.
    /// </summary>
    public required long Offset { get; init; }

    /// <summary>
    ///     Length of the file in bytes.
    /// </summary>
    public required long Length { get; init; }

    /// <summary>
    ///     Optional filename extracted from file metadata or path references.
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>
    ///     Current extraction status.
    /// </summary>
    public CarveStatus Status { get; set; } = CarveStatus.NotExtracted;

    /// <summary>
    ///     Path where the file was extracted to (if extracted).
    /// </summary>
    public string? ExtractedPath { get; set; }

    /// <summary>
    ///     Error message if extraction failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    ///     Additional metadata from parsing (dimensions, format, etc.).
    /// </summary>
    public Dictionary<string, object> Metadata { get; } = [];

    /// <summary>
    ///     Gets the display name (filename if available, otherwise type + offset).
    /// </summary>
    public string DisplayName => !string.IsNullOrEmpty(FileName)
        ? FileName
        : $"{TypeDefinition.DisplayName}_{Offset:X8}";

    /// <summary>
    ///     Gets the file type description for display in the file table.
    /// </summary>
    public string FileTypeDescription => MatchedSignature.GetDisplayDescription(this);

    /// <summary>
    ///     Gets the offset as a hex string.
    /// </summary>
    public string OffsetHex => $"0x{Offset:X8}";

    /// <summary>
    ///     Gets a human-readable length string.
    /// </summary>
    public string LengthFormatted
    {
        get
        {
            if (Length >= 1024 * 1024) return $"{Length / (1024.0 * 1024.0):F2} MB";

            if (Length >= 1024) return $"{Length / 1024.0:F2} KB";

            return $"{Length} B";
        }
    }
}
