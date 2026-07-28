using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Lighting;

public sealed class FnvRetailLightAssociationOracleTests
{
    [Fact]
    public void Influence_UsesSceneOffsetBoundSurfaceDistanceAndStrictBoundary()
    {
        var inside = FnvRetailLightAssociationOracle.EvaluateInfluence(
            new Vector3(18f, 0f, 0f),
            new Vector3(1f, 0f, 0f),
            new FnvRetailGeometryBound(new Vector3(10f, 0f, 0f), 2f),
            8f);

        Assert.Equal(new Vector3(9f, 0f, 0f), inside.Delta);
        Assert.Equal(9f, inside.CenterDistance);
        Assert.Equal(7f, inside.SurfaceDistance);
        Assert.Equal(0.875f, inside.Score);
        Assert.True(inside.BoundWithinEffectiveRadius);

        var edge = FnvRetailLightAssociationOracle.EvaluateInfluence(
            new Vector3(19f, 0f, 0f),
            Vector3.UnitX,
            new FnvRetailGeometryBound(new Vector3(10f, 0f, 0f), 2f),
            8f);

        Assert.Equal(1f, edge.Score);
        Assert.False(edge.BoundWithinEffectiveRadius);
    }

    [Fact]
    public void Influence_AllowsNegativeScoreWhenLightIsInsideGeometryBound()
    {
        var result = FnvRetailLightAssociationOracle.EvaluateInfluence(
            Vector3.Zero,
            Vector3.Zero,
            new FnvRetailGeometryBound(Vector3.Zero, 5f),
            10f);

        Assert.Equal(-5f, result.SurfaceDistance);
        Assert.Equal(-0.5f, result.Score);
        Assert.True(result.BoundWithinEffectiveRadius);
    }

    [Fact]
    public void FinalSort_IsStableAscendingAndDoesNotApplyPassCap()
    {
        FnvRetailAttachedLightCandidate[] attached =
        [
            Candidate(0x10, 12f),
            Candidate(0x20, 5f),
            Candidate(0x30, 5f),
            Candidate(0x40, 9f),
            Candidate(0x50, 7f)
        ];

        var sorted = FnvRetailLightAssociationOracle.StableSortForGeometry(
            attached,
            Vector3.Zero,
            new FnvRetailGeometryBound(Vector3.Zero, 2f));

        Assert.Equal(5, sorted.Length);
        Assert.Equal(
            [0x20u, 0x30u, 0x50u, 0x40u, 0x10u],
            sorted.Select(static candidate => candidate.EmitterReferenceFormId));
    }

    [Fact]
    public void ActiveNonShadowFilter_PreservesOrderAndUsesRecoveredFlags()
    {
        FnvRetailAttachedLightCandidate[] ordered =
        [
            Candidate(0x10, 1f),
            Candidate(0x20, 2f) with { FrustumCull = byte.MaxValue },
            Candidate(0x30, 3f) with { NiLightFlags = 1 },
            Candidate(0x40, 4f) with { CastShadow = 1 },
            Candidate(0x50, 5f)
        ];

        var active = FnvRetailLightAssociationOracle.FilterActiveNonShadowInOrder(ordered);

        Assert.Equal(
            [0x10u, 0x50u],
            active.Select(static candidate => candidate.EmitterReferenceFormId));
    }

    [Fact]
    public void ActiveNonShadowFilter_DependsOnlyOnTheThreeRecoveredRuntimeFields()
    {
        var diagnosticFieldsUnavailable = new FnvRetailAttachedLightCandidate(
            0,
            new Vector3(float.NaN),
            float.NaN);

        var active = FnvRetailLightAssociationOracle.FilterActiveNonShadowInOrder(
            [diagnosticFieldsUnavailable]);

        Assert.Single(active);
    }

    [Fact]
    public void Contract_RemainsIsolatedFromProductionRouting()
    {
        Assert.False(FnvRetailLightAssociationOracle.RuntimeSupported);
        var root = SourceContract.RepoRoot;
        var sourceRoot = Path.Combine(root, "src");
        var oraclePath = Path.GetFullPath(Path.Combine(
            sourceRoot,
            "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Lighting",
            "FnvRetailLightAssociationOracle.cs"));
        var consumers = Directory
            .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".cs" or ".hlsl")
            .Where(path => !Path.GetFullPath(path).Equals(
                oraclePath, StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains(
                nameof(FnvRetailLightAssociationOracle),
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.Empty(consumers);
    }

    [Fact]
    public void InvalidInputs_FailClosed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FnvRetailLightAssociationOracle.EvaluateInfluence(
                Vector3.Zero,
                Vector3.Zero,
                new FnvRetailGeometryBound(Vector3.Zero, -1f),
                1f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FnvRetailLightAssociationOracle.EvaluateInfluence(
                Vector3.Zero,
                Vector3.Zero,
                new FnvRetailGeometryBound(Vector3.Zero, 1f),
                0f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FnvRetailLightAssociationOracle.StableSortForGeometry(
                [Candidate(1, 1f) with { EffectiveRadius = 0f }],
                Vector3.Zero,
                new FnvRetailGeometryBound(Vector3.Zero, 1f)));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FnvRetailLightAssociationOracle.StableSortForGeometry(
                [],
                new Vector3(float.NaN),
                new FnvRetailGeometryBound(Vector3.Zero, 1f)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FnvRetailLightAssociationOracle.StableSortForGeometry(
                [Candidate(1, 1f)],
                Vector3.Zero,
                new FnvRetailGeometryBound(Vector3.Zero, -1f)));
    }

    private static FnvRetailAttachedLightCandidate Candidate(uint formId, float distance)
    {
        return new FnvRetailAttachedLightCandidate(formId, new Vector3(distance, 0f, 0f), 10f);
    }
}