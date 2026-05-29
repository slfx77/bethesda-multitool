using FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Allocation;

/// <summary>
///     Stable ordering used by <see cref="FormIdPlanner" /> when assigning plugin-range
///     FormIDs to <see cref="RecordDisposition.New" /> records. Sorts by record type
///     (ordinal) then by source FormID. The order matters because the same input must
///     produce the same allocated FormIDs across runs — the parity harness compares
///     legacy vs planner output byte-for-byte, and a non-deterministic allocation order
///     would cause every emitted FormID to drift.
/// </summary>
public sealed class DeterministicAllocationOrder : IComparer<CatalogEntry>
{
    public static readonly DeterministicAllocationOrder Instance = new();

    public int Compare(CatalogEntry? x, CatalogEntry? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var typeCompare = string.CompareOrdinal(x.Type, y.Type);
        if (typeCompare != 0)
        {
            return typeCompare;
        }

        var xId = x.DmpFormId ?? x.MasterFormId ?? 0;
        var yId = y.DmpFormId ?? y.MasterFormId ?? 0;
        return xId.CompareTo(yId);
    }
}
