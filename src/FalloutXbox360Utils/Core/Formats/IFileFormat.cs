namespace FalloutXbox360Utils.Core.Formats;

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
    ///     Label used for grouping in CLI summaries (e.g., "DDX Textures", "XMA Audio").
    ///     All signatures of a format share the same group label.
    /// </summary>
    string GroupLabel { get; }

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
