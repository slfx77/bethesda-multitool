using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Models;

public sealed class StarfieldAtmosphereResolverTests
{
    private const uint RootFormId = 0x00000100;
    private const uint ChildFormId = 0x00000200;
    private const uint GrandchildFormId = 0x00000300;

    [Fact]
    public void Resolve_OverlaysAbsentAndExplicitZeroWithoutMutatingSources()
    {
        var root = Full(RootFormId, sun: 0x111, climate: 0x222);
        var child = Diff(ChildFormId, RootFormId, new StarfieldAtmospherePatch
        {
            ParentFormId = RootFormId,
            SunPresetOverrideFormId = 0
        });
        var grandchild = Diff(GrandchildFormId, ChildFormId, new StarfieldAtmospherePatch
        {
            ClimateOverrideFormId = 0x00064D14
        });
        var rootPatch = root.Patch;
        var childPatch = child.Patch;
        var grandchildPatch = grandchild.Patch;

        var result = Resolve(GrandchildFormId, root, child, grandchild);

        Assert.True(result.IsResolved, result.FailureDetail);
        Assert.Equal(
            [RootFormId, ChildFormId, GrandchildFormId],
            result.InheritanceChain.ToArray());
        Assert.NotNull(result.EffectivePatch);
        Assert.Equal(RootFormId, result.EffectivePatch.ParentFormId);
        Assert.True(result.EffectivePatch.SunPresetOverrideFormId.HasValue);
        Assert.Equal(0u, result.EffectivePatch.SunPresetOverrideFormId.Value);
        Assert.Equal(0x00064D14u, result.EffectivePatch.ClimateOverrideFormId);
        Assert.NotSame(rootPatch, result.EffectivePatch);
        Assert.Same(rootPatch, root.Patch);
        Assert.Same(childPatch, child.Patch);
        Assert.Same(grandchildPatch, grandchild.Patch);
        Assert.Equal(0x111u, root.Patch?.SunPresetOverrideFormId);
        Assert.Null(child.Patch?.ClimateOverrideFormId);
    }

    [Fact]
    public void Resolve_EarthDiff_InheritsSunAndOverridesClimate()
    {
        const uint commonParent = 0x0020CDD3;
        const uint earthAtmosphere = 0x0000C9D1;
        var root = Full(commonParent, sun: 0, climate: 0);
        var earth = Diff(earthAtmosphere, commonParent, new StarfieldAtmospherePatch
        {
            ParentFormId = commonParent,
            ClimateOverrideFormId = 0x00064D14
        });

        var result = Resolve(earthAtmosphere, root, earth);

        Assert.True(result.IsResolved, result.FailureDetail);
        Assert.Equal([commonParent, earthAtmosphere], result.InheritanceChain.ToArray());
        Assert.True(result.EffectivePatch?.SunPresetOverrideFormId.HasValue);
        Assert.Equal(0u, result.EffectivePatch!.SunPresetOverrideFormId!.Value);
        Assert.Equal(0x00064D14u, result.EffectivePatch.ClimateOverrideFormId);
    }

    [Fact]
    public void Resolve_MissingTarget_FailsClosed()
    {
        var result = Resolve(ChildFormId, Full(RootFormId));

        AssertFailure(
            result,
            StarfieldAtmosphereResolutionStatus.TargetNotFound,
            ChildFormId,
            []);
    }

    [Fact]
    public void Resolve_MissingParentRecord_FailsClosed()
    {
        var child = Diff(ChildFormId, RootFormId, new StarfieldAtmospherePatch());

        var result = Resolve(ChildFormId, child);

        AssertFailure(
            result,
            StarfieldAtmosphereResolutionStatus.MissingParent,
            RootFormId,
            [ChildFormId]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0u)]
    public void Resolve_DiffWithoutNonzeroOuterParent_FailsClosed(uint? parentFormId)
    {
        var child = Diff(ChildFormId, parentFormId, new StarfieldAtmospherePatch());

        var result = Resolve(ChildFormId, child);

        AssertFailure(
            result,
            StarfieldAtmosphereResolutionStatus.MissingParent,
            ChildFormId,
            [ChildFormId]);
    }

    [Fact]
    public void Resolve_DecodeFailureInAncestor_FailsClosed()
    {
        var root = Full(RootFormId) with
        {
            Patch = null,
            DecodeFailure = "synthetic malformed ATMO"
        };
        var child = Diff(ChildFormId, RootFormId, new StarfieldAtmospherePatch());

        var result = Resolve(ChildFormId, root, child);

        AssertFailure(
            result,
            StarfieldAtmosphereResolutionStatus.DecodeFailure,
            RootFormId,
            [RootFormId, ChildFormId]);
        Assert.Contains("malformed", result.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_MissingDecodedPatch_FailsClosed()
    {
        var root = Full(RootFormId) with { Patch = null };

        var result = Resolve(RootFormId, root);

        AssertFailure(
            result,
            StarfieldAtmosphereResolutionStatus.MissingPatch,
            RootFormId,
            [RootFormId]);
    }

    [Fact]
    public void Resolve_IncompleteFullProjection_FailsClosed()
    {
        var root = Full(RootFormId) with
        {
            Patch = new StarfieldAtmospherePatch
            {
                ParentFormId = 0,
                SunPresetOverrideFormId = 0
            }
        };

        var result = Resolve(RootFormId, root);

        AssertFailure(
            result,
            StarfieldAtmosphereResolutionStatus.MissingPatch,
            RootFormId,
            [RootFormId]);
    }

    [Fact]
    public void Resolve_UnknownPayloadKind_FailsClosed()
    {
        var record = Full(RootFormId) with
        {
            PayloadKind = StarfieldAtmospherePayloadKind.Unknown
        };

        var result = Resolve(RootFormId, record);

        AssertFailure(
            result,
            StarfieldAtmosphereResolutionStatus.UnknownPayloadKind,
            RootFormId,
            [RootFormId]);
    }

    [Fact]
    public void Resolve_DiffWithContradictoryReflectedParent_FailsClosed()
    {
        var root = Full(RootFormId);
        var child = Diff(ChildFormId, RootFormId, new StarfieldAtmospherePatch
        {
            ParentFormId = GrandchildFormId
        });

        var result = Resolve(ChildFormId, root, child);

        AssertFailure(
            result,
            StarfieldAtmosphereResolutionStatus.ParentContractViolation,
            ChildFormId,
            [ChildFormId]);
    }

    [Fact]
    public void Resolve_SelfCycle_FailsClosed()
    {
        var record = Diff(ChildFormId, ChildFormId, new StarfieldAtmospherePatch
        {
            ParentFormId = ChildFormId
        });

        var result = Resolve(ChildFormId, record);

        AssertFailure(
            result,
            StarfieldAtmosphereResolutionStatus.InheritanceCycle,
            ChildFormId,
            [ChildFormId]);
    }

    [Fact]
    public void Resolve_MultiRecordCycle_FailsClosedDeterministically()
    {
        var first = Diff(RootFormId, ChildFormId, new StarfieldAtmospherePatch());
        var second = Diff(ChildFormId, GrandchildFormId, new StarfieldAtmospherePatch());
        var third = Diff(GrandchildFormId, ChildFormId, new StarfieldAtmospherePatch());

        var result = Resolve(RootFormId, first, second, third);

        AssertFailure(
            result,
            StarfieldAtmosphereResolutionStatus.InheritanceCycle,
            ChildFormId,
            [GrandchildFormId, ChildFormId, RootFormId]);
    }

    [Fact]
    public void Resolve_ChainBeyondDepthCap_FailsBeforeReadingNextAncestor()
    {
        const uint greatGrandparentFormId = 0x00000400;
        var root = Full(greatGrandparentFormId);
        var parent = Diff(RootFormId, greatGrandparentFormId, new StarfieldAtmospherePatch());
        var child = Diff(ChildFormId, RootFormId, new StarfieldAtmospherePatch());
        var grandchild = Diff(GrandchildFormId, ChildFormId, new StarfieldAtmospherePatch());
        var records = Index(root, parent, child, grandchild);

        var result = StarfieldAtmosphereResolver.Resolve(
            GrandchildFormId, records, maxDepth: 3);

        AssertFailure(
            result,
            StarfieldAtmosphereResolutionStatus.DepthLimitExceeded,
            greatGrandparentFormId,
            [RootFormId, ChildFormId, GrandchildFormId]);
    }

    [Fact]
    public void Resolve_ChainExactlyAtDepthCap_Resolves()
    {
        var root = Full(RootFormId, climate: 0xABCD);
        var child = Diff(ChildFormId, RootFormId, new StarfieldAtmospherePatch());
        var grandchild = Diff(GrandchildFormId, ChildFormId, new StarfieldAtmospherePatch());
        var records = Index(root, child, grandchild);

        var result = StarfieldAtmosphereResolver.Resolve(
            GrandchildFormId, records, maxDepth: 3);

        Assert.True(result.IsResolved, result.FailureDetail);
        Assert.Equal(0xABCDu, result.EffectivePatch?.ClimateOverrideFormId);
    }

    private static StarfieldAtmosphereResolution Resolve(
        uint targetFormId,
        params StarfieldAtmosphereRecord[] records)
    {
        return StarfieldAtmosphereResolver.Resolve(targetFormId, Index(records));
    }

    private static IReadOnlyDictionary<uint, StarfieldAtmosphereRecord> Index(
        params StarfieldAtmosphereRecord[] records)
    {
        return records.ToDictionary(record => record.FormId);
    }

    private static StarfieldAtmosphereRecord Full(
        uint formId,
        uint sun = 0,
        uint climate = 0)
    {
        return new StarfieldAtmosphereRecord
        {
            FormId = formId,
            PayloadKind = StarfieldAtmospherePayloadKind.FullObject,
            Patch = new StarfieldAtmospherePatch
            {
                ParentFormId = 0,
                SunPresetOverrideFormId = sun,
                ClimateOverrideFormId = climate
            }
        };
    }

    private static StarfieldAtmosphereRecord Diff(
        uint formId,
        uint? parentFormId,
        StarfieldAtmospherePatch patch)
    {
        return new StarfieldAtmosphereRecord
        {
            FormId = formId,
            ParentFormId = parentFormId,
            PayloadKind = StarfieldAtmospherePayloadKind.Diff,
            Patch = patch
        };
    }

    private static void AssertFailure(
        StarfieldAtmosphereResolution result,
        StarfieldAtmosphereResolutionStatus expectedStatus,
        uint expectedFailureFormId,
        IReadOnlyList<uint> expectedChain)
    {
        Assert.False(result.IsResolved);
        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(result.EffectivePatch);
        Assert.Equal(expectedFailureFormId, result.FailureFormId);
        Assert.Equal(expectedChain.ToArray(), result.InheritanceChain.ToArray());
        Assert.NotNull(result.FailureDetail);
    }
}
