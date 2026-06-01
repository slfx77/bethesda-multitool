using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing.Subrecords;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Synthetic-byte tests for <see cref="LandSubrecordParser" />. Each test builds a
///     LAND record's data section from raw bytes (VHGT / VCLR / BTXT / ATXT / VTXT / VTEX)
///     and asserts the parser populates <see cref="LandVisualData" /> with correct values,
///     correct endian conversion, and correct per-field provenance.
/// </summary>
public class LandSubrecordParserTests
{
    private const int VnmlSize = 33 * 33 * 3;

    [Fact]
    public void Parses_VclrPayload_BigEndian()
    {
        var vclr = new byte[VnmlSize];
        for (var i = 0; i < vclr.Length; i++)
        {
            vclr[i] = (byte)(i % 256);
        }

        var data = BuildRecord(isBigEndian: true, ("VCLR", vclr));
        var result = LandSubrecordParser.Parse(data, data.Length, isBigEndian: true);

        Assert.NotNull(result.VisualData);
        Assert.NotNull(result.VisualData!.VertexColors);
        Assert.Equal(VnmlSize, result.VisualData.VertexColors!.Length);
        Assert.Equal(100, result.VisualData.VertexColors[100]);
        Assert.Equal(VisualDataSource.Dmp, result.VisualData.VertexColorsSource);
        Assert.Equal(VisualDataSource.Dmp, result.VisualData.Source);
        Assert.Equal(VnmlSize, result.VclrByteCount);
    }

    [Fact]
    public void Parses_VclrPayload_MasterEsm_LittleEndian()
    {
        var vclr = new byte[VnmlSize];
        var data = BuildRecord(isBigEndian: false, ("VCLR", vclr));
        var result = LandSubrecordParser.Parse(data, data.Length, isBigEndian: false);

        Assert.NotNull(result.VisualData);
        Assert.Equal(VisualDataSource.MasterEsm, result.VisualData!.VertexColorsSource);
        Assert.Equal(VisualDataSource.MasterEsm, result.VisualData.Source);
    }

    [Fact]
    public void Parses_BtxtPerQuadrant_WithFormId()
    {
        var subrecords = new (string Sig, byte[] Bytes)[4];
        for (var q = 0; q < 4; q++)
        {
            var payload = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(payload, 0x01000001u + (uint)q);
            payload[4] = (byte)q;
            payload[5] = 0x00;
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6), 0);
            subrecords[q] = ("BTXT", payload);
        }

        var data = BuildRecord(isBigEndian: true, subrecords);
        var result = LandSubrecordParser.Parse(data, data.Length, isBigEndian: true);

        Assert.NotNull(result.VisualData);
        Assert.Equal(4, result.VisualData!.TextureLayers.Count);
        Assert.Equal(4, result.BtxtCount);
        for (var q = 0; q < 4; q++)
        {
            var layer = result.VisualData.TextureLayers[q];
            Assert.Equal(LandTextureLayerKind.Base, layer.Kind);
            Assert.Equal(0x01000001u + (uint)q, layer.TextureFormId);
            Assert.Equal((byte)q, layer.Quadrant);
        }
        Assert.Equal(VisualDataSource.Dmp, result.VisualData.TextureLayersSource);
    }

    [Fact]
    public void Parses_AtxtThenVtxt_AttachesBlendEntries()
    {
        var atxt = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(atxt, 0x02000005u);
        atxt[4] = 2; // quadrant
        atxt[5] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(atxt.AsSpan(6), 3); // layer

        var vtxt = new byte[8 * 3];
        WriteBlendEntryBigEndian(vtxt.AsSpan(0), position: 10, opacity: 0.5f);
        WriteBlendEntryBigEndian(vtxt.AsSpan(8), position: 20, opacity: 1.0f);
        WriteBlendEntryBigEndian(vtxt.AsSpan(16), position: 30, opacity: 0.25f);

        var data = BuildRecord(isBigEndian: true, ("ATXT", atxt), ("VTXT", vtxt));
        var result = LandSubrecordParser.Parse(data, data.Length, isBigEndian: true);

        Assert.NotNull(result.VisualData);
        Assert.Single(result.VisualData!.TextureLayers);
        var layer = result.VisualData.TextureLayers[0];
        Assert.Equal(LandTextureLayerKind.Alpha, layer.Kind);
        Assert.Equal(3, layer.BlendEntries.Count);
        Assert.Equal(10, layer.BlendEntries[0].Position);
        Assert.Equal(0.5f, layer.BlendEntries[0].Opacity);
        Assert.Equal(1.0f, layer.BlendEntries[1].Opacity);
        Assert.Equal(0.25f, layer.BlendEntries[2].Opacity);
        Assert.Equal(0, result.UnattachedVtxtCount);
    }

    [Fact]
    public void Parses_VtxtWithoutPrecedingAtxt_CountsAsUnattached()
    {
        var vtxt = new byte[8];
        WriteBlendEntryBigEndian(vtxt, position: 5, opacity: 0.75f);

        var data = BuildRecord(isBigEndian: true, ("VTXT", vtxt));
        var result = LandSubrecordParser.Parse(data, data.Length, isBigEndian: true);

        Assert.Equal(1, result.UnattachedVtxtCount);
        Assert.Equal(8, result.UnattachedVtxtByteCount);
        Assert.True(result.VisualData?.TextureLayers.Count is null or 0);
    }

    [Fact]
    public void Parses_VtexIndices_BigEndian()
    {
        var vtex = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(vtex.AsSpan(0), 0xDEADBEEF);
        BinaryPrimitives.WriteUInt32BigEndian(vtex.AsSpan(4), 0x12345678);
        BinaryPrimitives.WriteUInt32BigEndian(vtex.AsSpan(8), 0u);
        BinaryPrimitives.WriteUInt32BigEndian(vtex.AsSpan(12), 0xFFFFFFFF);

        var data = BuildRecord(isBigEndian: true, ("VTEX", vtex));
        var result = LandSubrecordParser.Parse(data, data.Length, isBigEndian: true);

        Assert.NotNull(result.VisualData);
        Assert.NotNull(result.VisualData!.TextureIndices);
        Assert.Equal([0xDEADBEEF, 0x12345678, 0u, 0xFFFFFFFFu], result.VisualData.TextureIndices);
        Assert.Equal(VisualDataSource.Dmp, result.VisualData.TextureIndicesSource);
    }

    [Fact]
    public void ParseVisualOnly_SkipsHeightmap_ButPreservesVisualData()
    {
        var vclr = new byte[VnmlSize];
        var data = BuildRecord(isBigEndian: false, ("VCLR", vclr));
        var visual = LandSubrecordParser.ParseVisualOnly(data, data.Length, isBigEndian: false);

        Assert.NotNull(visual);
        Assert.True(visual!.HasVertexColors);
        Assert.Equal(VisualDataSource.MasterEsm, visual.Source);
    }

    [Fact]
    public void MergeCategories_PrefersRuntime_FillsFromMasterEsm()
    {
        var runtime = new LandVisualData
        {
            TextureLayers =
            {
                new LandTextureLayer { Kind = LandTextureLayerKind.Base, TextureFormId = 0x123, Quadrant = 0 }
            },
            TextureLayersSource = VisualDataSource.Runtime,
            Source = VisualDataSource.Runtime
        };
        var master = new LandVisualData
        {
            VertexColors = new byte[VnmlSize],
            VertexColorsSource = VisualDataSource.MasterEsm,
            TextureLayers =
            {
                new LandTextureLayer { Kind = LandTextureLayerKind.Base, TextureFormId = 0x999, Quadrant = 0 }
            },
            TextureLayersSource = VisualDataSource.MasterEsm,
            Source = VisualDataSource.MasterEsm
        };

        var merged = LandVisualData.MergeCategories(runtime, master);

        Assert.NotNull(merged);
        Assert.Equal(VisualDataSource.Runtime, merged!.TextureLayersSource);
        Assert.Equal(0x123u, merged.TextureLayers[0].TextureFormId);
        Assert.Equal(VisualDataSource.MasterEsm, merged.VertexColorsSource);
        Assert.Equal(VisualDataSource.Merged, merged.Source);
    }

    [Fact]
    public void MergeCategories_AllFromMaster_AggregateIsMasterEsm()
    {
        var master = new LandVisualData
        {
            VertexColors = new byte[VnmlSize],
            VertexColorsSource = VisualDataSource.MasterEsm,
            TextureLayers =
            {
                new LandTextureLayer { Kind = LandTextureLayerKind.Base, TextureFormId = 0x456, Quadrant = 1 }
            },
            TextureLayersSource = VisualDataSource.MasterEsm,
            Source = VisualDataSource.MasterEsm
        };

        var merged = LandVisualData.MergeCategories(null, master);

        Assert.NotNull(merged);
        Assert.Equal(VisualDataSource.MasterEsm, merged!.Source);
    }

    [Fact]
    public void RoundTrip_MasterEsm_VclrAndBtxt_ProducesIdenticalEncoderBytes()
    {
        var vclr = new byte[VnmlSize];
        for (var i = 0; i < vclr.Length; i++)
        {
            vclr[i] = (byte)((i * 7) % 256);
        }

        var btxt = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(btxt, 0x03000010u);
        btxt[4] = 0; // quadrant
        btxt[5] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(btxt.AsSpan(6), 0);

        var data = BuildRecord(isBigEndian: false, ("VCLR", vclr), ("BTXT", btxt));
        var visual = LandSubrecordParser.ParseVisualOnly(data, data.Length, isBigEndian: false);

        Assert.NotNull(visual);
        var heightmap = new LandHeightmap
        {
            HeightOffset = 0f,
            HeightDeltas = new sbyte[33 * 33]
        };
        var subs = LandEncoder.Encode(heightmap, visual);

        Assert.NotNull(subs);
        var encodedVclr = subs!.SingleOrDefault(s => s.Signature == "VCLR");
        Assert.NotNull(encodedVclr);
        Assert.Equal(vclr, encodedVclr!.Bytes);

        var encodedBtxts = subs.Where(s => s.Signature == "BTXT").ToList();
        Assert.Single(encodedBtxts);
    }

    private static byte[] BuildRecord(bool isBigEndian, params (string Sig, byte[] Bytes)[] subrecords)
    {
        var totalSize = 0;
        foreach (var (_, bytes) in subrecords)
        {
            totalSize += 6 + bytes.Length;
        }

        var buffer = new byte[totalSize];
        var offset = 0;
        foreach (var (sig, bytes) in subrecords)
        {
            // EsmSubrecordUtils.IterateSubrecords reverses the 4 signature bytes when
            // bigEndian=true (Xbox 360 storage order is reversed). Write the bytes in the
            // order the iterator expects so the parser sees the canonical signature.
            var sigBytes = Encoding.ASCII.GetBytes(sig);
            if (isBigEndian)
            {
                Array.Reverse(sigBytes);
            }

            sigBytes.CopyTo(buffer.AsSpan(offset));

            if (isBigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset + 4), (ushort)bytes.Length);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 4), (ushort)bytes.Length);
            }

            bytes.CopyTo(buffer.AsSpan(offset + 6));
            offset += 6 + bytes.Length;
        }

        return buffer;
    }

    private static void WriteBlendEntryBigEndian(Span<byte> dest, ushort position, float opacity)
    {
        BinaryPrimitives.WriteUInt16BigEndian(dest, position);
        dest[2] = 0;
        dest[3] = 0;
        BinaryPrimitives.WriteSingleBigEndian(dest[4..], opacity);
    }
}
