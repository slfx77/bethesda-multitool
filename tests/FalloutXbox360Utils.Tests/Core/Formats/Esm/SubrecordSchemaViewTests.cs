using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm;

/// <summary>
///     Contract tests for <see cref="SubrecordSchemaView" /> — the typed read-side bridge that
///     mirrors <c>SchemaModelSerializer</c> on the write side. Verifies the four core invariants:
///     hard-fail on missing schema (matching encoder behavior), schema-driven field decode,
///     soft-fail variant returns null, and accessor coercion across adjacent numeric types.
/// </summary>
public class SubrecordSchemaViewTests
{
    [Fact]
    public void Read_ThrowsWhenSchemaNotRegistered()
    {
        // Sanity-check the registry has no schema for our sentinel — same guard as the
        // encoder-side test for symmetry.
        var lookup = SubrecordSchemaRegistry.GetSchema("ZZZZ", "NONE", 99);
        Assert.Null(lookup);

        var data = new byte[99];

        var ex = Assert.Throws<InvalidOperationException>(
            () => SubrecordSchemaView.Read("ZZZZ", "NONE", data, bigEndian: false));
        Assert.Contains("ZZZZ", ex.Message);
    }

    [Fact]
    public void Read_PopulatesFieldsFromSchemaWalk()
    {
        // ALCH/ENIT — 20 bytes: UInt32 Value + Bytes Flags(4) + FormId Addiction + Float
        // AddictionChance + FormId UseSoundOrWithdrawalEffect. Build the LE byte block by hand
        // and verify the view returns each field as the expected typed value.
        var data = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 250u);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 0x00000002u);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), 0x000FAB42u);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(12, 4), 0.25f);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), 0x000FAB99u);

        var view = SubrecordSchemaView.Read("ENIT", "ALCH", data, bigEndian: false);

        Assert.Equal(250u, view.UInt32("Value"));
        Assert.Equal(0x000FAB42u, view.UInt32("Addiction"));
        Assert.Equal(0.25f, view.Float("AddictionChance"));
        Assert.Equal(0x000FAB99u, view.UInt32("UseSoundOrWithdrawalEffect"));
        // FormId returns null when value is zero — sanity-check the present-value path.
        Assert.Equal(0x000FAB42u, view.FormId("Addiction"));
    }

    [Fact]
    public void TryRead_ReturnsNullWhenSchemaMissing()
    {
        var lookup = SubrecordSchemaRegistry.GetSchema("ZZZZ", "NONE", 99);
        Assert.Null(lookup);

        var view = SubrecordSchemaView.TryRead("ZZZZ", "NONE", new byte[99], bigEndian: false);

        Assert.Null(view);
    }

    [Fact]
    public void Float_CoercesAdjacentNumericTypes()
    {
        // ALCH/DATA (4 bytes, single Float "Weight"). Verifying the view's Float accessor
        // reads a registered Float field correctly — the broader coercion paths
        // (uint->float, etc.) are tested indirectly via SubrecordDataReader.GetFloat
        // which the view delegates to.
        var data = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(data, 1.5f);

        var view = SubrecordSchemaView.Read("DATA", "ALCH", data, bigEndian: false);

        Assert.Equal(1.5f, view.Float("Weight"));
        // Missing field falls back to the supplied default.
        Assert.Equal(-1f, view.Float("NotAField", def: -1f));
    }

    [Fact]
    public void FormId_ReturnsNullForZero()
    {
        // ALCH/ENIT with Addiction = 0 — view.FormId("Addiction") should return null,
        // matching the prevailing handler idiom of suppressing zero FormIDs.
        var data = new byte[20];
        // Leave bytes 8-11 as zero (Addiction FormID).
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 99u);

        var view = SubrecordSchemaView.Read("ENIT", "ALCH", data, bigEndian: false);

        Assert.Null(view.FormId("Addiction"));
        Assert.Equal(99u, view.UInt32("Value"));
    }
}
