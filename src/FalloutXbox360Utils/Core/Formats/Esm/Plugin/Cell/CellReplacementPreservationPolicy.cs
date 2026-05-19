using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;

/// <summary>
///     Preservation policy for <c>--replace-cell-temporaries</c>. A master ref is deletion-eligible
///     only when the DMP carries a same-base placement at the same world position.
/// </summary>
internal static class CellReplacementPreservationPolicy
{
    /// <summary>
    ///     World-space tolerance for per-ref position matching. FNV uses approximately
    ///     1 unit ~= 1.4 cm; 50 units stays below the editor's 64-unit snap step.
    /// </summary>
    internal const float PositionMatchTolerance = 50f;

    internal const float PositionMatchToleranceSquared = PositionMatchTolerance * PositionMatchTolerance;

    public static Dictionary<uint, List<PlacedReference>> BuildPlacementsByBase(
        IEnumerable<PlacedReference> placements,
        IReadOnlyDictionary<uint, uint> sourceToAllocated)
    {
        var placementsByBase = new Dictionary<uint, List<PlacedReference>>();

        foreach (var placed in placements)
        {
            if (placed.BaseFormId == 0)
            {
                continue;
            }

            AddPlacement(placementsByBase, placed.BaseFormId, placed);
            if (sourceToAllocated.TryGetValue(placed.BaseFormId, out var allocatedBase)
                && allocatedBase != 0
                && allocatedBase != placed.BaseFormId)
            {
                AddPlacement(placementsByBase, allocatedBase, placed);
            }
        }

        return placementsByBase;
    }

    public static Func<ParsedMainRecord, bool> CreatePreserveFilter(
        IReadOnlyDictionary<uint, List<PlacedReference>> placementsByBase)
    {
        return masterRef => ShouldPreserveMasterRef(masterRef, placementsByBase);
    }

    public static bool ShouldPreserveMasterRef(
        ParsedMainRecord masterRef,
        IReadOnlyDictionary<uint, List<PlacedReference>> placementsByBase)
    {
        // 0x00000400 = Persistent flag on FNV record headers.
        if ((masterRef.Header.Flags & 0x00000400u) != 0)
        {
            return true;
        }

        var baseFormId = CellStructuralReferencePreserver.ReadNameFormId(masterRef);
        if (!baseFormId.HasValue
            || !placementsByBase.TryGetValue(baseFormId.Value, out var candidates))
        {
            return true;
        }

        if (!TryReadMasterRefPosition(masterRef, out var x, out var y, out var z))
        {
            return true;
        }

        foreach (var candidate in candidates)
        {
            var dx = candidate.X - x;
            var dy = candidate.Y - y;
            var dz = candidate.Z - z;
            if (dx * dx + dy * dy + dz * dz <= PositionMatchToleranceSquared)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool TryReadMasterRefPosition(ParsedMainRecord masterRef, out float x, out float y, out float z)
    {
        var data = masterRef.Subrecords.FirstOrDefault(s => s.Signature == "DATA" && s.Data.Length >= 12);
        if (data is null)
        {
            x = y = z = 0f;
            return false;
        }

        x = BinaryPrimitives.ReadSingleLittleEndian(data.Data.AsSpan(0, 4));
        y = BinaryPrimitives.ReadSingleLittleEndian(data.Data.AsSpan(4, 4));
        z = BinaryPrimitives.ReadSingleLittleEndian(data.Data.AsSpan(8, 4));
        return true;
    }

    private static void AddPlacement(
        Dictionary<uint, List<PlacedReference>> placementsByBase,
        uint baseFormId,
        PlacedReference placed)
    {
        if (!placementsByBase.TryGetValue(baseFormId, out var list))
        {
            list = [];
            placementsByBase[baseFormId] = list;
        }

        list.Add(placed);
    }
}
