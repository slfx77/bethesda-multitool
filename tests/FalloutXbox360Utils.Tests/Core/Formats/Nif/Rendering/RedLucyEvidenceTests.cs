using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis.Helpers;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;
using FalloutXbox360Utils.Core.Utils;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class RedLucyEvidenceTests(SampleFileFixture samples)
{
    private const uint BountyHunterDusterFormId = 0x0010D8DB;
    private const string OutfitDiffusePath = @"textures\armor\lucassimms\OutfitF.dds";
    private const string OutfitNormalPath = @"textures\armor\lucassimms\OutfitF_n.dds";

    [Fact]
    public void BountyHunterDuster_UsesDirectGenderModelsWithoutAlternateTextureBlob()
    {
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final ESM not available");

        var esm = EsmFileLoader.Load(samples.PcFinalEsm!, false);
        Assert.NotNull(esm);

        var record = DiffHelpers.FindRecordByFormId(
            esm.Data,
            esm.IsBigEndian,
            BountyHunterDusterFormId);
        Assert.NotNull(record);

        var recordData = NpcRecordDataReader.ReadRecordData(
            esm.Data,
            esm.IsBigEndian,
            record!);
        Assert.NotNull(recordData);

        var subrecords = EsmRecordParser.ParseSubrecords(recordData!, esm.IsBigEndian);
        var maleModel = EsmRecordParser.GetSubrecordString(
            Assert.IsType<AnalyzerSubrecordInfo>(
                EsmRecordParser.FindSubrecord(subrecords, "MODL")));
        var femaleModel = EsmRecordParser.GetSubrecordString(
            Assert.IsType<AnalyzerSubrecordInfo>(
                EsmRecordParser.FindSubrecord(subrecords, "MOD3")));

        Assert.EndsWith(".nif", maleModel, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".nif", femaleModel, StringComparison.OrdinalIgnoreCase);
        Assert.Null(EsmRecordParser.FindSubrecord(subrecords, "MODS"));
        Assert.Null(EsmRecordParser.FindSubrecord(subrecords, "MODT"));
    }

    [Fact]
    public void LucassimmsOutfitStaticMetadata_MatchesAcrossPcAndXboxSamples()
    {
        var pcNifPath = SampleFileFixture.FindSamplePath(
            @"Sample\Meshes\meshes_pc\meshes\armor\lucassimms\f\outfitf.nif");
        var xboxNifPath = SampleFileFixture.FindSamplePath(
            @"Sample\Meshes\meshes_360_final\meshes\armor\lucassimms\f\outfitf.nif");

        Assert.SkipWhen(pcNifPath is null, "PC lucassimms OutfitF NIF not available");
        Assert.SkipWhen(xboxNifPath is null, "Xbox lucassimms OutfitF NIF not available");

        var pcEvidence = LoadOutfitEvidence(pcNifPath!);
        var xboxEvidence = LoadOutfitEvidence(xboxNifPath!);

        Assert.NotEmpty(pcEvidence);
        Assert.NotEmpty(xboxEvidence);
        Assert.Equal(
            pcEvidence.OrderBy(e => e.ShapeName).Select(DescribeEvidence),
            xboxEvidence.OrderBy(e => e.ShapeName).Select(DescribeEvidence));
    }

    [Fact]
    public void Xbox360LucassimmsSpecularTexture_RemainsUnboundInStaticShaderSlots()
    {
        var xboxSpecularPath = SampleFileFixture.FindSamplePath(
            @"Sample\Textures\textures_360_final\textures\armor\lucassimms\outfitf_s.ddx");
        var xboxNifPath = SampleFileFixture.FindSamplePath(
            @"Sample\Meshes\meshes_360_final\meshes\armor\lucassimms\f\outfitf.nif");

        Assert.SkipWhen(
            xboxSpecularPath is null,
            "Xbox lucassimms specular texture not available");
        Assert.SkipWhen(xboxNifPath is null, "Xbox lucassimms OutfitF NIF not available");

        var evidence = LoadOutfitEvidence(xboxNifPath!);

        Assert.NotEmpty(evidence);
        Assert.All(
            evidence,
            submesh =>
            {
                Assert.DoesNotContain(
                    submesh.TextureSlots,
                    slot => slot != null &&
                            slot.Contains("outfitf_s", StringComparison.OrdinalIgnoreCase));
            });
    }

    [Fact]
    public void ReleaseBetaDumpSearch_FindsGenericOutfitTextureNamesButNoQualifiedLucassimmsOutfitPath()
    {
        var dumpChunkPath = SampleFileFixture.FindSamplePath(
            @"Sample\MemoryDump\Fallout_Release_Beta.xex10.dmp");

        Assert.SkipWhen(
            dumpChunkPath is null,
            "Release beta dump chunk not available");

        var dumpData = File.ReadAllBytes(dumpChunkPath!);

        Assert.NotEmpty(
            BinaryPatternSearcher.FindTextMatches(dumpData, "lucassimms"));
        Assert.NotEmpty(
            BinaryPatternSearcher.FindTextMatches(dumpData, "lucassimmshat.ddx"));
        Assert.NotEmpty(
            BinaryPatternSearcher.FindTextMatches(dumpData, "outfitf.nif"));
        Assert.NotEmpty(
            BinaryPatternSearcher.FindTextMatches(dumpData, "outfitm.nif"));
        Assert.NotEmpty(
            BinaryPatternSearcher.FindTextMatches(dumpData, "outfitf.ddx"));
        Assert.NotEmpty(
            BinaryPatternSearcher.FindTextMatches(dumpData, "outfitf_n.ddx"));
        Assert.Empty(
            BinaryPatternSearcher.FindTextMatches(
                dumpData,
                @"textures\armor\lucassimms\outfitf"));
        Assert.Empty(
            BinaryPatternSearcher.FindTextMatches(
                dumpData,
                @"textures\armor\lucassimms\outfitm"));
    }

    private static List<OutfitShaderEvidence> LoadOutfitEvidence(string nifPath)
    {
        var workingData = File.ReadAllBytes(nifPath);
        var workingInfo = Assert.IsType<NifInfo>(NifParser.Parse(workingData));

        using var textureResolver = new NifTextureResolver();
        var model = NifGeometryExtractor.Extract(
            workingData,
            workingInfo,
            textureResolver);
        if (model == null && workingInfo.IsBigEndian)
        {
            var converted = NifConverter.Convert(workingData);
            Assert.True(converted.Success, converted.ErrorMessage);

            workingData = Assert.IsType<byte[]>(converted.OutputData);
            workingInfo = Assert.IsType<NifInfo>(NifParser.Parse(workingData));
            model = NifGeometryExtractor.Extract(
                workingData,
                workingInfo,
                textureResolver);
        }

        Assert.NotNull(model);

        return model.Submeshes
            .Where(submesh =>
                string.Equals(
                    submesh.DiffuseTexturePath,
                    OutfitDiffusePath,
                    StringComparison.OrdinalIgnoreCase))
            .Select(submesh => CreateEvidence(submesh))
            .ToList();
    }

    private static OutfitShaderEvidence CreateEvidence(RenderableSubmesh submesh)
    {
        var metadata = Assert.IsType<NifShaderTextureMetadata>(submesh.ShaderMetadata);
        var shaderFlags = Assert.IsType<uint>(metadata.ShaderFlags);
        var shaderFlags2 = Assert.IsType<uint>(metadata.ShaderFlags2);

        Assert.Equal(OutfitDiffusePath, submesh.DiffuseTexturePath);
        Assert.Equal(OutfitNormalPath, submesh.NormalMapTexturePath);
        Assert.Equal(OutfitDiffusePath, metadata.DiffusePath);
        Assert.Equal(OutfitNormalPath, metadata.NormalMapPath);
        Assert.Equal(0x82000103u, shaderFlags);
        Assert.Equal(1u, shaderFlags2);
        Assert.True(metadata.HasRemappableTextures);
        Assert.Equal(8, metadata.TextureSlots.Count);
        Assert.Equal(OutfitDiffusePath, metadata.GetTextureSlot(0));
        Assert.Equal(OutfitNormalPath, metadata.GetTextureSlot(1));
        for (var slot = 2; slot < 8; slot++)
        {
            Assert.Null(metadata.GetTextureSlot(slot));
        }

        return new OutfitShaderEvidence(
            submesh.ShapeName,
            shaderFlags,
            shaderFlags2,
            metadata.TextureSlots.ToArray());
    }

    private static string DescribeEvidence(OutfitShaderEvidence evidence)
    {
        return string.Join(
            "|",
            [
                evidence.ShapeName ?? "(unnamed)",
                $"0x{evidence.ShaderFlags:X8}",
                $"0x{evidence.ShaderFlags2:X8}",
                .. evidence.TextureSlots.Select(slot => slot ?? "<null>")
            ]);
    }

    private sealed record OutfitShaderEvidence(
        string? ShapeName,
        uint ShaderFlags,
        uint ShaderFlags2,
        string?[] TextureSlots);
}