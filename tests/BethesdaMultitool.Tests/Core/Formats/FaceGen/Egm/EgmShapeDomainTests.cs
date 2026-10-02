using System.Numerics;
using BethesdaMultitool.Core.Formats.FaceGen.Egm;
using BethesdaMultitool.Core.Formats.FaceGen.Tri;
using BethesdaMultitool.Core.Formats.Nif.Rendering.FaceGen;
using BethesdaMultitool.Tests.Core.Formats.FaceGen.Tri;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.FaceGen.Egm;

/// <summary>Verifies full-domain shaping and the distinct NIF neutral-prefix requirement without changing renderer behavior.</summary>
public sealed class EgmShapeDomainTests
{
    /// <summary>Known basis arithmetic shapes both base vertices and statistical targets without truncating to visible geometry.</summary>
    [Fact]
    public void PreservesFullDomainAndSourceCoefficientIdentity()
    {
        var tri = TriReader.Read(TriFixture.Create().Bytes, TestContext.Current.CancellationToken);
        var egm = EgmReader.Read(EgmFixture.Create(), TestContext.Current.CancellationToken);
        float[] symmetric = [0.5f, 2];
        float[] asymmetric = [-0.25f];
        var domain = EgmShapeDomain.Create(tri, egm, 2001060901, symmetric, asymmetric, TestContext.Current.CancellationToken);
        Vector3[] expected = [new(1, 0, 2), new(0, 0.5f, 0), new(0, 0, 1), new(2, 2, -0.5f), new(-0.5f, 2.5f, -4)];
        Assert.Equal(expected, domain.Displacements.ToArray());
        Assert.Equal(new Vector3(6, 2, -0.5f), domain.Vertices.Span[3]);
        Assert.Equal(new Vector3(-0.5f, 3.5f, 2), domain.Vertices.Span[4]);
        Assert.Equal(tri.SourceHash, domain.TriSourceHash);
        Assert.Equal(egm.SourceHash, domain.EgmSourceHash);
        Assert.Equal("1703BF3990EA082A96AD3933784C5020812A89BD991D52651C5D517A2EA33163", domain.CoefficientHash);
        Assert.Equal(3, domain.BaseVertexCount);
        symmetric[0] = 100;
        asymmetric[0] = 100;
        Assert.Equal(0.5f, domain.SymmetricCoefficients.Span[0]);
        Assert.Equal(-0.25f, domain.AsymmetricCoefficients.Span[0]);
        var repeated = EgmShapeDomain.Create(tri, egm, 2001060901, [0.5f, 2], [-0.25f], TestContext.Current.CancellationToken);
        Assert.Equal(domain.CoefficientHash, repeated.CoefficientHash);
        Assert.Equal(domain.Vertices.ToArray(), repeated.Vertices.ToArray());
    }

    /// <summary>The NIF neutral prefix and statistical reference domain receive the same shape before an expression is evaluated.</summary>
    [Fact]
    public void ShapesNifNeutralSeparatelyBeforeStatisticalExpression()
    {
        var tri = TriReader.Read(TriFixture.Create().Bytes, TestContext.Current.CancellationToken);
        var egm = EgmReader.Read(EgmFixture.Create(), TestContext.Current.CancellationToken);
        var domain = EgmShapeDomain.Create(tri, egm, 2001060901, [0.5f, 2], [-0.25f], TestContext.Current.CancellationToken);
        Vector3[] original = [new(10, 20, 30), new(11, 20, 30), new(10, 21, 30)];
        var neutral = domain.ApplyBaseDisplacements(original, TestContext.Current.CancellationToken);
        Assert.Equal(new Vector3(11, 20.5f, 30), neutral[1]);
        Assert.Equal(new Vector3(11, 20, 30), original[1]);
        Assert.Equal(neutral, domain.ApplyBaseDisplacements(original, TestContext.Current.CancellationToken));
        var binding = TriGeometryBinding.Create(tri, "synthetic.nif", "explicit-source-hash", 6, neutral,
            tri.Triangles.Span, domain.Vertices.Span, TestContext.Current.CancellationToken);
        var pose = binding.Evaluate([new(tri.ResolveMorph("Blink"), 1)], TestContext.Current.CancellationToken);
        Assert.Equal(new Vector3(16, 22, 29.5f), pose[1]);
        Assert.Equal(neutral, binding.Evaluate([], TestContext.Current.CancellationToken));
    }

    /// <summary>Wrong basis identities, incomplete coefficient arrays and wrong V+K pairs fail explicitly.</summary>
    [Fact]
    public void RejectsMismatchedBasisCountsAndDomains()
    {
        var tri = TriReader.Read(TriFixture.Create().Bytes, TestContext.Current.CancellationToken);
        var egm = EgmReader.Read(EgmFixture.Create(), TestContext.Current.CancellationToken);
        Assert.Throws<InvalidDataException>(() => EgmShapeDomain.Create(tri, egm, 7, [0, 0], [0], TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => EgmShapeDomain.Create(tri, egm, egm.BasisKey, [0], [0], TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => EgmShapeDomain.Create(tri, egm, egm.BasisKey, [0, 0], [0, 0], TestContext.Current.CancellationToken));
        var tinyBytes = new byte[64];
        "FREGM002"u8.CopyTo(tinyBytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tinyBytes.AsSpan(8), 3);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tinyBytes.AsSpan(20), egm.BasisKey);
        var wrongDomain = EgmReader.Read(tinyBytes, TestContext.Current.CancellationToken);
        Assert.Throws<InvalidDataException>(() => EgmShapeDomain.Create(tri, wrongDomain, egm.BasisKey, [], [], TestContext.Current.CancellationToken));
        var domain = EgmShapeDomain.Create(tri, egm, egm.BasisKey, [0, 0], [0], TestContext.Current.CancellationToken);
        Assert.Throws<ArgumentException>(() => domain.ApplyBaseDisplacements([Vector3.Zero], TestContext.Current.CancellationToken));
    }

    /// <summary>Non-finite inputs, overflowing arithmetic and cancellation cannot publish a partial shape result.</summary>
    [Fact]
    public void RejectsNonFiniteOverflowAndCancellation()
    {
        var tri = TriReader.Read(TriFixture.Create().Bytes, TestContext.Current.CancellationToken);
        var egm = EgmReader.Read(EgmFixture.Create(), TestContext.Current.CancellationToken);
        Assert.Throws<ArgumentException>(() => EgmShapeDomain.Create(tri, egm, egm.BasisKey, [float.NaN, 0], [0], TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => EgmShapeDomain.Create(tri, egm, egm.BasisKey, [float.MaxValue, 0], [0], TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => EgmShapeDomain.Create(tri, egm, egm.BasisKey, [0, 0], [float.MaxValue], TestContext.Current.CancellationToken));
        var domain = EgmShapeDomain.Create(tri, egm, egm.BasisKey, [0.5f, 2], [-0.25f], TestContext.Current.CancellationToken);
        Assert.Throws<InvalidDataException>(() => domain.ApplyBaseDisplacements([new(float.NaN, 0, 0), Vector3.Zero, Vector3.Zero], TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => EgmShapeDomain.Create(tri, egm, egm.BasisKey, [0, 0], [0], cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => domain.ApplyBaseDisplacements(tri.Vertices.Span, cancellation.Token));
    }

    /// <summary>Ordinary finite coefficients agree with the existing pre-skin delta path on independently known values.</summary>
    [Fact]
    public void MatchesExistingPreSkinArithmeticWithoutReplacingIt()
    {
        var bytes = EgmFixture.Create();
        var tri = TriReader.Read(TriFixture.Create().Bytes, TestContext.Current.CancellationToken);
        var strict = EgmReader.Read(bytes, TestContext.Current.CancellationToken);
        var legacy = Assert.IsType<EgmParser>(EgmParser.Parse(bytes));
        var old = FaceGenMeshMorpher.ComputeAccumulatedDeltas(legacy, [0.5f, 2], [-0.25f], legacy.VertexCount);
        Assert.NotNull(old);
        var domain = EgmShapeDomain.Create(tri, strict, strict.BasisKey, [0.5f, 2], [-0.25f], TestContext.Current.CancellationToken);
        Assert.Equal(-4, old[14]);
        for (var vertex = 0; vertex < strict.VertexCount; vertex++)
        {
            Assert.Equal(new Vector3(old[vertex * 3], old[vertex * 3 + 1], old[vertex * 3 + 2]), domain.Displacements.Span[vertex]);
        }
        var tiny = EgmShapeDomain.Create(tri, strict, strict.BasisKey, [1e-8f, 0], [0], TestContext.Current.CancellationToken);
        Assert.Equal(1e-8f, tiny.Displacements.Span[0].X);
        Assert.Null(FaceGenMeshMorpher.ComputeAccumulatedDeltas(legacy, [1e-8f, 0], [0], legacy.VertexCount));
    }
}
