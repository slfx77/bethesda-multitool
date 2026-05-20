using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

public class RuntimeWorldReaderLandVisualTests
{
    private const uint BaseVa = 0x40000000;
    private const uint LandVa = BaseVa + 0x0100;
    private const uint LoadedLandVa = BaseVa + 0x0200;
    private const uint TextureArrayVa = BaseVa + 0x0300;
    private const uint PercentArrayVa = BaseVa + 0x0400;
    private const uint PercentMaskVa = BaseVa + 0x0500;
    private const uint BadPercentMaskVa = BaseVa + 0x0A00;
    private const uint BaseTextureVa = BaseVa + 0x1000;
    private const uint AlphaTextureVa = BaseVa + 0x1100;
    private const uint InvalidTextureVa = BaseVa + 0x1200;
    private const uint TextureSetVa = BaseVa + 0x1300;
    private const uint GrassVa = BaseVa + 0x1400;
    private const uint StringVa = BaseVa + 0x1500;
    private const uint DiffusePathVa = BaseVa + 0x4000;
    private const uint NormalPathVa = BaseVa + 0x4100;
    private const uint AlternateDiffusePathVa = BaseVa + 0x4200;
    private const uint AlternateNormalPathVa = BaseVa + 0x4300;
    private const uint WrongInlinePathVa = BaseVa + 0x4400;
    private const uint NiDiffuseTextureVa = BaseVa + 0x5000;
    private const uint NiNormalTextureVa = BaseVa + 0x5100;

    [Fact]
    public void ReadRuntimeLandData_ReconstructsTextureLayersFromRuntimeLoadedLandData()
    {
        var buffer = new byte[0x20000];
        WriteLand(buffer, 0x000AAAAA);
        WriteLoadedLand(buffer, baseTextureVa: BaseTextureVa, textureArrayVa: TextureArrayVa,
            percentArrayVa: PercentArrayVa);
        WriteUInt32BE(buffer, Offset(TextureArrayVa), AlphaTextureVa);
        WriteUInt32BE(buffer, Offset(TextureArrayVa) + 4, InvalidTextureVa);
        WriteUInt32BE(buffer, Offset(PercentArrayVa), PercentMaskVa);
        WriteUInt32BE(buffer, Offset(PercentArrayVa) + 4, PercentMaskVa);
        WritePercentMask(buffer, PercentMaskVa, (18, 0.75f), (288, 1.0f));

        WriteLandTexture(buffer, BaseTextureVa, 0x00111111, "RuntimeBaseTexture");
        WriteLandTexture(buffer, AlphaTextureVa, 0x00222222, "RuntimeAlphaTexture");
        WriteFormHeader(buffer, InvalidTextureVa, 0x3A, 0x00333333);
        WriteTextureSet(buffer);
        WriteFormHeader(buffer, GrassVa, 0x24, 0x00555555);

        var land = ReadLand(buffer, 0x000AAAAA);

        Assert.NotNull(land);
        Assert.NotNull(land.VisualData);
        Assert.Equal("runtime-land", land.VisualData.Source);

        var layers = land.VisualData.TextureLayers;
        Assert.Equal(2, layers.Count);

        var baseLayer = layers[0];
        Assert.Equal(LandTextureLayerKind.Base, baseLayer.Kind);
        Assert.Equal(0x00111111u, baseLayer.TextureFormId);
        Assert.Equal(0, baseLayer.Quadrant);

        var alphaLayer = layers[1];
        Assert.Equal(LandTextureLayerKind.Alpha, alphaLayer.Kind);
        Assert.Equal(0x00222222u, alphaLayer.TextureFormId);
        Assert.Equal(0, alphaLayer.Quadrant);
        Assert.Equal((ushort)0, alphaLayer.Layer);
        Assert.Equal([18, 288], alphaLayer.BlendEntries.Select(e => (int)e.Position).ToArray());
        Assert.Equal(0.75f, alphaLayer.BlendEntries[0].Opacity);
        Assert.Equal(1.0f, alphaLayer.BlendEntries[1].Opacity);

        Assert.DoesNotContain(layers, l => l.TextureFormId == 0x00333333);

        var runtimeTextures = land.RuntimeLandTextures.OrderBy(t => t.FormId).ToArray();
        Assert.Equal([0x00111111u, 0x00222222u], runtimeTextures.Select(t => t.FormId).ToArray());
        Assert.All(runtimeTextures, t =>
        {
            Assert.Equal(0x00444444u, t.TextureSetFormId);
            Assert.Equal(new byte[] { 1, 2, 3 }, t.HavokData);
            Assert.Equal(new byte[] { 4 }, t.SpecularData);
            Assert.Equal([0x00555555u], t.GrassFormIds);
        });

        var runtimeTextureSet = Assert.Single(land.RuntimeTextureSets);
        Assert.Equal(0x00444444u, runtimeTextureSet.FormId);
        Assert.Equal("RuntimeTerrainTextureSet", runtimeTextureSet.EditorId);
        Assert.Equal("textures\\landscape\\runtime_diffuse.dds", runtimeTextureSet.DiffuseTexture);
        Assert.Equal("Textures\\Landscape\\RuntimeNormal.dds", runtimeTextureSet.NormalTexture);
        Assert.Equal((ushort)0x1234, runtimeTextureSet.Flags);

        var textureDiag = Assert.Single(land.Diagnostics!.QuadTextureArrays, d => d.Pointer.IsMapped);
        Assert.Equal(2, textureDiag.SampledPointerCount);
        Assert.Equal(1, textureDiag.ResolvedTextureCount);
        Assert.Equal([0x00222222u], textureDiag.TextureFormIds);
    }

    [Fact]
    public void ParseAll_MergesTextureSetsRecoveredFromRuntimeLandTexturePointers()
    {
        var runtimeTextureSet = new TextureSetRecord
        {
            FormId = 0x00ABCDEF,
            EditorId = "RuntimeTerrainTextureSet",
            DiffuseTexture = "textures\\landscape\\runtime_diffuse.dds",
            NormalTexture = "textures\\landscape\\runtime_normal.dds",
            Flags = 0x1234,
            IsBigEndian = true
        };
        var scanResult = new EsmRecordScanResult
        {
            LandRecords =
            [
                new ExtractedLandRecord
                {
                    Header = new DetectedMainRecord("LAND", 0, 0, 0x00123456, 0, true),
                    RuntimeTextureSets = [runtimeTextureSet]
                }
            ]
        };

        var records = new RecordParser(scanResult).ParseAll();

        var parsedTextureSet = Assert.Single(records.TextureSets);
        Assert.Equal(0x00ABCDEFu, parsedTextureSet.FormId);
        Assert.Equal("RuntimeTerrainTextureSet", parsedTextureSet.EditorId);
        Assert.Equal("textures\\landscape\\runtime_diffuse.dds", parsedTextureSet.DiffuseTexture);
    }

    [Fact]
    public void ReadRuntimeLandData_RecoversTextureSetPathsFromNiSourcePointerArray()
    {
        var buffer = new byte[0x20000];
        WriteLand(buffer, 0x000CCCCC);
        WriteLoadedLand(buffer, baseTextureVa: BaseTextureVa, textureArrayVa: 0, percentArrayVa: 0);
        WriteLandTexture(buffer, BaseTextureVa, 0x00111111, "RuntimeBaseTexture");
        WriteTextureSetWithNiSourcePointerArray(buffer);

        var land = ReadLand(buffer, 0x000CCCCC);

        var textureSet = Assert.Single(land!.RuntimeTextureSets);
        Assert.Equal("textures\\landscape\\direct_diffuse.dds", textureSet.DiffuseTexture);
        Assert.Equal("textures\\landscape\\direct_diffuse_n.dds", textureSet.NormalTexture);
    }

    [Fact]
    public void ReadRuntimeLandData_SelectsHighestScoringTextureSetPathLayout()
    {
        var buffer = new byte[0x20000];
        WriteLand(buffer, 0x000DDDDD);
        WriteLoadedLand(buffer, baseTextureVa: BaseTextureVa, textureArrayVa: 0, percentArrayVa: 0);
        WriteLandTexture(buffer, BaseTextureVa, 0x00111111, "RuntimeBaseTexture");
        WriteTextureSetWithFileEntriesAndInlineNoise(buffer);

        var land = ReadLand(buffer, 0x000DDDDD);

        var textureSet = Assert.Single(land!.RuntimeTextureSets);
        Assert.Equal("textures\\landscape\\scored_diffuse.dds", textureSet.DiffuseTexture);
        Assert.Equal("textures\\landscape\\scored_diffuse_n.dds", textureSet.NormalTexture);
    }

    [Fact]
    public void ReadRuntimeLandData_SkipsAlphaLayerWhenPercentMaskIsInvalid()
    {
        var buffer = new byte[0x20000];
        WriteLand(buffer, 0x000BBBBB);
        WriteLoadedLand(buffer, baseTextureVa: BaseTextureVa, textureArrayVa: TextureArrayVa,
            percentArrayVa: PercentArrayVa);
        WriteUInt32BE(buffer, Offset(TextureArrayVa), AlphaTextureVa);
        WriteUInt32BE(buffer, Offset(PercentArrayVa), BadPercentMaskVa);
        WriteBadPercentMask(buffer, BadPercentMaskVa);

        WriteLandTexture(buffer, BaseTextureVa, 0x00111111, "RuntimeBaseTexture");
        WriteLandTexture(buffer, AlphaTextureVa, 0x00222222, "RuntimeAlphaTexture");
        WriteTextureSet(buffer);
        WriteFormHeader(buffer, GrassVa, 0x24, 0x00555555);

        var land = ReadLand(buffer, 0x000BBBBB);

        Assert.NotNull(land);
        Assert.NotNull(land.VisualData);
        Assert.Single(land.VisualData.TextureLayers);
        Assert.Equal(LandTextureLayerKind.Base, land.VisualData.TextureLayers[0].Kind);
    }

    private static RuntimeLoadedLandData? ReadLand(byte[] buffer, uint formId)
    {
        var accessor = new SparseMemoryAccessor();
        accessor.AddRange(0, buffer);
        var minidumpInfo = new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = BaseVa,
                    FileOffset = 0,
                    Size = buffer.Length
                }
            ]
        };
        var context = new RuntimeMemoryContext(accessor, buffer.Length, minidumpInfo);
        var reader = new RuntimeWorldReader(context);

        return reader.ReadRuntimeLandData(new RuntimeEditorIdEntry
        {
            EditorId = "RuntimeLand",
            FormId = formId,
            FormType = 0x44,
            TesFormOffset = Offset(LandVa)
        });
    }

    private static void WriteLand(byte[] buffer, uint formId)
    {
        WriteFormHeader(buffer, LandVa, 0x44, formId);
        WriteUInt32BE(buffer, Offset(LandVa) + 56, LoadedLandVa);
    }

    private static void WriteLoadedLand(
        byte[] buffer,
        uint baseTextureVa,
        uint textureArrayVa,
        uint percentArrayVa)
    {
        var offset = Offset(LoadedLandVa);
        WriteUInt32BE(buffer, offset + 32, baseTextureVa);
        WriteUInt32BE(buffer, offset + 48, textureArrayVa);
        WriteUInt32BE(buffer, offset + 64, percentArrayVa);
        WriteInt32BE(buffer, offset + 152, 1);
        WriteInt32BE(buffer, offset + 156, -2);
        WriteSingleBE(buffer, offset + 160, 0f);
    }

    private static void WriteLandTexture(byte[] buffer, uint va, uint formId, string editorId)
    {
        WriteFormHeader(buffer, va, 0x12, formId);
        WriteBsString(buffer, va + 16, StringVa + (formId & 0xFF) * 0x40, editorId);
        WriteUInt32BE(buffer, Offset(va) + 40, TextureSetVa);
        buffer[Offset(va) + 44] = 1;
        buffer[Offset(va) + 45] = 2;
        buffer[Offset(va) + 46] = 3;
        buffer[Offset(va) + 47] = 4;
        WriteUInt32BE(buffer, Offset(va) + 48, GrassVa);
    }

    private static void WriteTextureSet(byte[] buffer)
    {
        WriteFormHeader(buffer, TextureSetVa, 0x04, 0x00444444);
        WriteBsString(buffer, TextureSetVa + 16, StringVa + 0x1200, "RuntimeTerrainTextureSet");
        WriteInt16BE(buffer, Offset(TextureSetVa) + 52, -1);
        WriteInt16BE(buffer, Offset(TextureSetVa) + 54, -2);
        WriteInt16BE(buffer, Offset(TextureSetVa) + 56, -3);
        WriteInt16BE(buffer, Offset(TextureSetVa) + 58, 1);
        WriteInt16BE(buffer, Offset(TextureSetVa) + 60, 2);
        WriteInt16BE(buffer, Offset(TextureSetVa) + 62, 3);
        WriteTextureInlineEntry(buffer, 0, DiffusePathVa, "textures/landscape/runtime_diffuse.dds");
        WriteTextureInlineEntry(buffer, 1, NormalPathVa, "Data\\Textures\\Landscape\\RuntimeNormal.dds");
        WriteUInt16BE(buffer, Offset(TextureSetVa) + 160, 0x1234);
    }

    private static void WriteTextureSetWithNiSourcePointerArray(byte[] buffer)
    {
        WriteFormHeader(buffer, TextureSetVa, 0x04, 0x00444444);
        WriteBsString(buffer, TextureSetVa + 16, StringVa + 0x1200, "RuntimeTerrainTextureSet");
        WriteUInt32BE(buffer, Offset(TextureSetVa) + 72, NiDiffuseTextureVa);
        WriteUInt32BE(buffer, Offset(TextureSetVa) + 76, NiNormalTextureVa);
        WriteNiSourceTexture(buffer, NiDiffuseTextureVa, AlternateDiffusePathVa,
            "textures\\landscape\\direct_diffuse.dds");
        WriteNiSourceTexture(buffer, NiNormalTextureVa, AlternateNormalPathVa,
            "textures\\landscape\\direct_diffuse_n.dds");
    }

    private static void WriteTextureSetWithFileEntriesAndInlineNoise(byte[] buffer)
    {
        WriteFormHeader(buffer, TextureSetVa, 0x04, 0x00444444);
        WriteBsString(buffer, TextureSetVa + 16, StringVa + 0x1200, "RuntimeTerrainTextureSet");
        WriteUInt32BE(buffer, Offset(TextureSetVa) + 76, WrongInlinePathVa);
        WriteAsciiNullTerminated(buffer, WrongInlinePathVa, "textures\\clutter\\wrong.dds");
        WriteUInt32BE(buffer, Offset(TextureSetVa) + 164, AlternateDiffusePathVa);
        WriteUInt32BE(buffer, Offset(TextureSetVa) + 168, AlternateNormalPathVa);
        WriteAsciiNullTerminated(buffer, AlternateDiffusePathVa, "textures\\landscape\\scored_diffuse.dds");
        WriteAsciiNullTerminated(buffer, AlternateNormalPathVa, "textures\\landscape\\scored_diffuse_n.dds");
    }

    private static void WriteNiSourceTexture(byte[] buffer, uint va, uint pathVa, string path)
    {
        WriteUInt32BE(buffer, Offset(va) + 4, 1);
        WriteUInt32BE(buffer, Offset(va) + 48, pathVa);
        WriteAsciiNullTerminated(buffer, pathVa, path);
    }

    private static void WriteTextureInlineEntry(byte[] buffer, int slot, uint pathVa, string path)
    {
        var offset = Offset(TextureSetVa) + 72 + slot * 12;
        WriteUInt32BE(buffer, offset, 0x82015E40);
        WriteUInt32BE(buffer, offset + 4, pathVa);
        WriteUInt32BE(buffer, offset + 8, 0x001D001D);
        WriteAsciiNullTerminated(buffer, pathVa, path);
    }

    private static void WriteAsciiNullTerminated(byte[] buffer, uint va, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        bytes.CopyTo(buffer.AsSpan(Offset(va), bytes.Length));
        buffer[Offset(va) + bytes.Length] = 0;
    }

    private static void WritePercentMask(byte[] buffer, uint va, params (int Position, float Opacity)[] values)
    {
        for (var i = 0; i < 17 * 17; i++)
        {
            WriteSingleBE(buffer, Offset(va) + i * 4, 0f);
        }

        foreach (var (position, opacity) in values)
        {
            WriteSingleBE(buffer, Offset(va) + position * 4, opacity);
        }
    }

    private static void WriteBadPercentMask(byte[] buffer, uint va)
    {
        for (var i = 0; i < 17 * 17; i++)
        {
            WriteSingleBE(buffer, Offset(va) + i * 4, i == 0 ? float.NaN : 0.5f);
        }
    }

    private static void WriteFormHeader(byte[] buffer, uint va, byte formType, uint formId)
    {
        var offset = Offset(va);
        buffer[offset + 4] = formType;
        WriteUInt32BE(buffer, offset + 12, formId);
    }

    private static void WriteBsString(byte[] buffer, uint fieldVa, uint stringVa, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        WriteUInt32BE(buffer, Offset(fieldVa), stringVa);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(Offset(fieldVa) + 4, 2), (ushort)bytes.Length);
        bytes.CopyTo(buffer.AsSpan(Offset(stringVa), bytes.Length));
    }

    private static void WriteUInt32BE(byte[] buffer, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset, 4), value);
    }

    private static void WriteInt32BE(byte[] buffer, int offset, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset, 4), value);
    }

    private static void WriteInt16BE(byte[] buffer, int offset, short value)
    {
        BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(offset, 2), value);
    }

    private static void WriteUInt16BE(byte[] buffer, int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), value);
    }

    private static void WriteSingleBE(byte[] buffer, int offset, float value)
    {
        BinaryPrimitives.WriteSingleBigEndian(buffer.AsSpan(offset, 4), value);
    }

    private static int Offset(uint va) => checked((int)(va - BaseVa));
}
