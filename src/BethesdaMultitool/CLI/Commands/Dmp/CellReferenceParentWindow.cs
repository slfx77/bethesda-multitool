namespace BethesdaMultitool.CLI.Commands.Dmp;

public sealed record CellReferenceParentWindow
{
    public uint CellFormId { get; init; }
    public uint? AnchorReferenceFormId { get; init; }
    public long? CenterOffset { get; init; }
    public long? MinOffset { get; init; }
    public long? MaxOffset { get; init; }
    public int RadiusBeforeBytes { get; init; } = 0x400;
    public int RadiusAfterBytes { get; init; } = 0x400;
    public string? Label { get; init; }

    internal bool TryResolveRange(Func<uint, long?> resolveAnchorOffset, out long minOffset, out long maxOffset)
    {
        minOffset = 0;
        maxOffset = 0;

        if (MinOffset.HasValue && MaxOffset.HasValue)
        {
            minOffset = Math.Min(MinOffset.Value, MaxOffset.Value);
            maxOffset = Math.Max(MinOffset.Value, MaxOffset.Value);
            return maxOffset > 0;
        }

        var center = CenterOffset;
        if (!center.HasValue && AnchorReferenceFormId is { } anchorReferenceFormId)
        {
            center = resolveAnchorOffset(anchorReferenceFormId);
        }

        if (center is not > 0)
        {
            return false;
        }

        var before = Math.Max(0, RadiusBeforeBytes);
        var after = Math.Max(0, RadiusAfterBytes);
        minOffset = center.Value > before ? center.Value - before : 0;
        maxOffset = center.Value > long.MaxValue - after ? long.MaxValue : center.Value + after;
        return true;
    }
}
