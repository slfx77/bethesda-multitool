using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v22 LAND + XCLR encoder tests. Covers the cell-render gap from xex21 (cells with
///     XCLW water height but no LAND record → appeared flooded) and XCLR radiation-region
///     loss on cell overrides.
/// </summary>
public class LandAndXclrEncoderTests
{
    // ===================================================================================
    // LandEncoder
    // ===================================================================================

    [Fact]
    public void Land_Encode_ProducesDataVnmlVhgtInOrder()
    {
        var heightmap = new LandHeightmap
        {
            HeightOffset = 125.0f,
            HeightDeltas = new sbyte[33 * 33]
        };

        var subs = LandEncoder.Encode(heightmap);

        Assert.NotNull(subs);
        Assert.Equal(3, subs.Count);
        Assert.Equal("DATA", subs[0].Signature);
        Assert.Equal("VNML", subs[1].Signature);
        Assert.Equal("VHGT", subs[2].Signature);
    }

    [Fact]
    public void Land_Encode_VhgtCarriesHeightOffsetAndDeltas()
    {
        var deltas = new sbyte[33 * 33];
        // Sentinel pattern: index 0 = 1, index 1088 = -2, other zeros.
        deltas[0] = 1;
        deltas[1088] = -2;

        var heightmap = new LandHeightmap
        {
            HeightOffset = 12.5f,
            HeightDeltas = deltas
        };

        var subs = LandEncoder.Encode(heightmap);
        Assert.NotNull(subs);
        var vhgt = Assert.Single(subs, s => s.Signature == "VHGT").Bytes;

        Assert.Equal(1096, vhgt.Length); // float + 1089 sbyte + 3 padding
        Assert.Equal(12.5f, BitConverter.ToSingle(vhgt, 0));
        Assert.Equal(1, (sbyte)vhgt[4]);
        Assert.Equal(-2, (sbyte)vhgt[4 + 1088]);
    }

    [Fact]
    public void Land_Encode_VnmlIsFlatUpwardNormals()
    {
        var heightmap = new LandHeightmap
        {
            HeightOffset = 0f,
            HeightDeltas = new sbyte[33 * 33]
        };

        var subs = LandEncoder.Encode(heightmap);
        Assert.NotNull(subs);
        var vnml = Assert.Single(subs, s => s.Signature == "VNML").Bytes;

        Assert.Equal(33 * 33 * 3, vnml.Length); // 3267 bytes
        // Every vertex should be (0, 0, 127) — flat upward unit normal.
        for (var i = 0; i < 33 * 33; i++)
        {
            Assert.Equal(0, vnml[i * 3 + 0]);
            Assert.Equal(0, vnml[i * 3 + 1]);
            Assert.Equal(127, vnml[i * 3 + 2]);
        }
    }

    [Fact]
    public void Land_Encode_DataIsFourZeroBytes()
    {
        var heightmap = new LandHeightmap
        {
            HeightOffset = 0f,
            HeightDeltas = new sbyte[33 * 33]
        };

        var subs = LandEncoder.Encode(heightmap);
        Assert.NotNull(subs);
        var data = Assert.Single(subs, s => s.Signature == "DATA").Bytes;

        Assert.Equal(4, data.Length);
        Assert.All(data, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Land_Encode_ReturnsNullForWrongDeltaCount()
    {
        var heightmap = new LandHeightmap
        {
            HeightOffset = 0f,
            HeightDeltas = new sbyte[100] // not 1089
        };

        var subs = LandEncoder.Encode(heightmap);
        Assert.Null(subs);
    }

    // ===================================================================================
    // CellEncoder XCLR emission
    // ===================================================================================

    [Fact]
    public void CellEncoder_OmitsXclrWhenEmpty()
    {
        var encoder = new CellEncoder();
        var cell = new CellRecord
        {
            FormId = 0x1234,
            EditorId = "TestCell",
            GridX = 0,
            GridY = 0,
            Flags = 0
        };

        var result = encoder.Encode(cell);
        Assert.DoesNotContain(result.Subrecords, s => s.Signature == "XCLR");
    }

    [Fact]
    public void CellEncoder_EmitsXclrFormIdArray()
    {
        var encoder = new CellEncoder();
        var cell = new CellRecord
        {
            FormId = 0x1234,
            EditorId = "TestCell",
            GridX = 0,
            GridY = 0,
            Flags = 0,
            RadiationRegionFormIds = [0x000ABCDEu, 0x00012345u, 0x000F0F0Fu]
        };

        var result = encoder.Encode(cell);
        var xclr = Assert.Single(result.Subrecords, s => s.Signature == "XCLR").Bytes;

        Assert.Equal(12, xclr.Length); // 3 FormIDs × 4 bytes
        Assert.Equal(0x000ABCDEu, BinaryPrimitives.ReadUInt32LittleEndian(xclr.AsSpan(0, 4)));
        Assert.Equal(0x00012345u, BinaryPrimitives.ReadUInt32LittleEndian(xclr.AsSpan(4, 4)));
        Assert.Equal(0x000F0F0Fu, BinaryPrimitives.ReadUInt32LittleEndian(xclr.AsSpan(8, 4)));
    }

    [Fact]
    public void CellEncoder_XclrEmitsAfterXclw()
    {
        // Canonical subrecord order: XCLW (water height) should precede XCLR (radiation).
        // fopdoc CELL order: ... XCLW, XCLR, XCLL, XCMT, XCCM, ...
        var encoder = new CellEncoder();
        var cell = new CellRecord
        {
            FormId = 0x1234,
            EditorId = "TestCell",
            GridX = 0,
            GridY = 0,
            Flags = 0x02, // HasWater
            WaterHeight = 100.0f,
            RadiationRegionFormIds = [0x1u]
        };

        var result = encoder.Encode(cell);
        var sigs = result.Subrecords.Select(s => s.Signature).ToList();
        var xclwIdx = sigs.IndexOf("XCLW");
        var xclrIdx = sigs.IndexOf("XCLR");

        Assert.True(xclwIdx >= 0, "XCLW missing");
        Assert.True(xclrIdx >= 0, "XCLR missing");
        Assert.True(xclwIdx < xclrIdx, "XCLW must come before XCLR");
    }
}
