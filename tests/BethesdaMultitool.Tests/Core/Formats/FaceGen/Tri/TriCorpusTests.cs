using System.Text.Json;
using BethesdaMultitool.Core.Formats.FaceGen.Tri;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.FaceGen.Tri;

/// <summary>Validates explicit private corpus inputs against independently generated hashes and morph metadata.</summary>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class TriCorpusTests
{
    /// <summary>Requires the repository's explicit real-asset test opt-in.</summary>
    public TriCorpusTests() => BucketBTestGuard.SkipUnlessEnabled();

    /// <summary>Checks every declared file, its independent field values and the exact encoded-byte identity.</summary>
    [Fact]
    public async Task MatchesExplicitIndependentCorpusManifest()
    {
        var manifestPath = Environment.GetEnvironmentVariable("BMT_TRI_CORPUS_MANIFEST");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(manifestPath), "Set BMT_TRI_CORPUS_MANIFEST to an explicit independent TRI manifest.");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken));
        var files = manifest.RootElement.GetProperty("accepted");
        Assert.True(files.GetArrayLength() > 0);
        Assert.Equal(0, manifest.RootElement.GetProperty("failures").GetArrayLength());
        foreach (var entry in files.EnumerateArray())
        {
            var document = await TriReader.ReadFileAsync(entry.GetProperty("file").GetString()!, TestContext.Current.CancellationToken);
            Assert.Equal(entry.GetProperty("sha256").GetString()!.ToUpperInvariant(), document.SourceHash);
            Assert.Equal(entry.GetProperty("bytes").GetInt32(), document.EncodedSize);
            Assert.Equal(entry.GetProperty("vertices").GetInt32(), document.Header.VertexCount);
            Assert.Equal(entry.GetProperty("triangles").GetInt32(), document.Header.TriangleCount);
            Assert.Equal(entry.GetProperty("statisticalVertices").GetInt32(), document.Header.StatisticalVertexCount);
            var deltas = entry.GetProperty("differential");
            Assert.Equal(deltas.GetArrayLength(), document.DifferentialMorphs.Count);
            for (var index = 0; index < deltas.GetArrayLength(); index++)
            {
                Assert.Equal(deltas[index].GetProperty("name").GetString(), document.DifferentialMorphs[index].Label.Text);
                Assert.Equal(deltas[index].GetProperty("scale").GetSingle(), document.DifferentialMorphs[index].Scale);
                var nonzero = 0;
                for (var vertex = 0; vertex < document.Header.VertexCount; vertex++)
                {
                    var packed = document.DifferentialMorphs[index].PackedDeltas.Span.Slice(vertex * 3, 3);
                    if (packed[0] != 0 || packed[1] != 0 || packed[2] != 0) nonzero++;
                }
                Assert.Equal(deltas[index].GetProperty("nonzeroVertices").GetInt32(), nonzero);
            }
            var targets = entry.GetProperty("statistical");
            Assert.Equal(targets.GetArrayLength(), document.StatisticalMorphs.Count);
            for (var index = 0; index < targets.GetArrayLength(); index++)
            {
                var actual = document.StatisticalMorphs[index];
                Assert.Equal(targets[index].GetProperty("name").GetString(), actual.Label.Text);
                Assert.Equal(targets[index].GetProperty("vertices").GetInt32(), actual.VertexIndices.Length);
                Assert.Equal(targets[index].GetProperty("firstTargetVertex").GetInt32(), actual.FirstTargetVertex);
            }
        }
    }

    /// <summary>Checks selected real NIF blocks and TRI indexed topology rather than accepting equal vertex counts.</summary>
    [Theory]
    [InlineData("headhuman", 6, 0.9995f, 6.7631f, -0.7579f)]
    [InlineData("eyelefthuman", 7, -2.3618f, 6.8039f, 7.0654f)]
    public async Task BindsExplicitSourceNifGeometry(string stem, int blockIndex, float firstX, float firstY, float firstZ)
    {
        var root = Environment.GetEnvironmentVariable("BMT_TRI_NIF_ROOT");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root), "Set BMT_TRI_NIF_ROOT to an explicit FNV head-geometry fixture directory.");
        var tri = await TriReader.ReadFileAsync(Path.Combine(root, stem + ".tri"), TestContext.Current.CancellationToken);
        var path = Path.Combine(root, stem + ".nif");
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var binding = TriNifBinding.Bind(bytes, path, blockIndex, tri, TriGeometryBindingTests.ReferenceDomain(tri),
            TestContext.Current.CancellationToken);
        Assert.Equal(blockIndex, binding.GeometryBlockIndex);
        var neutral = binding.Evaluate([], TestContext.Current.CancellationToken);
        Assert.Equal(tri.Header.VertexCount, neutral.Length);
        // Independently observed NifAnalyzer coordinates are printed to four decimal places.
        Assert.InRange(neutral[0].X, firstX - 0.0001f, firstX + 0.0001f);
        Assert.InRange(neutral[0].Y, firstY - 0.0001f, firstY + 0.0001f);
        Assert.InRange(neutral[0].Z, firstZ - 0.0001f, firstZ + 0.0001f);
        Assert.Throws<ArgumentOutOfRangeException>(() => TriNifBinding.Bind(bytes, path, -1, tri,
            TriGeometryBindingTests.ReferenceDomain(tri), TestContext.Current.CancellationToken));
        Assert.Throws<NotSupportedException>(() => TriNifBinding.Bind(bytes, path, 0, tri,
            TriGeometryBindingTests.ReferenceDomain(tri), TestContext.Current.CancellationToken));
    }
}
