using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     Collects REFR/ACHR/ACRE identities from final bundle bytes. This is actual
///     emission evidence: planned children and children encoded before a parent-cell
///     suppression are deliberately absent.
/// </summary>
internal static class EmittedPlacedReferenceCollector
{
    internal static IReadOnlySet<uint> Collect(IReadOnlyList<CellOverrideBundle> bundles)
    {
        var result = new HashSet<uint>();
        foreach (var bundle in bundles)
        {
            CollectRecords(bundle.PersistentChildRecords, result);
            CollectRecords(bundle.VwdChildRecords, result);
            CollectRecords(bundle.TemporaryChildRecords, result);
        }

        return result;
    }

    private static void CollectRecords(IReadOnlyList<byte[]> records, HashSet<uint> result)
    {
        foreach (var record in records)
        {
            if (record.Length < 24)
            {
                continue;
            }

            var signature = Encoding.ASCII.GetString(record, 0, 4);
            if (signature is not ("REFR" or "ACHR" or "ACRE"))
            {
                continue;
            }

            result.Add(BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(12, 4)));
        }
    }
}
