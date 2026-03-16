using System.Globalization;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Verification;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NpcRuntimeFaceGenProbeCaptureComparerTests
{
    [Fact]
    public void LoadAndCompare_ParsesCaptureBundleAndComputesArrayDeltas()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(
                Path.Combine(tempDir, "capture.json"),
                """
                {
                  "target": {
                    "baseNpcFormId": "00112640",
                    "raceFormId": "000042BF",
                    "female": false,
                    "matchedFormType": "TESNPC"
                  },
                  "descriptors": {
                    "npcTexture": {
                      "descriptorAddress": "0x1234",
                      "valuesPointer": "0x2000",
                      "count": 3,
                      "stride": 1,
                      "valid": true
                    },
                    "raceTexture": {
                      "descriptorAddress": "0x5678",
                      "valuesPointer": "0x3000",
                      "count": 3,
                      "stride": 1,
                      "valid": true
                    }
                  }
                }
                """,
                Encoding.UTF8);

            WriteCsv(Path.Combine(tempDir, "npc_texture_coeffs.csv"), [1.0f, 2.0f, 3.0f]);
            WriteCsv(Path.Combine(tempDir, "race_texture_coeffs.csv"), [-0.5f, 0.25f, 0.75f]);
            WriteCsv(Path.Combine(tempDir, "texture_coeffs.csv"), [0.5f, 2.25f, 3.75f]);

            var capture = NpcRuntimeFaceGenProbeCaptureComparer.LoadCapture(tempDir);
            Assert.Equal(0x00112640u, capture.BaseNpcFormId);
            Assert.Equal(0x000042BFu, capture.RaceFormId);
            Assert.False(capture.IsFemale);
            Assert.Equal([1.0f, 2.0f, 3.0f], capture.NpcTextureCoefficients);
            Assert.Equal([-0.5f, 0.25f, 0.75f], capture.RaceTextureCoefficients);
            Assert.Equal([0.5f, 2.25f, 3.75f], capture.MergedTextureCoefficients);

            var appearance = new NpcAppearance
            {
                NpcFormId = 0x00112640u,
                EditorId = "VStreetDennisCrocker",
                FullName = "Dennis Crocker",
                NpcFaceGenTextureCoeffs = [1.1f, 1.5f, 3.0f],
                RaceFaceGenTextureCoeffs = [-0.25f, 0.50f, 0.50f],
                FaceGenTextureCoeffs = [0.85f, 2.0f, 3.5f]
            };

            var comparison = NpcRuntimeFaceGenProbeCaptureComparer.Compare(appearance, capture);
            Assert.True(comparison.Compared);
            Assert.NotNull(comparison.Merged);
            Assert.NotNull(comparison.Npc);
            Assert.NotNull(comparison.Race);

            Assert.Equal(3, comparison.Merged!.ComparedCount);
            Assert.Equal(0.2833333333333333, comparison.Merged.MeanAbsoluteDelta, 6);
            Assert.Equal(0.35, comparison.Merged.MaxAbsoluteDelta, 6);
            Assert.Equal(-0.05, comparison.Merged.MeanSignedDelta, 6);
            Assert.Equal(0.5f, comparison.Merged.Rows[0].RuntimeValue);
            Assert.Equal(0.85f, comparison.Merged.Rows[0].CurrentValue);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void Compare_RanksRuntimeRaceAgainstAllParsedRaces()
    {
        var capture = new RuntimeFaceGenProbeCapture
        {
            CaptureDirectory = "capture",
            CaptureDirectoryName = "capture",
            BaseNpcFormId = 0x00112640u,
            RaceFormId = 0x000042BFu,
            IsFemale = false,
            NpcTextureDescriptor = new RuntimeFaceGenProbeDescriptor(null, null, 3u, 1u, true),
            RaceTextureDescriptor = new RuntimeFaceGenProbeDescriptor(null, null, 3u, 1u, true),
            NpcTextureCoefficients = [1.0f, 2.0f, 3.0f],
            RaceTextureCoefficients = [-1.0f, 0.5f, 2.0f],
            MergedTextureCoefficients = [0.0f, 2.5f, 5.0f]
        };

        var appearance = new NpcAppearance
        {
            NpcFormId = 0x00112640u,
            EditorId = "VStreetDennisCrocker",
            FullName = "Dennis Crocker",
            NpcFaceGenTextureCoeffs = [1.0f, 2.0f, 3.0f],
            RaceFaceGenTextureCoeffs = [-0.5f, 1.0f, 1.5f],
            FaceGenTextureCoeffs = [0.5f, 3.0f, 4.5f]
        };

        var races = new Dictionary<uint, RaceScanEntry>
        {
            [0x000042BFu] = new()
            {
                EditorId = "AfricanAmericanOld",
                YoungerRaceFormId = 0x0000424Au,
                MaleFaceGenTexture = [-0.5f, 1.0f, 1.5f],
                FemaleFaceGenTexture = [0.25f, 0.25f, 0.25f]
            },
            [0x0000424Au] = new()
            {
                EditorId = "AfricanAmerican",
                OlderRaceFormId = 0x000042BFu,
                MaleFaceGenTexture = [-1.0f, 0.5f, 2.0f],
                FemaleFaceGenTexture = [0.5f, 0.5f, 0.5f]
            },
            [0x00000019u] = new()
            {
                EditorId = "Caucasian",
                MaleFaceGenTexture = [0.0f, 0.0f, 0.0f],
                FemaleFaceGenTexture = [1.0f, 1.0f, 1.0f]
            }
        };

        var comparison = NpcRuntimeFaceGenProbeCaptureComparer.Compare(appearance, capture, races);

        Assert.NotNull(comparison.RaceMatches);
        Assert.Equal(6, comparison.RaceMatches!.Count);
        Assert.Equal(0x0000424Au, comparison.RaceMatches[0].RaceFormId);
        Assert.Equal("male", comparison.RaceMatches[0].CandidateSex);
        Assert.Equal("runtime.younger", comparison.RaceMatches[0].RelationToRuntimeRace);
        Assert.Equal(0.0, comparison.RaceMatches[0].Comparison.MeanAbsoluteDelta, 6);
        Assert.Equal(1.0, comparison.RaceMatches[0].Fit.Scale, 6);
        Assert.Equal(0.0, comparison.RaceMatches[0].Fit.Comparison.MeanAbsoluteDelta, 6);
        Assert.Equal(0x000042BFu, comparison.RaceMatches[1].RaceFormId);
        Assert.Equal("male", comparison.RaceMatches[1].CandidateSex);
        Assert.Equal("runtime", comparison.RaceMatches[1].RelationToRuntimeRace);
    }

    private static void WriteCsv(string path, IReadOnlyList<float> values)
    {
        var sb = new StringBuilder();
        sb.AppendLine("index,value");
        for (var index = 0; index < values.Count; index++)
        {
            sb.Append(index.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(values[index].ToString("F6", CultureInfo.InvariantCulture))
                .AppendLine();
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }
}
