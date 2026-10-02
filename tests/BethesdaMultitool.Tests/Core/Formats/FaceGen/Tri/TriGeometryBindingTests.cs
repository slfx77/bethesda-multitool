using System.Numerics;
using BethesdaMultitool.Core.Formats.FaceGen.Tri;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.FaceGen.Tri;

/// <summary>Checks exact topology admission, explicit reference domains and independent neutral-pose evaluation.</summary>
public sealed class TriGeometryBindingTests
{
    /// <summary>Statistical target-minus-reference deltas act on NIF neutral coordinates, independently of the TRI base shape.</summary>
    [Fact]
    public void EvaluatesBothFamiliesFromImmutableRestPose()
    {
        var document = TriReader.Read(TriFixture.Create().Bytes, TestContext.Current.CancellationToken);
        Vector3[] rest = [new(10, 20, 30), new(11, 20, 30), new(10, 21, 30)];
        var reference = ReferenceDomain(document);
        reference[1] = new Vector3(2, 0, 0);
        reference[3] = new Vector3(8, 0, 0);
        var binding = TriGeometryBinding.Create(document, "explicit.nif", "synthetic-source-identity", 7, rest,
            [0, 1, 2], reference, TestContext.Current.CancellationToken);
        TriMorphWeight[] weights = [new(document.ResolveMorph("Ee"), 0.5f),
            new(document.ResolveMorph("Blink"), 0.5f), new(document.ResolveMorph("Look"), -1)];
        var pose = binding.Evaluate(weights, TestContext.Current.CancellationToken);
        Assert.Equal(new Vector3(10.5f, 20, 30), pose[0]);
        Assert.Equal(new Vector3(14, 19, 30), pose[1]);
        Assert.Equal(new Vector3(10, 21, 25.5f), pose[2]);
        Assert.Equal(rest, binding.Evaluate([], TestContext.Current.CancellationToken));
        Assert.Equal(pose, binding.Evaluate(weights, TestContext.Current.CancellationToken));
        rest[0] = new Vector3(99);
        reference[3] = new Vector3(-100);
        Assert.Equal(pose, binding.Evaluate(weights, TestContext.Current.CancellationToken));
        pose[0] = Vector3.Zero;
        Assert.Equal(new Vector3(10.5f, 20, 30), binding.Evaluate(weights, TestContext.Current.CancellationToken)[0]);
        Assert.Equal("explicit.nif", binding.GeometrySourceName);
        Assert.Equal(7, binding.GeometryBlockIndex);
    }

    /// <summary>Explicit destination duplication fans out a source result and cannot silently drop an original vertex.</summary>
    [Fact]
    public void DestinationMapSupportsDuplicatesAndRejectsMissingSources()
    {
        var document = TriReader.Read(TriFixture.Create().Bytes, TestContext.Current.CancellationToken);
        var binding = CreateBinding(document);
        var mapped = binding.WithDestinationMap([2, 1, 0, 2], TestContext.Current.CancellationToken);
        var values = mapped.Evaluate([new(document.ResolveMorph("Ee"), 1)], TestContext.Current.CancellationToken);
        Assert.Equal(4, mapped.DestinationVertexCount);
        Assert.Equal(new Vector3(0, 1, 3), values[0]);
        Assert.Equal(values[0], values[3]);
        Assert.Equal(new Vector3(1, 0, 0), values[2]);
        Assert.Throws<InvalidDataException>(() => binding.WithDestinationMap([0, 1, 1], TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => binding.WithDestinationMap([0, 1, 2, 3], TestContext.Current.CancellationToken));
        Assert.Equal(3, binding.DestinationVertexCount);
    }

    /// <summary>Counts alone fail; oriented indexed facets preserve winding and duplicate multiplicity while tolerating cyclic corners.</summary>
    [Fact]
    public void ExactTopologyRejectsWindingMultiplicityAndDomainMismatches()
    {
        var document = TriReader.Read(TriFixture.Create().Bytes, TestContext.Current.CancellationToken);
        var rest = document.Vertices.ToArray();
        var reference = ReferenceDomain(document);
        var admitted = TriGeometryBinding.Create(document, "source.nif", "hash", 0, rest, [1, 2, 0, 0, 0, 1], reference,
            TestContext.Current.CancellationToken);
        Assert.Equal(new TriTopologyMatch(2, 1, 1, 0), admitted.Topology);
        Assert.Throws<InvalidDataException>(() => TriGeometryBinding.Create(document, "source.nif", "hash", 0,
            rest, [0, 2, 1], reference, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => TriGeometryBinding.Create(document, "source.nif", "hash", 0,
            rest, [0, 1, 2, 0, 1, 2], reference, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => TriGeometryBinding.Create(document, "source.nif", "hash", 0,
            rest.AsSpan(0, 2), [0, 1, 2], reference, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => TriGeometryBinding.Create(document, "source.nif", "hash", 0,
            rest, [0, 1, 2], rest, TestContext.Current.CancellationToken));
        var quad = TriReader.Read(TriFixture.Create(quad: true).Bytes, TestContext.Current.CancellationToken);
        Assert.Throws<NotSupportedException>(() => CreateBinding(quad));
    }

    /// <summary>Unknown identities, duplicates, non-finite weights, overflow and cancellation never publish a partial pose.</summary>
    [Fact]
    public void InvalidEvaluationDoesNotChangeNeutralState()
    {
        var document = TriReader.Read(TriFixture.Create().Bytes, TestContext.Current.CancellationToken);
        var binding = CreateBinding(document);
        var morph = document.ResolveMorph("Ee");
        Assert.Throws<ArgumentException>(() => binding.Evaluate([new(morph, float.NaN)], TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => binding.Evaluate([new(morph, 1), new(morph, 2)], TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => binding.Evaluate([new(new TriMorphReference((TriMorphKind)99, 0), 1)], TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => binding.Evaluate([new(new TriMorphReference(TriMorphKind.Statistical, 2), 0)], TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => binding.Evaluate([new(morph, float.MaxValue)], TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => binding.Evaluate([], cancellation.Token));
        Assert.Equal(document.Vertices.ToArray(), binding.Evaluate([], TestContext.Current.CancellationToken));
    }

    /// <summary>Creates a synthetic binding in a matching source coordinate domain.</summary>
    private static TriGeometryBinding CreateBinding(TriDocument document) => TriGeometryBinding.Create(document,
        "synthetic.nif", "synthetic-source-identity", 1, document.Vertices.Span, document.Triangles.Span,
        ReferenceDomain(document), TestContext.Current.CancellationToken);

    /// <summary>Concatenates explicitly selected raw source coordinates for diagnostic tests, without actor-shaping assumptions.</summary>
    internal static Vector3[] ReferenceDomain(TriDocument document) => [.. document.Vertices.Span, .. document.StatisticalVertices.Span];
}
