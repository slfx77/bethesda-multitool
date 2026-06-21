using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Indexing;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Conversion;

/// <summary>
///     Tests for <see cref="EsmConversionIndexBuilder.ScanAllCellChildGroups" /> after replacing the
///     per-match <c>list.Any(g => g.Offset == offset)</c> dedup with a seeded <c>HashSet</c>. The dedup
///     is load-bearing: this flat re-scan re-discovers groups Phase 1 already indexed and must not
///     double-add them.
/// </summary>
public class EsmConversionIndexBuilderTests
{
    private const uint CellId = 0x100;
    private const int TemporaryGroupType = 9;

    private static readonly int[] ExpectedOffsets = [0, 24];

    /// <summary>Writes a 24-byte big-endian GRUP header ("PURG" signature) at the offset.</summary>
    private static void WriteGrup(byte[] buffer, int offset, uint size, uint label, int type)
    {
        buffer[offset + 0] = 0x50; // 'P'
        buffer[offset + 1] = 0x55; // 'U'
        buffer[offset + 2] = 0x52; // 'R'
        buffer[offset + 3] = 0x47; // 'G'
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 4), size);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 8), label);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 12), (uint)type);
        // Stamp + Unknown left zero.
    }

    [Fact]
    public void ScanAllCellChildGroups_IndexesEachDistinctOffsetOnce()
    {
        // Two temporary cell-child GRUPs with the same label at different offsets.
        var buffer = new byte[48];
        WriteGrup(buffer, 0, 24, CellId, TemporaryGroupType);
        WriteGrup(buffer, 24, 24, CellId, TemporaryGroupType);

        var index = new ConversionIndex();
        new EsmConversionIndexBuilder(buffer).ScanAllCellChildGroups(index);

        var entries = index.CellChildGroups[(CellId, TemporaryGroupType)];
        Assert.Equal(ExpectedOffsets, entries.Select(e => e.Offset).Order());
    }

    [Fact]
    public void ScanAllCellChildGroups_DoesNotReAddOffsetsFromPhase1()
    {
        // Same buffer, but pretend Phase 1 already indexed the GRUP at offset 0.
        var buffer = new byte[48];
        WriteGrup(buffer, 0, 24, CellId, TemporaryGroupType);
        WriteGrup(buffer, 24, 24, CellId, TemporaryGroupType);

        var index = new ConversionIndex();
        index.CellChildGroups[(CellId, TemporaryGroupType)] =
            [new GrupEntry(TemporaryGroupType, CellId, 0, 24)];

        new EsmConversionIndexBuilder(buffer).ScanAllCellChildGroups(index);

        // Offset 0 must not be duplicated; offset 24 must be added -> exactly two entries.
        var entries = index.CellChildGroups[(CellId, TemporaryGroupType)];
        Assert.Equal(ExpectedOffsets, entries.Select(e => e.Offset).Order());
    }

    [Fact]
    public void ScanAllCellChildGroups_IgnoresNonCellChildGroupTypes()
    {
        // Group type 5 (cell block) is not a cell-child group (8/9/10) and must be ignored.
        var buffer = new byte[24];
        WriteGrup(buffer, 0, 24, CellId, 5);

        var index = new ConversionIndex();
        new EsmConversionIndexBuilder(buffer).ScanAllCellChildGroups(index);

        Assert.Empty(index.CellChildGroups);
    }
}
