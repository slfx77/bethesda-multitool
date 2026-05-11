using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

public class CellEncoderTests
{
    [Fact]
    public void Encode_AlwaysEmitsEdidAndData()
    {
        var cell = new CellRecord { FormId = 0x42, EditorId = "TestCell" };

        var encoded = new CellEncoder().Encode(cell);

        Assert.Contains(encoded.Subrecords, s => s.Signature == "EDID");
        Assert.Contains(encoded.Subrecords, s => s.Signature == "DATA");
    }

    [Fact]
    public void Encode_InteriorCell_DataHasInteriorBit()
    {
        // v4 respects the model's IsInterior (computed from Flags bit 0) instead of v3's
        // hardcoded "force bit 0 on". Caller is responsible for setting Flags correctly.
        var cell = new CellRecord { FormId = 0x42, EditorId = "I", Flags = 0x01 };

        var encoded = new CellEncoder().Encode(cell);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Single(data.Bytes);
        Assert.Equal(0x01, data.Bytes[0] & 0x01);
    }

    [Fact]
    public void Encode_PreservesOtherCellFlagsAlongsideInteriorBit()
    {
        // Interior cell with public-place bit also set.
        var cell = new CellRecord { FormId = 0x42, EditorId = "I", Flags = 0x21 };
        var encoded = new CellEncoder().Encode(cell);
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");

        Assert.Equal(0x21, data.Bytes[0]); // interior + public-place
    }

    [Fact]
    public void Encode_OmitsXclwWhenWaterHeightUnset()
    {
        var cell = new CellRecord { FormId = 0x42, EditorId = "I", WaterHeight = null };
        var encoded = new CellEncoder().Encode(cell);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "XCLW");
    }

    [Fact]
    public void Encode_EmitsXclwWhenWaterHeightSet()
    {
        var cell = new CellRecord { FormId = 0x42, EditorId = "I", WaterHeight = -42.5f };
        var encoded = new CellEncoder().Encode(cell);
        var xclw = Assert.Single(encoded.Subrecords, s => s.Signature == "XCLW");
        Assert.Equal(-42.5f, BinaryPrimitives.ReadSingleLittleEndian(xclw.Bytes));
    }

    [Fact]
    public void Encode_EmitsLightingTemplateAndInheritanceFlagsWhenSet()
    {
        var cell = new CellRecord
        {
            FormId = 0x42,
            EditorId = "I",
            LightingTemplateFormId = 0x123456,
            LightingTemplateInheritanceFlags = 0x07
        };

        var encoded = new CellEncoder().Encode(cell);

        Assert.Contains(encoded.Subrecords, s => s.Signature == "LTMP");
        Assert.Contains(encoded.Subrecords, s => s.Signature == "LNAM");
    }

    [Fact]
    public void Encode_WarnsWhenEditorIdMissing()
    {
        var cell = new CellRecord { FormId = 0x42, EditorId = null };
        var encoded = new CellEncoder().Encode(cell);
        Assert.NotEmpty(encoded.Warnings);
    }
}
