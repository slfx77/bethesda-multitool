using Windows.UI;

namespace Xbox360MemoryCarver.App;

/// <summary>
///     Represents a colored region in the hex viewer corresponding to a detected file.
/// </summary>
internal sealed class FileRegion
{
    public long Start { get; init; }
    public long End { get; init; }
    public required string TypeName { get; init; }
    public Color Color { get; init; }
}
