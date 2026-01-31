using FalloutXbox360Utils.Core.Formats;

namespace FalloutXbox360Utils.Core;

/// <summary>
///     Information about a carved file.
/// </summary>
public class CarvedFileInfo
{
    public long Offset { get; set; }
    public long Length { get; set; }
    public string FileType { get; set; } = "";
    public string? FileName { get; set; }
    public byte[]? Header { get; set; }
    public string? Error { get; set; }

    /// <summary>
    ///     The signature ID used to identify this file (e.g., "xui_scene", "ddx_3xdo").
    ///     Used for efficient color lookup without string matching.
    /// </summary>
    public string? SignatureId { get; set; }

    /// <summary>
    ///     The file category for color coding.
    /// </summary>
    public FileCategory Category { get; set; }

    /// <summary>
    ///     True if this file was detected as potentially truncated due to a memory region gap.
    ///     Files crossing non-contiguous virtual address boundaries may be incomplete.
    /// </summary>
    public bool IsTruncated { get; set; }
}
