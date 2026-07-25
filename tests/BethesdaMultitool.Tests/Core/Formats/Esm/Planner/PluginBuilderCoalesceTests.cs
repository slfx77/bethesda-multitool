using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner;

/// <summary>
///     Characterization tests for <see cref="PluginBuilder.CoalesceCellOverrideBundles" /> after the
///     O(n^2) <c>List.InsertRange(0, ...)</c> prepend was replaced with chunk collection + a single
///     reverse-order flatten. The observable contract must be preserved: records are emitted in
///     original bundle order, but duplicates are deduped in reverse bundle order (later bundles win).
/// </summary>
public class PluginBuilderCoalesceTests
{
    // ReadRecordIdentity uses uint32 at offset 0 (signature) and offset 12 (FormID); byte 4 is a
    // payload marker that does NOT affect identity, used to tell two same-identity records apart.
    private static byte[] Rec(uint signature, uint formId, byte marker)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), signature);
        bytes[4] = marker;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), formId);
        return bytes;
    }

    private static CellOverrideBundle Bundle(uint cellFormId, params byte[][] persistent)
    {
        return new CellOverrideBundle
        {
            CellFormId = cellFormId,
            Context = new PcEsmCellContext { CellFormId = cellFormId, IsInterior = false },
            CellRecordBytes = [],
            PersistentChildRecords = persistent,
            TemporaryChildRecords = []
        };
    }

    [Fact]
    public void Coalesce_SingleBundlePerCell_PassesThrough()
    {
        var b = Bundle(0x100, Rec(0x10, 0x01, 0xA1));
        var result = PluginBuilder.CoalesceCellOverrideBundles([b]);

        Assert.Single(result);
        Assert.Same(b, result[0]);
    }

    [Fact]
    public void Coalesce_MultipleBundles_EmitsRecordsInOriginalBundleOrder()
    {
        var r1 = Rec(0x10, 0x01, 0xA1);
        var r2 = Rec(0x10, 0x02, 0xA2);
        var r3 = Rec(0x10, 0x03, 0xA3);

        var result = PluginBuilder.CoalesceCellOverrideBundles(
            [Bundle(0x100, r1), Bundle(0x100, r2), Bundle(0x100, r3)]);

        Assert.Single(result);
        Assert.Equal([r1, r2, r3], result[0].PersistentChildRecords);
    }

    [Fact]
    public void Coalesce_DuplicateAcrossBundles_LaterBundleWins()
    {
        // Same identity (0x10, 0x01) in both bundles, distinguished by marker byte.
        var dupFromFirst = Rec(0x10, 0x01, 0xB1);
        var dupFromLast = Rec(0x10, 0x01, 0xB2);
        var unique = Rec(0x10, 0x02, 0xBB);

        var result = PluginBuilder.CoalesceCellOverrideBundles(
            [Bundle(0x100, dupFromFirst), Bundle(0x100, dupFromLast, unique)]);

        Assert.Single(result);
        var persistent = result[0].PersistentChildRecords;

        // Later bundle's instance of the duplicate wins; emitted before the later bundle's unique record.
        Assert.Equal(2, persistent.Count);
        Assert.Same(dupFromLast, persistent[0]);
        Assert.Same(unique, persistent[1]);
    }

    [Fact]
    public void Coalesce_DistinctCells_AreNotMerged()
    {
        var result = PluginBuilder.CoalesceCellOverrideBundles(
            [Bundle(0x100, Rec(0x10, 0x01, 0xA1)), Bundle(0x200, Rec(0x10, 0x01, 0xA2))]);

        Assert.Equal(2, result.Count);
    }
}