using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using BethesdaMultitool.Core.Formats.FaceGen.Egm;
using BethesdaMultitool.Core.Formats.FaceGen.Tri;
using BethesdaMultitool.Core.Formats.Nif.Rendering.FaceGen;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.FaceGen.Egm;

/// <summary>Validates explicit private TRI/EGM pairs and legacy pre-skin compatibility against independent manifests.</summary>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class EgmCorpusTests
{
    /// <summary>Requires the existing explicit real-asset test opt-in.</summary>
    public EgmCorpusTests() => BucketBTestGuard.SkipUnlessEnabled();

    /// <summary>Checks every source/mode hash, complete statistical suffix and ordinary-coefficient pre-skin displacement.</summary>
    /// <returns>A task completing after all explicitly configured fixture pairs and arithmetic controls have been checked.</returns>
    [Fact]
    public async Task MatchesIndependentPairsAndExistingFullDomainArithmetic()
    {
        var root = Environment.GetEnvironmentVariable("BMT_EGM_PAIR_ROOT");
        var manifestPath = Environment.GetEnvironmentVariable("BMT_EGM_PAIR_MANIFEST");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(manifestPath),
            "Set BMT_EGM_PAIR_ROOT and BMT_EGM_PAIR_MANIFEST to explicit paired fixtures and their independent manifest.");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken));
        var pairs = manifest.RootElement.GetProperty("accepted");
        Assert.True(pairs.GetArrayLength() > 0);
        Assert.Equal(0, manifest.RootElement.GetProperty("failures").GetArrayLength());
        foreach (var pair in pairs.EnumerateArray())
        {
            var tri = await TriReader.ReadFileAsync(Path.Combine(root, pair.GetProperty("tri").GetString()!), TestContext.Current.CancellationToken);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(root, pair.GetProperty("egm").GetString()!), TestContext.Current.CancellationToken);
            var egm = EgmReader.Read(bytes, TestContext.Current.CancellationToken);
            Assert.Equal(pair.GetProperty("triSha256").GetString()!.ToUpperInvariant(), tri.SourceHash);
            Assert.Equal(pair.GetProperty("egmSha256").GetString()!.ToUpperInvariant(), egm.SourceHash);
            Assert.Equal(pair.GetProperty("basis").GetUInt32(), egm.BasisKey);
            Assert.Equal(pair.GetProperty("triV").GetInt32(), tri.Header.VertexCount);
            Assert.Equal(pair.GetProperty("triK").GetInt32(), tri.Header.StatisticalVertexCount);
            Assert.Equal(pair.GetProperty("egmVertices").GetInt32(), egm.VertexCount);
            Assert.Equal(pair.GetProperty("encodedBytes").GetInt32(), egm.EncodedSize);
            Assert.Equal(pair.GetProperty("reservedHex").GetString()!.ToUpperInvariant(), Convert.ToHexString(egm.Reserved.Span));
            Assert.Equal(pair.GetProperty("symmetric").GetInt32(), egm.SymmetricModes.Count);
            Assert.Equal(pair.GetProperty("asymmetric").GetInt32(), egm.AsymmetricModes.Count);
            Assert.Equal(egm.SymmetricModes.Count + egm.AsymmetricModes.Count, pair.GetProperty("modes").GetArrayLength());
            foreach (var expected in pair.GetProperty("modes").EnumerateArray())
            {
                var family = expected.GetProperty("family").GetString();
                Assert.True(family is "symmetric" or "asymmetric");
                var modes = family == "symmetric" ? egm.SymmetricModes : egm.AsymmetricModes;
                var mode = modes[expected.GetProperty("index").GetInt32()];
                Assert.Equal(expected.GetProperty("scale").GetSingle(), mode.Scale);
                Assert.Equal(expected.GetProperty("packedSha256").GetString()!.ToUpperInvariant(), PackedHash(mode));
                Assert.Equal(expected.GetProperty("nonzeroVertices").GetInt32(), CountNonzero(mode, 0));
                Assert.Equal(expected.GetProperty("nonzeroSuffixVertices").GetInt32(), CountNonzero(mode, tri.Header.VertexCount));
            }
            var symmetric = new float[egm.SymmetricModes.Count];
            var asymmetric = new float[egm.AsymmetricModes.Count];
            if (symmetric.Length > 0) symmetric[0] = 0.125f;
            if (symmetric.Length > 1) symmetric[^1] = -0.25f;
            if (asymmetric.Length > 0) asymmetric[0] = -0.5f;
            if (asymmetric.Length > 1) asymmetric[^1] = 0.25f;
            var domain = EgmShapeDomain.Create(tri, egm, pair.GetProperty("basis").GetUInt32(), symmetric, asymmetric,
                TestContext.Current.CancellationToken);
            var legacy = Assert.IsType<EgmParser>(EgmParser.Parse(bytes));
            var old = FaceGenMeshMorpher.ComputeAccumulatedDeltas(legacy, symmetric, asymmetric, legacy.VertexCount);
            for (var vertex = 0; vertex < egm.VertexCount; vertex++)
            {
                var actual = domain.Displacements.Span[vertex];
                if (old is null)
                {
                    Assert.InRange(MathF.Abs(actual.X), 0, 1e-9f);
                    Assert.InRange(MathF.Abs(actual.Y), 0, 1e-9f);
                    Assert.InRange(MathF.Abs(actual.Z), 0, 1e-9f);
                }
                else
                {
                    Assert.Equal(new Vector3(old[vertex * 3], old[vertex * 3 + 1], old[vertex * 3 + 2]), actual);
                }
            }
        }
    }

    /// <summary>Hashes canonical little-endian packed shorts independently of the host's native byte order.</summary>
    /// <param name="mode">The parsed complete source mode to encode without changing it.</param>
    /// <returns>Uppercase SHA-256 of its original packed payload.</returns>
    private static string PackedHash(EgmBasisMode mode)
    {
        var values = mode.PackedDeltas.Span;
        var bytes = new byte[values.Length * 2];
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * 2), values[index]);
        }
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    /// <summary>Counts original nonzero packed triples from an explicit base or suffix boundary.</summary>
    /// <param name="mode">The complete source mode to inspect.</param>
    /// <param name="firstVertex">The first included source index, either zero or the paired TRI base count.</param>
    /// <returns>The number of remaining source vertices with any nonzero packed component.</returns>
    private static int CountNonzero(EgmBasisMode mode, int firstVertex)
    {
        var values = mode.PackedDeltas.Span;
        var count = 0;
        for (var vertex = firstVertex; vertex < values.Length / 3; vertex++)
        {
            var offset = vertex * 3;
            if (values[offset] != 0 || values[offset + 1] != 0 || values[offset + 2] != 0)
            {
                count++;
            }
        }
        return count;
    }
}
