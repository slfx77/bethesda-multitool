using Windows.UI;

namespace FalloutXbox360Utils;

/// <summary>
///     Represents a colored region in the hex viewer corresponding to a detected file.
/// </summary>
internal sealed class FileRegion
{
    public long Start { get; init; }
    public long End { get; init; }
    public required string TypeName { get; init; }
    public Color Color { get; init; }

    /// <summary>
    ///     True for gap regions (unidentified data), false for recognized file data.
    ///     Used for sorting priority - file data takes precedence over gaps at same offset.
    /// </summary>
    public bool IsGap { get; init; }
}
