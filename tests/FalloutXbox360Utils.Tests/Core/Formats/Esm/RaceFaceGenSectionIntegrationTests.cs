using System.Buffers.Binary;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm;

/// <summary>
///     Tests verifying that RACE records with explicit MNAM/FNAM FaceGen sections
///     are correctly parsed through the full ESM pipeline, using synthetic data.
/// </summary>
public sealed class RaceFaceGenSectionIntegrationTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Theory]
    [InlineData(0x000042BFu, "AfricanAmericanOld")]
    [InlineData(0x0000424Au, "AfricanAmerican")]
    [InlineData(0x000038E5u, "Hispanic")]
    public void RaceWithExplicitFaceGenSections_ParsesAllCoefficients(uint formId, string editorId)
    {
        var raceRecord = BuildRaceWithFaceGenTail(formId, editorId);

        var builder = new EsmTestFileBuilder();
        builder.AddTopLevelGrup("RACE", raceRecord);

        var pipeline = builder.BuildAndAnalyze();

        // Verify raw subrecord signatures
        var parsedRace = pipeline.ParsedRecords.FirstOrDefault(r =>
            r.Header.Signature == "RACE" && r.Header.FormId == formId);
        Assert.NotNull(parsedRace);
        Assert.Equal(editorId, parsedRace!.EditorId);

        var signatures = parsedRace.Subrecords.Select(s => s.Signature).ToArray();
        Assert.Contains("NAM2", signatures);
        Assert.True(
            HasExplicitFaceGenTail(signatures),
            $"Expected explicit MNAM/FNAM FaceGen tail in RACE 0x{formId:X8}.");

        // Verify semantic parsing
        var semanticRace = pipeline.Collection.Races.FirstOrDefault(r => r.FormId == formId);
        Assert.NotNull(semanticRace);
        Assert.Equal(editorId, semanticRace!.EditorId);

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
            $"0x{formId:X8} {editorId}: NAM2 present, explicit MNAM/FNAM FaceGen tail, " +
            $"male FGTS={semanticRace.MaleFaceGenTextureSymmetric.Length}, " +
            $"female FGTS={semanticRace.FemaleFaceGenTextureSymmetric.Length}");
    }

    /// <summary>
    ///     Build a synthetic LE RACE record with the FaceGen tail sequence:
    ///     EDID, FULL, DATA(36B), NAM2(0B), MNAM(0B), FGGS(200B), FGGA(120B), FGTS(200B), SNAM(0B),
    ///     FNAM(0B), FGGS(200B), FGGA(120B), FGTS(200B), SNAM(0B)
    /// </summary>
    private static byte[] BuildRaceWithFaceGenTail(uint formId, string editorId)
    {
        // DATA = 36 bytes: 14 bytes skill boosts + 2 padding + 4 floats (heights/weights) + 4 flags
        var data = new byte[36];
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(16), 1.0f); // MaleHeight
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(20), 1.0f); // FemaleHeight
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(24), 1.0f); // MaleWeight
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(28), 1.0f); // FemaleWeight

        // FGGS = 200 bytes (50 LE floats), FGGA = 120 bytes (30 LE floats), FGTS = 200 bytes (50 LE floats)
        var maleFggs = MakeFaceGenFloats(50, 0.1f);
        var maleFgga = MakeFaceGenFloats(30, 0.2f);
        var maleFgts = MakeFaceGenFloats(50, 0.3f);
        var femaleFggs = MakeFaceGenFloats(50, 0.4f);
        var femaleFgga = MakeFaceGenFloats(30, 0.5f);
        var femaleFgts = MakeFaceGenFloats(50, 0.6f);

        return EsmTestFileBuilder.BuildRecord("RACE", formId, 0,
            ("EDID", NullTermBytes(editorId)),
            ("FULL", NullTermBytes(editorId)),
            ("DATA", data),
            ("NAM2", []),       // Marker: FaceGen data section follows
            ("MNAM", []),       // Male section marker
            ("FGGS", maleFggs),
            ("FGGA", maleFgga),
            ("FGTS", maleFgts),
            ("SNAM", []),       // End male FaceGen
            ("FNAM", []),       // Female section marker
            ("FGGS", femaleFggs),
            ("FGGA", femaleFgga),
            ("FGTS", femaleFgts),
            ("SNAM", []));      // End female FaceGen
    }

    private static byte[] MakeFaceGenFloats(int count, float seed)
    {
        var bytes = new byte[count * 4];
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), seed + i * 0.001f);
        }

        return bytes;
    }

    private static byte[] NullTermBytes(string s)
    {
        var bytes = new byte[s.Length + 1];
        System.Text.Encoding.ASCII.GetBytes(s, bytes);
        return bytes;
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
