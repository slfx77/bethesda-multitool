using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm;

public sealed class RaceFaceGenSectionIntegrationTests(ITestOutputHelper output, SampleFileFixture samples)
{
    private readonly ITestOutputHelper _output = output;

    [Theory]
    [InlineData(0x000042BFu, "AfricanAmericanOld")]
    [InlineData(0x0000424Au, "AfricanAmerican")]
    [InlineData(0x000038E5u, "Hispanic")]
    [Trait("Category", "Slow")]
    public void PcFinalRuntimeMismatchRaces_HaveExplicitMaleAndFemaleFaceGenSections(uint formId,
        string expectedEditorId)
    {
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final ESM not available");

        var pipeline = PcFinalEsmPipelineCache.GetOrBuild(samples.PcFinalEsm!);
        var parsedRace = pipeline.ParsedRecords.FirstOrDefault(record =>
            record.Header.Signature == "RACE" && record.Header.FormId == formId);
        var semanticRace = pipeline.Collection.Races.FirstOrDefault(race => race.FormId == formId);

        Assert.NotNull(parsedRace);
        Assert.NotNull(semanticRace);
        Assert.Equal(expectedEditorId, parsedRace!.EditorId);
        Assert.Equal(expectedEditorId, semanticRace!.EditorId);

        var signatures = parsedRace.Subrecords.Select(subrecord => subrecord.Signature).ToArray();
        Assert.Contains("NAM2", signatures);
        Assert.True(
            HasExplicitFaceGenTail(signatures),
            $"Expected explicit MNAM/FNAM FaceGen tail in RACE 0x{formId:X8}.");

        Assert.NotNull(semanticRace.MaleFaceGenGeometrySymmetric);
        Assert.NotNull(semanticRace.FemaleFaceGenGeometrySymmetric);
        Assert.NotNull(semanticRace.MaleFaceGenGeometryAsymmetric);
        Assert.NotNull(semanticRace.FemaleFaceGenGeometryAsymmetric);
        Assert.NotNull(semanticRace.MaleFaceGenTextureSymmetric);
        Assert.NotNull(semanticRace.FemaleFaceGenTextureSymmetric);
        Assert.Equal(50, semanticRace.MaleFaceGenGeometrySymmetric!.Length);
        Assert.Equal(50, semanticRace.FemaleFaceGenGeometrySymmetric!.Length);
        Assert.Equal(30, semanticRace.MaleFaceGenGeometryAsymmetric!.Length);
        Assert.Equal(30, semanticRace.FemaleFaceGenGeometryAsymmetric!.Length);
        Assert.Equal(50, semanticRace.MaleFaceGenTextureSymmetric!.Length);
        Assert.Equal(50, semanticRace.FemaleFaceGenTextureSymmetric!.Length);

        _output.WriteLine(
            $"0x{formId:X8} {expectedEditorId}: NAM2 present, explicit MNAM/FNAM FaceGen tail present, " +
            $"male FGTS={semanticRace.MaleFaceGenTextureSymmetric.Length}, " +
            $"female FGTS={semanticRace.FemaleFaceGenTextureSymmetric.Length}");
    }

    private static bool HasExplicitFaceGenTail(IReadOnlyList<string> signatures)
    {
        for (var index = 0; index <= signatures.Count - 10; index++)
        {
            if (signatures[index] != "MNAM")
            {
                continue;
            }

            if (signatures[index + 1] != "FGGS" ||
                signatures[index + 2] != "FGGA" ||
                signatures[index + 3] != "FGTS" ||
                signatures[index + 4] != "SNAM" ||
                signatures[index + 5] != "FNAM" ||
                signatures[index + 6] != "FGGS" ||
                signatures[index + 7] != "FGGA" ||
                signatures[index + 8] != "FGTS" ||
                signatures[index + 9] != "SNAM")
            {
                continue;
            }

            return true;
        }

        return false;
    }
}