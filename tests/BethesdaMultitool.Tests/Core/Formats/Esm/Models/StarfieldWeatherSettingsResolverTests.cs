using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Models;

public sealed class StarfieldWeatherSettingsResolverTests
{
    private const uint RootFormId = 0x00000100;
    private const uint ChildFormId = 0x00000200;
    private const uint GrandchildFormId = 0x00000300;

    [Fact]
    public void Resolve_ThreeLevelChain_RecursivelyOverlaysNullableLeaves()
    {
        var root = Full(
            RootFormId,
            new StarfieldWeatherSettingsPatch
            {
                ParentFormId = 0,
                DisplayNameKeywordFormId = 0x10,
                WeatherChoice = new StarfieldWeatherChoicePatch { Weight = 7 },
                ImageSpaceFormId = 0x20,
                Colors = new StarfieldWeatherColorSettingsPatch
                {
                    Sun = new StarfieldBlendableColorPatch
                    {
                        Operation = "RootOp",
                        Value = new StarfieldFloat4Patch { X = 1, Y = 2, Z = 3, W = 4 },
                        BlendAmount = 0.75f
                    },
                    Moonlight = new StarfieldBlendableColorPatch
                    {
                        Operation = "MoonRoot",
                        Value = new StarfieldFloat4Patch { X = 0.1f, Y = 0.2f, Z = 0.3f, W = 1 }
                    }
                },
                WindDirectionRange = new StarfieldBlendableFloatPatch
                {
                    Operation = "Add",
                    Value = 12,
                    BlendAmount = 0.5f
                },
                WindDirectionOverrideEnabled = true,
                VisibilityMultiplier = new StarfieldBlendableFloatPatch
                {
                    Operation = "Multiply",
                    Value = 1,
                    BlendAmount = 0.25f
                }
            });
        var child = Diff(
            ChildFormId,
            RootFormId,
            new StarfieldWeatherSettingsPatch
            {
                ParentFormId = RootFormId,
                Colors = new StarfieldWeatherColorSettingsPatch
                {
                    Sun = new StarfieldBlendableColorPatch
                    {
                        Operation = string.Empty,
                        Value = new StarfieldFloat4Patch { Z = 0 }
                    }
                },
                WindDirectionRange = new StarfieldBlendableFloatPatch { Value = 0 },
                VisibilityMultiplier = new StarfieldBlendableFloatPatch { BlendAmount = 0 }
            });
        var grandchild = Diff(
            GrandchildFormId,
            ChildFormId,
            new StarfieldWeatherSettingsPatch
            {
                ParentFormId = ChildFormId,
                DisplayNameKeywordFormId = 0,
                WeatherChoice = new StarfieldWeatherChoicePatch { Weight = 0 },
                Colors = new StarfieldWeatherColorSettingsPatch
                {
                    Sun = new StarfieldBlendableColorPatch
                    {
                        Value = new StarfieldFloat4Patch { X = 0, W = 9 },
                        BlendAmount = 0
                    }
                },
                WindDirectionOverrideEnabled = false
            });

        var result = Resolve(GrandchildFormId, root, child, grandchild);

        Assert.True(result.IsResolved);
        Assert.Equal(StarfieldWeatherSettingsResolutionStatus.Resolved, result.Status);
        Assert.Equal(new[] { RootFormId, ChildFormId, GrandchildFormId }, result.InheritanceChain.ToArray());
        Assert.Null(result.FailureFormId);
        Assert.Null(result.FailureDetail);

        var effective = Assert.IsType<StarfieldWeatherSettingsPatch>(result.EffectivePatch);
        Assert.Equal(ChildFormId, effective.ParentFormId);
        Assert.Equal(0u, effective.DisplayNameKeywordFormId);
        Assert.Equal(0u, effective.WeatherChoice?.Weight);
        Assert.Equal(0x20u, effective.ImageSpaceFormId);
        Assert.False(effective.WindDirectionOverrideEnabled);

        var sun = Assert.IsType<StarfieldBlendableColorPatch>(effective.Colors?.Sun);
        Assert.Equal(string.Empty, sun.Operation);
        Assert.Equal(0f, sun.Value?.X);
        Assert.Equal(2f, sun.Value?.Y);
        Assert.Equal(0f, sun.Value?.Z);
        Assert.Equal(9f, sun.Value?.W);
        Assert.Equal(0f, sun.BlendAmount);

        Assert.Equal("MoonRoot", effective.Colors?.Moonlight?.Operation);
        Assert.Equal("Add", effective.WindDirectionRange?.Operation);
        Assert.Equal(0f, effective.WindDirectionRange?.Value);
        Assert.Equal(0.5f, effective.WindDirectionRange?.BlendAmount);
        Assert.Equal("Multiply", effective.VisibilityMultiplier?.Operation);
        Assert.Equal(1f, effective.VisibilityMultiplier?.Value);
        Assert.Equal(0f, effective.VisibilityMultiplier?.BlendAmount);
    }

    [Fact]
    public void Resolve_OneFullRecord_ReturnsItsPatchAndOneElementChain()
    {
        var patch = new StarfieldWeatherSettingsPatch { ParentFormId = 0, TransDelta = 0 };

        var result = Resolve(RootFormId, Full(RootFormId, patch));

        Assert.True(result.IsResolved);
        Assert.Equal(patch, result.EffectivePatch);
        Assert.Equal(new[] { RootFormId }, result.InheritanceChain.ToArray());
    }

    [Fact]
    public void Resolve_UnknownTarget_FailsClosed()
    {
        var result = StarfieldWeatherSettingsResolver.Resolve(
            GrandchildFormId,
            new Dictionary<uint, StarfieldWeatherSettingsRecord>());

        AssertFailure(
            result,
            StarfieldWeatherSettingsResolutionStatus.TargetNotFound,
            GrandchildFormId,
            []);
    }

    [Fact]
    public void Resolve_MissingReferencedParent_FailsClosedWithReachableChain()
    {
        const uint missingParentFormId = 0x00BAD001;
        var child = Diff(ChildFormId, missingParentFormId, new StarfieldWeatherSettingsPatch());

        var result = Resolve(ChildFormId, child);

        AssertFailure(
            result,
            StarfieldWeatherSettingsResolutionStatus.MissingParent,
            missingParentFormId,
            [ChildFormId]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0u)]
    public void Resolve_DiffWithoutNonzeroParent_FailsClosed(uint? parentFormId)
    {
        var child = Diff(ChildFormId, parentFormId, new StarfieldWeatherSettingsPatch());

        var result = Resolve(ChildFormId, child);

        AssertFailure(
            result,
            StarfieldWeatherSettingsResolutionStatus.MissingParent,
            ChildFormId,
            [ChildFormId]);
    }

    [Fact]
    public void Resolve_DecodeFailureInAncestor_DoesNotReturnPartialEffectivePatch()
    {
        var root = Full(RootFormId, new StarfieldWeatherSettingsPatch { TransDelta = 1 }) with
        {
            DecodeFailure = "bad reflected field"
        };
        var child = Diff(
            ChildFormId,
            RootFormId,
            new StarfieldWeatherSettingsPatch { TransDelta = 2 });

        var result = Resolve(ChildFormId, root, child);

        AssertFailure(
            result,
            StarfieldWeatherSettingsResolutionStatus.DecodeFailure,
            RootFormId,
            [RootFormId, ChildFormId]);
        Assert.Equal("bad reflected field", result.FailureDetail);
    }

    [Fact]
    public void Resolve_NullPatchInAncestor_DoesNotReturnPartialEffectivePatch()
    {
        var root = Full(RootFormId, null);
        var child = Diff(ChildFormId, RootFormId, new StarfieldWeatherSettingsPatch());

        var result = Resolve(ChildFormId, root, child);

        AssertFailure(
            result,
            StarfieldWeatherSettingsResolutionStatus.MissingPatch,
            RootFormId,
            [RootFormId, ChildFormId]);
    }

    [Fact]
    public void Resolve_UnknownPayloadKind_DoesNotMasqueradeAsAValidPatch()
    {
        var record = new StarfieldWeatherSettingsRecord
        {
            FormId = RootFormId,
            PayloadKind = StarfieldWeatherSettingsPayloadKind.Unknown,
            Patch = new StarfieldWeatherSettingsPatch { TransDelta = 0 }
        };

        var result = Resolve(RootFormId, record);

        AssertFailure(
            result,
            StarfieldWeatherSettingsResolutionStatus.UnknownPayloadKind,
            RootFormId,
            [RootFormId]);
    }

    [Theory]
    [InlineData(null, 1u)]
    [InlineData(0u, 0u)]
    [InlineData(1u, 0u)]
    public void Resolve_FullObjectWithInvalidParentContract_FailsClosed(
        uint? outerParentFormId,
        uint? reflectedParentFormId)
    {
        var record = Full(
            RootFormId,
            new StarfieldWeatherSettingsPatch { ParentFormId = reflectedParentFormId }) with
        {
            ParentFormId = outerParentFormId
        };

        var result = Resolve(RootFormId, record);

        AssertFailure(
            result,
            StarfieldWeatherSettingsResolutionStatus.ParentContractViolation,
            RootFormId,
            [RootFormId]);
    }

    [Fact]
    public void Resolve_FullObjectWithoutExplicitReflectedZeroParent_FailsClosed()
    {
        var record = new StarfieldWeatherSettingsRecord
        {
            FormId = RootFormId,
            PayloadKind = StarfieldWeatherSettingsPayloadKind.FullObject,
            Patch = new StarfieldWeatherSettingsPatch()
        };

        var result = Resolve(RootFormId, record);

        AssertFailure(
            result,
            StarfieldWeatherSettingsResolutionStatus.ParentContractViolation,
            RootFormId,
            [RootFormId]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0u)]
    [InlineData(0x00000999u)]
    public void Resolve_DiffWithMismatchedReflectedParent_FailsClosed(uint? reflectedParentFormId)
    {
        var root = Full(RootFormId, new StarfieldWeatherSettingsPatch());
        var child = new StarfieldWeatherSettingsRecord
        {
            FormId = ChildFormId,
            ParentFormId = RootFormId,
            PayloadKind = StarfieldWeatherSettingsPayloadKind.Diff,
            Patch = new StarfieldWeatherSettingsPatch { ParentFormId = reflectedParentFormId }
        };

        var result = Resolve(ChildFormId, root, child);

        AssertFailure(
            result,
            StarfieldWeatherSettingsResolutionStatus.ParentContractViolation,
            ChildFormId,
            [ChildFormId]);
    }

    [Fact]
    public void Resolve_SelfCycle_FailsClosed()
    {
        var record = Diff(ChildFormId, ChildFormId, new StarfieldWeatherSettingsPatch());

        var result = Resolve(ChildFormId, record);

        AssertFailure(
            result,
            StarfieldWeatherSettingsResolutionStatus.InheritanceCycle,
            ChildFormId,
            [ChildFormId]);
    }

    [Fact]
    public void Resolve_MultiRecordCycle_FailsClosedDeterministically()
    {
        var first = Diff(RootFormId, ChildFormId, new StarfieldWeatherSettingsPatch());
        var second = Diff(ChildFormId, GrandchildFormId, new StarfieldWeatherSettingsPatch());
        var third = Diff(GrandchildFormId, ChildFormId, new StarfieldWeatherSettingsPatch());

        var result = Resolve(RootFormId, first, second, third);

        AssertFailure(
            result,
            StarfieldWeatherSettingsResolutionStatus.InheritanceCycle,
            ChildFormId,
            [GrandchildFormId, ChildFormId, RootFormId]);
    }

    [Fact]
    public void Resolve_ChainBeyondDepthCap_FailsBeforeReadingNextAncestor()
    {
        const uint greatGrandparentFormId = 0x00000400;
        var root = Full(greatGrandparentFormId, new StarfieldWeatherSettingsPatch());
        var parent = Diff(RootFormId, greatGrandparentFormId, new StarfieldWeatherSettingsPatch());
        var child = Diff(ChildFormId, RootFormId, new StarfieldWeatherSettingsPatch());
        var grandchild = Diff(GrandchildFormId, ChildFormId, new StarfieldWeatherSettingsPatch());
        var records = Index(root, parent, child, grandchild);

        var result = StarfieldWeatherSettingsResolver.Resolve(GrandchildFormId, records, maxDepth: 3);

        AssertFailure(
            result,
            StarfieldWeatherSettingsResolutionStatus.DepthLimitExceeded,
            greatGrandparentFormId,
            [RootFormId, ChildFormId, GrandchildFormId]);
    }

    [Fact]
    public void Resolve_ChainExactlyAtDepthCap_Resolves()
    {
        var root = Full(RootFormId, new StarfieldWeatherSettingsPatch { TransDelta = 4 });
        var child = Diff(ChildFormId, RootFormId, new StarfieldWeatherSettingsPatch());
        var grandchild = Diff(GrandchildFormId, ChildFormId, new StarfieldWeatherSettingsPatch());
        var records = Index(root, child, grandchild);

        var result = StarfieldWeatherSettingsResolver.Resolve(GrandchildFormId, records, maxDepth: 3);

        Assert.True(result.IsResolved);
        Assert.Equal(4f, result.EffectivePatch?.TransDelta);
    }

    private static StarfieldWeatherSettingsResolution Resolve(
        uint targetFormId,
        params StarfieldWeatherSettingsRecord[] records)
    {
        return StarfieldWeatherSettingsResolver.Resolve(targetFormId, Index(records));
    }

    private static IReadOnlyDictionary<uint, StarfieldWeatherSettingsRecord> Index(
        params StarfieldWeatherSettingsRecord[] records)
    {
        return records.ToDictionary(record => record.FormId);
    }

    private static StarfieldWeatherSettingsRecord Full(
        uint formId,
        StarfieldWeatherSettingsPatch? patch)
    {
        return new StarfieldWeatherSettingsRecord
        {
            FormId = formId,
            PayloadKind = StarfieldWeatherSettingsPayloadKind.FullObject,
            Patch = patch is null ? null : patch with { ParentFormId = patch.ParentFormId ?? 0 }
        };
    }

    private static StarfieldWeatherSettingsRecord Diff(
        uint formId,
        uint? parentFormId,
        StarfieldWeatherSettingsPatch patch)
    {
        return new StarfieldWeatherSettingsRecord
        {
            FormId = formId,
            ParentFormId = parentFormId,
            PayloadKind = StarfieldWeatherSettingsPayloadKind.Diff,
            Patch = patch with { ParentFormId = patch.ParentFormId ?? parentFormId }
        };
    }

    private static void AssertFailure(
        StarfieldWeatherSettingsResolution result,
        StarfieldWeatherSettingsResolutionStatus expectedStatus,
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
