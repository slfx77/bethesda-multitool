namespace FalloutXbox360Utils.Core.Formats;

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
