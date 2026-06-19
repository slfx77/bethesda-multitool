namespace BethesdaMultitool.Core.Formats.Esm.Runtime;

internal sealed record RuntimeWorldCellLayout(int WorldShift, int CellShift)
{
    /// <summary>Creates the default worldspace/cell field-shift layout for retail or prototype offsets.</summary>
    public static RuntimeWorldCellLayout CreateDefault(bool useProtoOffsets = false)
    {
        var shift = RuntimeBuildOffsets.GetWorldCellFieldShift(useProtoOffsets);
        return new RuntimeWorldCellLayout(shift, shift);
    }
}
