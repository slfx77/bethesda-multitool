using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Nav;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin.Nav;

/// <summary>
///     Synthetic in-memory tests for <see cref="NavMeshByteRewriter.SanitizeNvexInNavmRecord" />:
///     post-emission NVEX sanitizer that drops cross-NAVM links whose target FormID isn't in
///     the (master ∪ actually-emitted) set, then patches DATA.EdgeLinkCount to match.
///     Tests cover the three documented behaviors: drop danglers, no-op when nothing dangles,
///     and no-op when there's no NVEX at all.
/// </summary>
public sealed class NavMeshByteRewriterTests
{
    private const int RecordHeaderSize = 24;
    private const int SubrecordHeaderSize = 6;
    private const int DataPayloadSize = 20;
    private const int NvexEntrySize = 10;

    [Fact]
    public void SanitizeNvex_DropsDanglingFormIds()
    {
        // NVEX with 3 entries pointing at 0xAAA, 0xBBB, 0xCCC. validTargets contains only
        // 0xAAA and 0xCCC; 0xBBB should be dropped.
        var record = BuildNavmRecord(
            cellFormId: 0x0100AAAA,
            edgeLinkCountInData: 3,
            nvexTargets: new uint[] { 0x00000AAA, 0x00000BBB, 0x00000CCC });

        var validTargets = new HashSet<uint> { 0x00000AAA, 0x00000CCC };

        var sanitized = NavMeshByteRewriter.SanitizeNvexInNavmRecord(record, validTargets, out var dropped);

        Assert.Equal(1, dropped);

        // Locate the NVEX subrecord in the sanitized body and confirm it has exactly 2 entries
        // pointing at the surviving FormIDs in input order.
        var (nvexPayload, dataEdgeLinkCount) = ExtractNvexAndDataEdgeCount(sanitized);
        Assert.NotNull(nvexPayload);
        Assert.Equal(2 * NvexEntrySize, nvexPayload!.Length);

        Assert.Equal(0x00000AAAu,
            BinaryPrimitives.ReadUInt32LittleEndian(nvexPayload.AsSpan(0 * NvexEntrySize + 4, 4)));
        Assert.Equal(0x00000CCCu,
            BinaryPrimitives.ReadUInt32LittleEndian(nvexPayload.AsSpan(1 * NvexEntrySize + 4, 4)));

        // DATA.EdgeLinkCount must reflect the post-sanitization NVEX count or the engine walks
        // past the end of NVEX and null-derefs in NavMeshInfoMap setup.
        Assert.Equal(2u, dataEdgeLinkCount);
    }

    [Fact]
    public void SanitizeNvex_KeptNavmsPassThroughUnchanged()
    {
        // Every NVEX target is in validTargets AND DATA.EdgeLinkCount already matches.
        // The sanitizer's "changed" flag should stay false and the input array should be
        // returned reference-equal.
        var record = BuildNavmRecord(
            cellFormId: 0x0100AAAA,
            edgeLinkCountInData: 2,
            nvexTargets: new uint[] { 0x00000111, 0x00000222 });

        var validTargets = new HashSet<uint> { 0x00000111, 0x00000222 };

        var sanitized = NavMeshByteRewriter.SanitizeNvexInNavmRecord(record, validTargets, out var dropped);

        Assert.Equal(0, dropped);
        Assert.Same(record, sanitized);
    }

    [Fact]
    public void SanitizeNvex_NoNvexNoOp()
    {
        // NAVM with no NVEX subrecord. DATA.EdgeLinkCount = 0 (matches the absent NVEX),
        // so the sanitizer should short-circuit and return the input reference-equal.
        var record = BuildNavmRecord(
            cellFormId: 0x0100AAAA,
            edgeLinkCountInData: 0,
            nvexTargets: null);

        var validTargets = new HashSet<uint>();

        var sanitized = NavMeshByteRewriter.SanitizeNvexInNavmRecord(record, validTargets, out var dropped);

        Assert.Equal(0, dropped);
        Assert.Same(record, sanitized);
    }

    // ============================================================================
    // Synthetic NAVM record construction
    // ============================================================================

    /// <summary>
    ///     Build a minimal NAVM record with the 24-byte main-record header followed by:
    ///     <list type="bullet">
    ///         <item><description><b>DATA</b> (20 bytes): CellFormId at +0; EdgeLinkCount at +12.</description></item>
    ///         <item><description><b>NVEX</b> (10 bytes per entry): Type=0 at +0; NavmeshFormID at +4; Triangle=0 at +8. Omitted when <paramref name="nvexTargets" /> is null.</description></item>
    ///     </list>
    /// </summary>
    private static byte[] BuildNavmRecord(uint cellFormId, uint edgeLinkCountInData, uint[]? nvexTargets)
    {
        var dataSubrecordSize = SubrecordHeaderSize + DataPayloadSize;
        var nvexPayloadSize = nvexTargets is null ? 0 : nvexTargets.Length * NvexEntrySize;
        var nvexSubrecordSize = nvexTargets is null ? 0 : SubrecordHeaderSize + nvexPayloadSize;
        var bodySize = dataSubrecordSize + nvexSubrecordSize;

        var record = new byte[RecordHeaderSize + bodySize];

        // Main record header.
        record[0] = (byte)'N';
        record[1] = (byte)'A';
        record[2] = (byte)'V';
        record[3] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4, 4), (uint)bodySize);
        // flags (8..11), FormId (12..15), versioning (16..23) all left zero — sanitizer
        // doesn't read them.

        var bodyStart = RecordHeaderSize;

        // DATA subrecord.
        WriteSubrecordHeader(record.AsSpan(bodyStart, SubrecordHeaderSize), "DATA", DataPayloadSize);
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.AsSpan(bodyStart + SubrecordHeaderSize + 0, 4), cellFormId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.AsSpan(bodyStart + SubrecordHeaderSize + 12, 4), edgeLinkCountInData);

        // NVEX subrecord (optional).
        if (nvexTargets is not null)
        {
            var nvexStart = bodyStart + dataSubrecordSize;
            WriteSubrecordHeader(record.AsSpan(nvexStart, SubrecordHeaderSize), "NVEX", nvexPayloadSize);
            for (var i = 0; i < nvexTargets.Length; i++)
            {
                var entryStart = nvexStart + SubrecordHeaderSize + i * NvexEntrySize;
                // Type at +0 left zero; NavmeshFormId at +4.
                BinaryPrimitives.WriteUInt32LittleEndian(
                    record.AsSpan(entryStart + 4, 4), nvexTargets[i]);
                // Triangle at +8 left zero.
            }
        }

        return record;
    }

    private static void WriteSubrecordHeader(Span<byte> dest, string signature, int payloadSize)
    {
        dest[0] = (byte)signature[0];
        dest[1] = (byte)signature[1];
        dest[2] = (byte)signature[2];
        dest[3] = (byte)signature[3];
        BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(4, 2), (ushort)payloadSize);
    }

    /// <summary>
    ///     Walks a sanitized NAVM record's body and returns the NVEX payload (if any)
    ///     plus DATA.EdgeLinkCount.
    /// </summary>
    private static (byte[]? NvexPayload, uint DataEdgeLinkCount) ExtractNvexAndDataEdgeCount(byte[] record)
    {
        byte[]? nvexPayload = null;
        uint dataEdgeLinkCount = 0;

        var bodySize = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4, 4));
        var body = record.AsSpan(RecordHeaderSize, (int)bodySize);
        var j = 0;
        while (j + SubrecordHeaderSize <= body.Length)
        {
            var sig = System.Text.Encoding.ASCII.GetString(body.Slice(j, 4));
            var size = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(j + 4, 2));
            if (j + SubrecordHeaderSize + size > body.Length)
            {
                break;
            }

            if (sig == "NVEX")
            {
                nvexPayload = body.Slice(j + SubrecordHeaderSize, size).ToArray();
            }
            else if (sig == "DATA" && size >= 16)
            {
                dataEdgeLinkCount = BinaryPrimitives.ReadUInt32LittleEndian(
                    body.Slice(j + SubrecordHeaderSize + 12, 4));
            }

            j += SubrecordHeaderSize + size;
        }

        return (nvexPayload, dataEdgeLinkCount);
    }
}
