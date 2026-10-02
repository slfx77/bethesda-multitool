using System.Security.Cryptography;
using System.Text.Json;
using BethesdaMultitool.Core.Media.Audio.Lip;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Media.Audio.Lip;

/// <summary>Opt-in differential verification against explicit independently decoded private corpus manifests.</summary>
public sealed class LipCorpusTests
{
    /// <summary>Checks every listed file against independent counts, offsets, ranges and available input hashes.</summary>
    [Fact]
    [Trait("Category", "Corpus")]
    public void MatchesIndependentManifest()
    {
        var root = Environment.GetEnvironmentVariable("BMT_LIP_CORPUS_ROOT");
        var manifests = Environment.GetEnvironmentVariable("BMT_LIP_CORPUS_MANIFESTS");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(manifests),
            "Set BMT_LIP_CORPUS_ROOT and semicolon-separated BMT_LIP_CORPUS_MANIFESTS to opt into private LIP validation.");
        var count = 0;
        foreach (var manifest in manifests!.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            foreach (var row in document.RootElement.EnumerateArray())
            {
                var relative = row.GetProperty("path").GetString()!.Replace('\\', Path.DirectorySeparatorChar);
                var file = Path.GetFullPath(Path.Combine(root!, relative));
                var withinRoot = Path.GetRelativePath(root!, file);
                Assert.False(Path.IsPathRooted(withinRoot) || withinRoot == ".." || withinRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
                var bytes = File.ReadAllBytes(file);
                if (row.TryGetProperty("sha256", out var expectedHash))
                    Assert.Equal(expectedHash.GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
                var timeline = LipDecoder.Decode(bytes, TestContext.Current.CancellationToken);
                Assert.Equal(row.GetProperty("n").GetInt32(), timeline.FrameCount);
                Assert.Equal(row.GetProperty("start").GetInt32(), timeline.StartingFrame);
                Assert.Equal(row.GetProperty("expanded").GetInt32() + 16, (long)timeline.DeclaredSize);
                var minimum = timeline.FrameCount == 0 ? 0 : float.PositiveInfinity;
                var maximum = timeline.FrameCount == 0 ? 0 : float.NegativeInfinity;
                for (var frame = 0; frame < timeline.FrameCount; frame++)
                {
                    for (var track = 0; track < LipTimeline.Tracks.Count; track++)
                    {
                        var value = timeline.GetValue(frame, track);
                        minimum = Math.Min(minimum, value);
                        maximum = Math.Max(maximum, value);
                    }
                }
                Assert.Equal((float)row.GetProperty("min").GetDouble(), minimum);
                Assert.Equal((float)row.GetProperty("max").GetDouble(), maximum);
                count++;
            }
        }
        Assert.True(count > 0, "The explicit manifests must contain files; empty coverage is not a pass.");
    }
}
