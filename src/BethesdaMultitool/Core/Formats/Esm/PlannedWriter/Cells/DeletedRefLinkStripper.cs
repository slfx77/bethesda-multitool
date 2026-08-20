using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Reporting;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     Post-emission sweep: strip link subrecords (XLKR linked ref, XTEL teleport target,
///     XESP enable parent, XNDP navmesh door link) whose target is a ref this plugin marks
///     removed. Carried master refs keep their subrecords verbatim, so links into the
///     removed set survive encoding — and under an ESM-flagged file with ONAM, removals
///     apply strictly, leaving the engine to walk ExtraLinkedRef into a form that no
///     longer resolves (the Gomorrah access-violation class).
///     <para>
///         <b>Why this stayed a byte sweep when the other link rules moved to plan time.</b>
///         Retirement Stage H5 (2026-08-12) moved every captured-child link decision into
///         <c>PlacedRefLinkPlanner</c>, but this rule's subjects are the two populations the plan
///         does not describe: master refs carried forward as verbatim bytes, and the deletion
///         tombstones themselves — both produced by <see cref="MasterChildCarryForward" /> during
///         writing, neither backed by a <c>RecordPlan</c>. Relocating this rule therefore means
///         relocating carry-forward and tombstone synthesis first, which is its own stage.
///     </para>
///     <para>
///         Measured on the three-dump corpus at H5: 0 / 26 / 415 tombstones emitted (xex21 /
///         xex22 / xex44) and <c>refr.link-to-deleted-stripped</c> == 0 on all three — the rule is
///         live but currently unexercised, so it is retained on the strength of the crash class it
///         guards rather than on observed strips.
///     </para>
/// </summary>
internal static class DeletedRefLinkStripper
{
    private const uint DeletedFlag = 0x20u;
    private const uint CompressedFlag = 0x00040000u;

    public static void Apply(List<CellOverrideBundle> bundles, ConversionPipelineStats? stats)
    {
        var deleted = new HashSet<uint>();
        foreach (var bundle in bundles)
        {
            CollectDeleted(bundle.PersistentChildRecords, deleted);
            CollectDeleted(bundle.VwdChildRecords, deleted);
            CollectDeleted(bundle.TemporaryChildRecords, deleted);
        }

        if (deleted.Count == 0)
        {
            return;
        }

        for (var i = 0; i < bundles.Count; i++)
        {
            var bundle = bundles[i];
            var persistent = SweepRecords(bundle.PersistentChildRecords, deleted, stats, out var p);
            var vwd = SweepRecords(bundle.VwdChildRecords, deleted, stats, out var v);
            var temporary = SweepRecords(bundle.TemporaryChildRecords, deleted, stats, out var t);
            if (p || v || t)
            {
                bundles[i] = bundle with
                {
                    PersistentChildRecords = persistent,
                    VwdChildRecords = vwd,
                    TemporaryChildRecords = temporary
                };
            }
        }
    }

    private static void CollectDeleted(IReadOnlyList<byte[]> records, HashSet<uint> deleted)
    {
        foreach (var record in records)
        {
            if (record.Length >= 24
                && (BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(8, 4)) & DeletedFlag) != 0)
            {
                deleted.Add(BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(12, 4)));
            }
        }
    }

    private static List<byte[]> SweepRecords(
        IReadOnlyList<byte[]> records,
        HashSet<uint> deleted,
        ConversionPipelineStats? stats,
        out bool changed)
    {
        changed = false;
        var result = new List<byte[]>(records.Count);
        foreach (var record in records)
        {
            var swept = SweepRecord(record, deleted, stats);
            if (!ReferenceEquals(swept, record))
            {
                changed = true;
            }

            result.Add(swept);
        }

        return result;
    }

    private static byte[] SweepRecord(byte[] record, HashSet<uint> deleted, ConversionPipelineStats? stats)
    {
        if (record.Length < 24)
        {
            return record;
        }

        var sig = Encoding.ASCII.GetString(record, 0, 4);
        // PGRE added 2026-08-07 alongside its carry-forward: 22 of master's 174 placed grenades
        // carry XESP, so a carried mine can hold an enable-parent link to a form this plugin
        // hard-deletes. That dangling link is the access-violation class this sweeper exists for.
        // (XPWR, on 4 master PGREs, is not in this sweeper's subrecord set at all — pre-existing
        // and out of scope here.)
        if (sig is not ("REFR" or "ACHR" or "ACRE" or "PGRE"))
        {
            return record;
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(8, 4));
        if ((flags & (DeletedFlag | CompressedFlag)) != 0)
        {
            return record;
        }

        var dataSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4, 4));
        var end = Math.Min(record.Length, 24 + dataSize);
        List<(int Offset, int Length)>? drops = null;

        var pos = 24;
        while (pos + 6 <= end)
        {
            var subSig = Encoding.ASCII.GetString(record, pos, 4);
            var subLen = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(pos + 4, 2));
            var total = 6 + subLen;
            if (subSig is "XLKR" or "XTEL" or "XESP" or "XNDP" && subLen >= 4)
            {
                // XLKR may carry one or two FormIDs; the others carry the target first.
                var uintsToCheck = subSig == "XLKR" ? subLen / 4 : 1;
                for (var u = 0; u < uintsToCheck; u++)
                {
                    var target = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(pos + 6 + u * 4, 4));
                    if (deleted.Contains(target))
                    {
                        drops ??= [];
                        drops.Add((pos, total));
                        break;
                    }
                }
            }

            pos += total;
        }

        if (drops is null)
        {
            return record;
        }

        var removedBytes = drops.Sum(d => d.Length);
        var rebuilt = new byte[record.Length - removedBytes];
        record.AsSpan(0, 24).CopyTo(rebuilt);
        BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(4, 4), (uint)(dataSize - removedBytes));

        var src = 24;
        var dst = 24;
        foreach (var (offset, length) in drops)
        {
            var chunk = offset - src;
            record.AsSpan(src, chunk).CopyTo(rebuilt.AsSpan(dst));
            dst += chunk;
            src = offset + length;
            stats?.IncrementDropReason("refr.link-to-deleted-stripped");
        }

        record.AsSpan(src, record.Length - src).CopyTo(rebuilt.AsSpan(dst));
        return rebuilt;
    }
}
