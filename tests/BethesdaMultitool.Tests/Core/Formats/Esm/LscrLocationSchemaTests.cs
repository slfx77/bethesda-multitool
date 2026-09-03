using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm;

/// <summary>
///     LSCR LNAM is a 12-byte location (Direct FormID, Indirect FormID, Grid Y, Grid X per xEdit),
///     repeated per location. Until 2026-09-02 it fell through to the generic 4-byte "Load Screen
///     FormID" LNAM schema — byte-correct on retail only because every retail Indirect/grid tail is
///     zero. With a nonzero grid pair the two int16s must swap individually, never as one dword.
///     (Real-data verdict, three-way 2026-09-02: all LSCR convert byte-identical to PC except two
///     records whose LNAM ORDER the Xbox master itself reverses — genuine content, unordered list.)
/// </summary>
public class LscrLocationSchemaTests
{
    [Fact]
    public void Registry_HasTheTwelveByteLscrLocationSchema()
    {
        var schema = SubrecordSchemaRegistry.GetSchema("LNAM", "LSCR", 12);

        Assert.NotNull(schema);
        Assert.Equal("Load Screen Location", schema.Description);
    }

    [Fact]
    public void Read_BigEndianLocation_SwapsEachGridInt16Independently()
    {
        // Xbox (big-endian) bytes: Direct 0x0010CDA6, Indirect 0x000DA726, Grid Y -3, Grid X 17.
        var data = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), 0x0010CDA6u);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4, 4), 0x000DA726u);
        BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(8, 2), -3);
        BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(10, 2), 17);

        var view = SubrecordSchemaView.Read("LNAM", "LSCR", data, true);

        Assert.Equal(0x0010CDA6u, view.FormId("Direct"));
        Assert.Equal(0x000DA726u, view.FormId("Indirect"));
        // A whole-dword swap of the tail would read (17, -3) here; the per-field schema keeps
        // each int16 in its own slot.
        Assert.Equal(-3, Convert.ToInt32(view.Raw["Grid Y"]));
        Assert.Equal(17, Convert.ToInt32(view.Raw["Grid X"]));
    }
}
