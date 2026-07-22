using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     Master-resident cell-child records this plugin emits (overrides, carry-forwards,
///     deletion records, LAND copies). The ESM-flagged TES4 must list these in ONAM or
///     the FO3/FNV runtime mishandles the overrides at load.
/// </summary>
internal static class OverriddenChildFormIdCollector
{
    public static HashSet<uint> Collect(List<CellOverrideBundle> bundles)
    {
        var result = new HashSet<uint>();
        foreach (var bundle in bundles)
        {
            CollectFromRecords(bundle.PersistentChildRecords, result);
            CollectFromRecords(bundle.VwdChildRecords, result);
            CollectFromRecords(bundle.TemporaryChildRecords, result);
        }

        return result;

        static void CollectFromRecords(IReadOnlyList<byte[]> records, HashSet<uint> result)
        {
            foreach (var record in records)
            {
                if (record.Length < 24)
                {
                    continue;
                }

                var sig = System.Text.Encoding.ASCII.GetString(record, 0, 4);
                if (sig is not ("REFR" or "ACHR" or "ACRE" or "PGRE" or "PMIS" or "LAND" or "NAVM"))
                {
                    continue;
                }

                // xEdit rule (wbImplementation.pas: "ONAMs are for overridden temporary refs
                // only"): persistent-flagged records are excluded. Listing them sends the
                // runtime's temporary-group loader after records that live in persistent
                // groups — "Failed to load temporary data" for the whole cell.
                var flags = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    record.AsSpan(8, 4));
                if ((flags & 0x400u) != 0)
                {
                    continue;
                }

                var formId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    record.AsSpan(12, 4));
                if (formId < 0x01000000u)
                {
                    result.Add(formId);
                }
            }
        }
    }
}
