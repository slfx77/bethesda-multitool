namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Display model for a single subrecord row in a three-way diff table.
/// </summary>
internal sealed class ThreeWaySubrecordRow
{
    public required string Signature { get; init; }
    public required string SizeDisplay { get; init; }
    public required string XboxOffsetDisplay { get; init; }
    public required string ConvertedOffsetDisplay { get; init; }
    public required string PcOffsetDisplay { get; init; }
    public required string StatusMarkup { get; init; }
    public required bool ShowDetails { get; init; }
    public required string? DetailsMarkup { get; init; }
}
