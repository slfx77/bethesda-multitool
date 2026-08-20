using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Post-emission link sweep: subrecords pointing at a ref this plugin hard-deletes must
///     be stripped from every surviving child record — under ESM+ONAM semantics the engine
///     applies the removal strictly and then walks the stale link into an access violation.
///     PGRE joined the sweep 2026-08-07: 22 of master's 174 placed grenades carry XESP, so a
///     carried mine can hold an enable-parent link into the removed set.
/// </summary>
public sealed class DeletedRefLinkStripperTests
{
    private const uint CellId = 0x000ABC00;
    private const uint DeletedRefId = 0x000D0001;
    private const uint LiveRefId = 0x000D0002;
    private const uint PgreId = 0x000A1001;
    private const uint DeletedFlag = 0x20u;

    [Fact]
    public void Carried_Pgre_Xesp_To_Deleted_Ref_Is_Stripped()
    {
        var pgre = BuildRecord("PGRE", PgreId, 0,
            ("NAME", FormIdBytes(0x000A2001)),
            ("XESP", XespBytes(DeletedRefId)),
            ("DATA", new byte[24]));
        var bundles = new List<CellOverrideBundle>
        {
            MakeBundle(temporary: [BuildRecord("REFR", DeletedRefId, DeletedFlag), pgre])
        };

        DeletedRefLinkStripper.Apply(bundles, null);

        var swept = bundles[0].TemporaryChildRecords[1];
        Assert.Equal("PGRE", Encoding.ASCII.GetString(swept, 0, 4));
        Assert.DoesNotContain("XESP", ReadSubrecordSignatures(swept));
        Assert.Contains("NAME", ReadSubrecordSignatures(swept));
        Assert.Contains("DATA", ReadSubrecordSignatures(swept));
    }

    [Fact]
    public void Pgre_Xesp_To_Live_Ref_Is_Kept()
    {
        var pgre = BuildRecord("PGRE", PgreId, 0,
            ("NAME", FormIdBytes(0x000A2001)),
            ("XESP", XespBytes(LiveRefId)),
            ("DATA", new byte[24]));
        var bundles = new List<CellOverrideBundle>
        {
            MakeBundle(temporary: [BuildRecord("REFR", DeletedRefId, DeletedFlag), pgre])
        };

        DeletedRefLinkStripper.Apply(bundles, null);

        Assert.Contains("XESP", ReadSubrecordSignatures(bundles[0].TemporaryChildRecords[1]));
    }

    [Fact]
    public void Refr_Xlkr_To_Deleted_Ref_Is_Stripped_Across_Bundles()
    {
        // The deleted ref lives in one cell's bundle; the linking record in another —
        // the removed set is collected globally before the sweep.
        var linked = BuildRecord("REFR", LiveRefId, 0,
            ("NAME", FormIdBytes(0x000A2001)),
            ("XLKR", FormIdBytes(DeletedRefId)),
            ("DATA", new byte[24]));
        var bundles = new List<CellOverrideBundle>
        {
            MakeBundle(temporary: [BuildRecord("REFR", DeletedRefId, DeletedFlag)]),
            MakeBundle(CellId + 1, [linked])
        };

        DeletedRefLinkStripper.Apply(bundles, null);

        var swept = bundles[1].TemporaryChildRecords[0];
        Assert.DoesNotContain("XLKR", ReadSubrecordSignatures(swept));
        // Data-size header must shrink by the stripped subrecord's 6+len bytes.
        Assert.Equal(
            (uint)(swept.Length - 24),
            BinaryPrimitives.ReadUInt32LittleEndian(swept.AsSpan(4, 4)));
    }

    private static CellOverrideBundle MakeBundle(
        uint cellFormId = CellId,
        List<byte[]>? temporary = null)
    {
        return new CellOverrideBundle
        {
            CellFormId = cellFormId,
            Context = new PcEsmCellContext
            {
                CellFormId = cellFormId,
                IsInterior = true,
                WorldspaceFormId = null,
                BlockGroupType = 2,
                SubblockGroupType = 3,
                BlockLabel = [1, 0, 0, 0],
                SubblockLabel = [2, 0, 0, 0]
            },
            CellRecordBytes = BuildRecord("CELL", cellFormId, 0),
            PersistentChildRecords = [],
            TemporaryChildRecords = temporary ?? []
        };
    }

    private static byte[] BuildRecord(
        string signature, uint formId, uint flags, params (string Sig, byte[] Data)[] subrecords)
    {
        var dataSize = subrecords.Sum(s => 6 + s.Data.Length);
        var bytes = new byte[24 + dataSize];
        Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)dataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), formId);

        var pos = 24;
        foreach (var (sig, data) in subrecords)
        {
            Encoding.ASCII.GetBytes(sig).CopyTo(bytes, pos);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(pos + 4, 2), (ushort)data.Length);
            data.CopyTo(bytes, pos + 6);
            pos += 6 + data.Length;
        }

        return bytes;
    }

    private static byte[] FormIdBytes(uint formId)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, formId);
        return bytes;
    }

    private static byte[] XespBytes(uint parentFormId)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, parentFormId);
        return bytes;
    }

    private static List<string> ReadSubrecordSignatures(byte[] record)
    {
        var signatures = new List<string>();
        var dataSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4, 4));
        var end = Math.Min(record.Length, 24 + dataSize);
        var pos = 24;
        while (pos + 6 <= end)
        {
            signatures.Add(Encoding.ASCII.GetString(record, pos, 4));
            pos += 6 + BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(pos + 4, 2));
        }

        return signatures;
    }
}