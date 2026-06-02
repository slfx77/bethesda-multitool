using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Nav;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
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
    public void LandOverrideBuilder_RejectsFlatCandidateWhenMasterIsNonFlat()
    {
        var candidate = ExactHeightmap(_ => 128f);
        var master = ExactHeightmap((x, y) => 128f + x + y);

        Assert.True(LandOverrideBuilder.ShouldRejectFlatOverride(candidate, master));
    }

    [Fact]
    public void LandOverrideBuilder_KeepsFlatCandidateWhenMasterIsAlsoFlat()
    {
        var candidate = ExactHeightmap(_ => 128f);
        var master = ExactHeightmap(_ => 256f);

        Assert.False(LandOverrideBuilder.ShouldRejectFlatOverride(candidate, master));
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
    public void Land_Encode_VnmlUsesHeightSlope()
    {
        var exactHeights = new float[33, 33];
        for (var y = 0; y < 33; y++)
        {
            for (var x = 0; x < 33; x++)
            {
                exactHeights[y, x] = x * 16f;
            }
        }

        var heightmap = new LandHeightmap
        {
            HeightOffset = 0f,
            HeightDeltas = new sbyte[33 * 33],
            ExactHeights = exactHeights
        };

        var subs = LandEncoder.Encode(heightmap);
        Assert.NotNull(subs);
        var vnml = Assert.Single(subs, s => s.Signature == "VNML").Bytes;
        var center = (16 * 33 + 16) * 3;

        Assert.Equal(33 * 33 * 3, vnml.Length);
        Assert.NotEqual(0, (sbyte)vnml[center]);
        Assert.Equal(0, (sbyte)vnml[center + 1]);
        Assert.InRange((sbyte)vnml[center + 2], 120, 127);
    }

    [Fact]
    public void Land_Encode_DataCarriesCanonicalExteriorFlag()
    {
        // Vanilla FNV exterior LANDs always have bit 4 (0x10) set in DATA. Quadrant bits
        // (0..3) are set when BTXT data is emitted for that quadrant; with no BTXTs they
        // stay clear. Prior behavior (DATA=0) made the engine treat the cell as having no
        // LAND data and the terrain rendered flat — see the Phase 9 Goodsprings bugfix.
        var heightmap = new LandHeightmap
        {
            HeightOffset = 0f,
            HeightDeltas = new sbyte[33 * 33]
        };

        var subs = LandEncoder.Encode(heightmap);
        Assert.NotNull(subs);
        var data = Assert.Single(subs, s => s.Signature == "DATA").Bytes;

        Assert.Equal(4, data.Length);
        Assert.Equal(0x10, data[0]);
        Assert.Equal(0, data[1]);
        Assert.Equal(0, data[2]);
        Assert.Equal(0, data[3]);
    }

    [Fact]
    public void Land_Encode_DataQuadrantBitsReflectBtxtQuadrants()
    {
        var heightmap = new LandHeightmap
        {
            HeightOffset = 0f,
            HeightDeltas = new sbyte[33 * 33]
        };
        var visualData = new LandVisualData
        {
            TextureLayers =
            [
                new LandTextureLayer
                {
                    Kind = LandTextureLayerKind.Base, TextureFormId = 0x00038A28u,
                    Quadrant = 0, Layer = 0
                },
                new LandTextureLayer
                {
                    Kind = LandTextureLayerKind.Base, TextureFormId = 0x00038A28u,
                    Quadrant = 2, Layer = 0
                }
            ]
        };

        var subs = LandEncoder.Encode(heightmap, visualData);
        Assert.NotNull(subs);
        var data = Assert.Single(subs, s => s.Signature == "DATA").Bytes;

        // Bit 4 (0x10) always; bit 0 for quad 0; bit 2 for quad 2; bits 1 + 3 clear.
        Assert.Equal(0x10 | 0x01 | 0x04, data[0]);
    }

    [Fact]
    public void Land_Encode_BtxtLayerIs65535_NotZero()
    {
        // BTXT (Base) layer must be 0xFFFF (the base-layer sentinel). Master FNV LANDs all
        // use 0xFFFF for BTXT; the prior encoder emitted 0 which confused the engine into
        // skipping the quadrant's base texture (contributed to flat-terrain rendering).
        var heightmap = new LandHeightmap
        {
            HeightOffset = 0f,
            HeightDeltas = new sbyte[33 * 33]
        };
        var visualData = new LandVisualData
        {
            TextureLayers =
            [
                new LandTextureLayer
                {
                    Kind = LandTextureLayerKind.Base, TextureFormId = 0x00038A28u,
                    Quadrant = 0, Layer = 0
                },
                new LandTextureLayer
                {
                    Kind = LandTextureLayerKind.Alpha, TextureFormId = 0x0001F958u,
                    Quadrant = 0, Layer = 3
                }
            ]
        };

        var subs = LandEncoder.Encode(heightmap, visualData);
        Assert.NotNull(subs);
        var btxt = Assert.Single(subs, s => s.Signature == "BTXT").Bytes;
        var btxtLayer = BinaryPrimitives.ReadUInt16LittleEndian(btxt.AsSpan(6, 2));
        Assert.Equal(0xFFFF, btxtLayer);

        var atxt = Assert.Single(subs, s => s.Signature == "ATXT").Bytes;
        var atxtLayer = BinaryPrimitives.ReadUInt16LittleEndian(atxt.AsSpan(6, 2));
        Assert.Equal(3, atxtLayer);
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

    [Fact]
    public void Land_Encode_EmitsVisualSubrecordsAfterVhgt()
    {
        var heightmap = new LandHeightmap
        {
            HeightOffset = 0f,
            HeightDeltas = new sbyte[33 * 33]
        };
        var vclr = Enumerable.Range(0, 33 * 33 * 3).Select(i => (byte)(i % 251)).ToArray();
        var visual = new LandVisualData
        {
            VertexColors = vclr,
            TextureIndices = [0x1000u, 0x2000u],
            TextureLayers =
            [
                new LandTextureLayer
                {
                    Kind = LandTextureLayerKind.Base,
                    TextureFormId = 0x11111111,
                    Quadrant = 1,
                    PlatformFlag = 2,
                    Layer = 0
                },
                new LandTextureLayer
                {
                    Kind = LandTextureLayerKind.Alpha,
                    TextureFormId = 0x22222222,
                    Quadrant = 3,
                    PlatformFlag = 4,
                    Layer = 5,
                    BlendEntries =
                    [
                        new LandTextureBlendEntry(12, 0xAA, 0xBB, 0.5f)
                    ]
                }
            ]
        };

        var subs = LandEncoder.Encode(heightmap, visual);

        Assert.NotNull(subs);
        Assert.Equal(["DATA", "VNML", "VHGT", "VCLR", "BTXT", "ATXT", "VTXT", "VTEX"],
            subs.Select(s => s.Signature).ToList());
        Assert.Equal(vclr, subs[3].Bytes);

        var btxt = subs[4].Bytes;
        Assert.Equal(0x11111111u, BinaryPrimitives.ReadUInt32LittleEndian(btxt.AsSpan(0, 4)));
        Assert.Equal(1, btxt[4]);
        Assert.Equal(2, btxt[5]);

        var atxt = subs[5].Bytes;
        Assert.Equal(0x22222222u, BinaryPrimitives.ReadUInt32LittleEndian(atxt.AsSpan(0, 4)));
        Assert.Equal(3, atxt[4]);
        Assert.Equal(4, atxt[5]);
        Assert.Equal((ushort)5, BinaryPrimitives.ReadUInt16LittleEndian(atxt.AsSpan(6, 2)));

        var vtxt = subs[6].Bytes;
        Assert.Equal((ushort)12, BinaryPrimitives.ReadUInt16LittleEndian(vtxt.AsSpan(0, 2)));
        Assert.Equal(0xAA, vtxt[2]);
        Assert.Equal(0xBB, vtxt[3]);
        Assert.Equal(0.5f, BinaryPrimitives.ReadSingleLittleEndian(vtxt.AsSpan(4, 4)));

        var vtex = subs[7].Bytes;
        Assert.Equal(0x1000u, BinaryPrimitives.ReadUInt32LittleEndian(vtex.AsSpan(0, 4)));
        Assert.Equal(0x2000u, BinaryPrimitives.ReadUInt32LittleEndian(vtex.AsSpan(4, 4)));
    }

    [Fact]
    public void Land_Encode_SkipsTextureLayersWithZeroFormId()
    {
        var heightmap = new LandHeightmap
        {
            HeightOffset = 0f,
            HeightDeltas = new sbyte[33 * 33]
        };
        var visual = new LandVisualData
        {
            TextureLayers =
            [
                new LandTextureLayer
                {
                    Kind = LandTextureLayerKind.Base,
                    TextureFormId = 0,
                    Quadrant = 0,
                    Layer = 0
                },
                new LandTextureLayer
                {
                    Kind = LandTextureLayerKind.Alpha,
                    TextureFormId = 0,
                    Quadrant = 1,
                    Layer = 0,
                    BlendEntries =
                    [
                        new LandTextureBlendEntry(12, 0, 0, 0.5f)
                    ]
                },
                new LandTextureLayer
                {
                    Kind = LandTextureLayerKind.Alpha,
                    TextureFormId = 0x22222222,
                    Quadrant = 2,
                    Layer = 0,
                    BlendEntries =
                    [
                        new LandTextureBlendEntry(13, 0, 0, 0.25f)
                    ]
                }
            ]
        };

        var subs = LandEncoder.Encode(heightmap, visual);

        Assert.NotNull(subs);
        Assert.DoesNotContain(subs, s => s.Signature == "BTXT");
        var atxt = Assert.Single(subs, s => s.Signature == "ATXT").Bytes;
        Assert.Equal(0x22222222u, BinaryPrimitives.ReadUInt32LittleEndian(atxt.AsSpan(0, 4)));
        var vtxt = Assert.Single(subs, s => s.Signature == "VTXT").Bytes;
        Assert.Equal((ushort)13, BinaryPrimitives.ReadUInt16LittleEndian(vtxt.AsSpan(0, 2)));
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
            Flags = 0x03, // Interior + HasWater
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

    private static LandHeightmap ExactHeightmap(Func<int, int, float> valueFactory)
    {
        var heights = new float[33, 33];
        for (var y = 0; y < 33; y++)
        {
            for (var x = 0; x < 33; x++)
            {
                heights[y, x] = valueFactory(x, y);
            }
        }

        return new LandHeightmap
        {
            HeightOffset = 0f,
            HeightDeltas = new sbyte[33 * 33],
            ExactHeights = heights
        };
    }

    private static LandHeightmap ExactHeightmap(Func<int, float> valueFactory)
    {
        return ExactHeightmap((x, _) => valueFactory(x));
    }
}
