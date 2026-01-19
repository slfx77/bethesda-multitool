using System.IO.MemoryMappedFiles;
using Xbox360MemoryCarver.Core.Converters;

namespace Xbox360MemoryCarver.Core.Formats;

/// <summary>
///     Category for grouping file types in the UI.
/// </summary>
public enum FileCategory
{
    Texture,
    Image,
    Audio,
    Video,
    Model,
    Module,
    Script,
    Xbox,
    Plugin,
    Header
}

/// <summary>
///     Defines a file signature (magic bytes pattern).
/// </summary>
public sealed class FormatSignature
{
    /// <summary>
    ///     Unique identifier for this signature variant (e.g., "ddx_3xdo", "xui_scene").
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
}

/// <summary>
///     Result from parsing a file header.
/// </summary>
public sealed class ParseResult
{
    /// <summary>
    ///     Format identifier (e.g., "DDS", "XMA2").
    /// </summary>
    public required string Format { get; init; }

    /// <summary>
    ///     Estimated size of the complete file in bytes.
    /// </summary>
    public int EstimatedSize { get; init; }

    /// <summary>
    ///     Optional filename extracted from file metadata.
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>
    ///     Optional override for the output folder (e.g., "anims" vs "meshes" for NIF files).
    ///     If null, the format's default OutputFolder is used.
    /// </summary>
    public string? OutputFolderOverride { get; init; }

    /// <summary>
    ///     Optional override for the file extension (e.g., ".kf" for animations instead of ".nif").
    ///     If null, the format's default Extension is used.
    /// </summary>
    public string? ExtensionOverride { get; init; }

    /// <summary>
    ///     Additional metadata (dimensions, format details, flags, etc.).
    /// </summary>
    public Dictionary<string, object> Metadata { get; init; } = [];
}

/// <summary>
///     Core interface for a file format module.
///     Each supported format implements this to describe itself and provide parsing/processing capabilities.
/// </summary>
public interface IFileFormat
{
    /// <summary>
    ///     Unique identifier for this format (e.g., "ddx", "xma", "xex").
    /// </summary>
    string FormatId { get; }

    /// <summary>
    ///     Display name for UI (e.g., "DDX", "XMA Audio", "Module").
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    ///     File extension including the dot (e.g., ".ddx", ".xma").
    /// </summary>
    string Extension { get; }

    /// <summary>
    ///     Category for grouping and coloring.
    /// </summary>
    FileCategory Category { get; }

    /// <summary>
    ///     Output folder name for extracted files of this type.
    /// </summary>
    string OutputFolder { get; }

    /// <summary>
    ///     Minimum valid file size in bytes.
    /// </summary>
    int MinSize { get; }

    /// <summary>
    ///     Maximum valid file size in bytes.
    /// </summary>
    int MaxSize { get; }

    /// <summary>
    ///     Whether to show in UI filter checkboxes.
    /// </summary>
    bool ShowInFilterUI { get; }

    /// <summary>
    ///     Whether to include this format's signatures in automatic scanning.
    ///     Set to false for formats that are better extracted via other means (e.g., minidump metadata).
    /// </summary>
    bool EnableSignatureScanning { get; }

    /// <summary>
    ///     All signatures that identify this format.
    /// </summary>
    IReadOnlyList<FormatSignature> Signatures { get; }

    /// <summary>
    ///     Parse file header to determine size and extract metadata.
    /// </summary>
    /// <param name="data">Raw data starting at the signature match.</param>
    /// <param name="offset">Offset within the data span (usually 0).</param>
    /// <returns>Parse result with size and metadata, or null if invalid.</returns>
    ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0);

    /// <summary>
    ///     Get display description for a file instance.
    /// </summary>
    /// <param name="signatureId">The matched signature ID.</param>
    /// <param name="metadata">Optional metadata from parsing.</param>
    /// <returns>Human-readable description.</returns>
    string GetDisplayDescription(string signatureId, IReadOnlyDictionary<string, object>? metadata = null);
}

/// <summary>
///     Optional interface for formats that support conversion to another format.
/// </summary>
public interface IFileConverter
{
    /// <summary>
    ///     Target format after conversion (e.g., ".dds" for DDX).
    /// </summary>
    string TargetExtension { get; }

    /// <summary>
    ///     Target folder name for converted files (e.g., "textures" for DDX→DDS).
    /// </summary>
    string TargetFolder { get; }

    /// <summary>
    ///     Whether the converter is ready to use.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    ///     Conversion statistics.
    /// </summary>
    int ConvertedCount { get; }

    int FailedCount { get; }

    /// <summary>
    ///     Check if this file can be converted based on signature ID and metadata.
    /// </summary>
    /// <param name="signatureId">The matched signature ID.</param>
    /// <param name="metadata">Metadata from parsing.</param>
    /// <returns>True if conversion should be attempted.</returns>
    bool CanConvert(string signatureId, IReadOnlyDictionary<string, object>? metadata);

    /// <summary>
    ///     Convert the file data to the target format.
    /// </summary>
    /// <param name="data">Original file data.</param>
    /// <param name="metadata">Metadata from parsing.</param>
    /// <returns>Conversion result with data and status.</returns>
    Task<DdxConversionResult> ConvertAsync(byte[] data, IReadOnlyDictionary<string, object>? metadata = null);

    /// <summary>
    ///     Initialize the converter (e.g., find external tools).
    ///     Called once during carver initialization.
    /// </summary>
    /// <param name="verbose">Enable verbose output.</param>
    /// <param name="options">Additional options.</param>
    /// <returns>True if converter is ready.</returns>
    bool Initialize(bool verbose = false, Dictionary<string, object>? options = null);
}

/// <summary>
///     Optional interface for formats that support repair/enhancement.
/// </summary>
public interface IFileRepairer
{
    /// <summary>
    ///     Determine if the file needs repair based on metadata.
    /// </summary>
    bool NeedsRepair(IReadOnlyDictionary<string, object>? metadata);

    /// <summary>
    ///     Repair or enhance the file.
    /// </summary>
    /// <param name="data">Original file data.</param>
    /// <param name="metadata">Metadata from parsing.</param>
    /// <returns>Repaired data.</returns>
    byte[] Repair(byte[] data, IReadOnlyDictionary<string, object>? metadata);
}

/// <summary>
///     Optional interface for formats that can scan entire dumps for records.
///     Used by MemoryDumpAnalyzer to gather format-specific information.
/// </summary>
public interface IDumpScanner
{
    /// <summary>
    ///     Scan the entire dump for records of this format type using memory-mapped access.
    ///     This avoids loading the entire file into memory.
    /// </summary>
    /// <param name="accessor">Memory-mapped view accessor for the dump file.</param>
    /// <param name="fileSize">Total size of the dump file.</param>
    /// <returns>Scan results specific to this format.</returns>
    object ScanDump(MemoryMappedViewAccessor accessor, long fileSize);
}
