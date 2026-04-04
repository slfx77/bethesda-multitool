namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime;

internal sealed record RuntimeWorldCellLayout(int WorldShift, int CellShift)
{
    public static RuntimeWorldCellLayout CreateDefault(bool useProtoOffsets = false)
    {
        var shift = RuntimeBuildOffsets.GetWorldCellFieldShift(useProtoOffsets);
        return new RuntimeWorldCellLayout(shift, shift);
    }
}
