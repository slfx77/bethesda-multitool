using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Models;

public sealed class StarfieldSunPresetResolverTests
{
    [Fact]
    public void Complete_root_resolves_without_inventing_transformations()
    {
        var root = Root(0x10);

        var result = StarfieldSunPresetResolver.Resolve(0x10, Index(root));

        Assert.True(result.IsResolved);
        Assert.Equal(StarfieldSunPresetResolutionStatus.Resolved, result.Status);
        Assert.Equal(new uint[] { 0x10 }, result.InheritanceChain);
        Assert.Same(root.Patch, result.EffectivePatch);
        Assert.Equal(20_000f, result.EffectivePatch?.SunIlluminance);
        Assert.Equal(50f, result.EffectivePatch?.DuskDawnPreset?.TransitionStartAngle);
    }

    [Fact]
    public void Diff_recursively_merges_nested_leaves_and_preserves_authored_zero_and_empty()
    {
        var root = Root(0x10);
        var diff = Diff(
            0x20,
            0x10,
            new StarfieldSunPresetPatch
            {
                ParentFormId = 0x10,
                SunColor = new StarfieldSunPresetFloat4Patch { X = 0 },
                SunIlluminance = 0,
                SunDiskTexture = string.Empty,
                DuskDawnPreset = new StarfieldSunPresetDawnDuskPatch
                {
                    DirectionalColor = new StarfieldSunPresetFloat4Patch { Y = 0 },
                    TransitionStartAngle = 0
                },
                NightPreset = new StarfieldSunPresetNightPatch
                {
                    DirectionalColor = new StarfieldSunPresetFloat4Patch { Z = 0 },
                    DirectionalIlluminance = 0,
                    GlareColor = new StarfieldSunPresetFloat4Patch { W = 0 }
                }
            });

        var result = StarfieldSunPresetResolver.Resolve(0x20, Index(root, diff));

        Assert.True(result.IsResolved);
        Assert.Equal(new uint[] { 0x10, 0x20 }, result.InheritanceChain);
        Assert.Equal(0f, result.EffectivePatch?.SunColor?.X);
        Assert.Equal(0.2f, result.EffectivePatch?.SunColor?.Y);
        Assert.Equal(0f, result.EffectivePatch?.SunIlluminance);
        Assert.Equal(string.Empty, result.EffectivePatch?.SunDiskTexture);
        Assert.Equal(0f, result.EffectivePatch?.DuskDawnPreset?.DirectionalColor?.Y);
        Assert.Equal(0.7f, result.EffectivePatch?.DuskDawnPreset?.DirectionalColor?.X);
        Assert.Equal(0f, result.EffectivePatch?.DuskDawnPreset?.TransitionStartAngle);
        Assert.Equal(80f, result.EffectivePatch?.DuskDawnPreset?.TransitionEndAngle);
        Assert.Equal(0f, result.EffectivePatch?.NightPreset?.DirectionalColor?.Z);
        Assert.Equal(10f, result.EffectivePatch?.NightPreset?.DirectionalColor?.X);
        Assert.Equal(0f, result.EffectivePatch?.NightPreset?.DirectionalIlluminance);
        Assert.Equal(0f, result.EffectivePatch?.NightPreset?.GlareColor?.W);
        Assert.Equal(0f, result.EffectivePatch?.NightPreset?.GlareColor?.X);
    }

    [Fact]
    public void Missing_target_reports_target_not_found()
    {
        var result = StarfieldSunPresetResolver.Resolve(
            0xDEAD, new Dictionary<uint, StarfieldSunPresetRecord>());

        AssertFailure(result, StarfieldSunPresetResolutionStatus.TargetNotFound, 0xDEAD);
    }

    [Fact]
    public void Decode_failure_is_not_treated_as_an_authored_patch()
    {
        var invalid = Root(0x10) with { DecodeFailure = "bad BETH stream", Patch = null };

        var result = StarfieldSunPresetResolver.Resolve(0x10, Index(invalid));

        AssertFailure(result, StarfieldSunPresetResolutionStatus.DecodeFailure, 0x10);
        Assert.Contains("bad BETH", result.FailureDetail!);
    }

    [Fact]
    public void Unknown_payload_kind_fails_closed()
    {
        var invalid = Root(0x10) with { PayloadKind = StarfieldSunPresetPayloadKind.Unknown };

        var result = StarfieldSunPresetResolver.Resolve(0x10, Index(invalid));

        AssertFailure(result, StarfieldSunPresetResolutionStatus.UnknownPayloadKind, 0x10);
    }

    [Fact]
    public void Missing_patch_fails_closed()
    {
        var invalid = Root(0x10) with { Patch = null };

        var result = StarfieldSunPresetResolver.Resolve(0x10, Index(invalid));

        AssertFailure(result, StarfieldSunPresetResolutionStatus.MissingPatch, 0x10);
    }

    [Fact]
    public void Full_root_requires_absent_RFDP_and_reflected_zero_parent()
    {
        var outerParent = Root(0x10) with { ParentFormId = 0x99 };
        AssertFailure(
            StarfieldSunPresetResolver.Resolve(0x10, Index(outerParent)),
            StarfieldSunPresetResolutionStatus.ParentContractViolation,
            0x10);

        var reflectedParent = Root(0x10) with
        {
            Patch = CompletePatch() with { ParentFormId = 0x99 }
        };
        AssertFailure(
            StarfieldSunPresetResolver.Resolve(0x10, Index(reflectedParent)),
            StarfieldSunPresetResolutionStatus.ParentContractViolation,
            0x10);
    }

    [Fact]
    public void Incomplete_full_root_cannot_seed_a_resolved_chain()
    {
        var invalid = Root(0x10) with
        {
            Patch = CompletePatch() with
            {
                NightPreset = new StarfieldSunPresetNightPatch
                {
                    DirectionalColor = FullColor(1, 1, 1, 1),
                    DirectionalIlluminance = 100,
                    GlareColor = new StarfieldSunPresetFloat4Patch { X = 0, Y = 0, Z = 0 }
                }
            }
        };

        var result = StarfieldSunPresetResolver.Resolve(0x10, Index(invalid));

        AssertFailure(result, StarfieldSunPresetResolutionStatus.MissingPatch, 0x10);
    }

    [Fact]
    public void Diff_requires_nonzero_outer_RFDP()
    {
        foreach (var outerParent in new uint?[] { null, 0 })
        {
            var invalid = new StarfieldSunPresetRecord
            {
                FormId = 0x20,
                ParentFormId = outerParent,
                PayloadKind = StarfieldSunPresetPayloadKind.Diff,
                Patch = new StarfieldSunPresetPatch { ParentFormId = 0x10 }
            };

            var result = StarfieldSunPresetResolver.Resolve(0x20, Index(Root(0x10), invalid));

            AssertFailure(result, StarfieldSunPresetResolutionStatus.MissingParent, 0x20);
        }
    }

    [Fact]
    public void Diff_requires_explicit_reflected_parent_equal_to_RFDP()
    {
        foreach (var reflectedParent in new uint?[] { null, 0x11 })
        {
            var invalid = Diff(
                0x20,
                0x10,
                new StarfieldSunPresetPatch { ParentFormId = reflectedParent });

            var result = StarfieldSunPresetResolver.Resolve(0x20, Index(Root(0x10), invalid));

            AssertFailure(result, StarfieldSunPresetResolutionStatus.ParentContractViolation, 0x20);
        }
    }

    [Fact]
    public void Missing_parent_reports_the_missing_FormID_after_index_lookup()
    {
        var diff = Diff(
            0x20,
            0x99,
            new StarfieldSunPresetPatch { ParentFormId = 0x99 });

        var result = StarfieldSunPresetResolver.Resolve(0x20, Index(diff));

        AssertFailure(result, StarfieldSunPresetResolutionStatus.MissingParent, 0x99);
        Assert.Equal(new uint[] { 0x20 }, result.InheritanceChain);
    }

    [Fact]
    public void Cycle_is_detected_before_any_patch_is_exposed()
    {
        var first = Diff(1, 2, new StarfieldSunPresetPatch { ParentFormId = 2 });
        var second = Diff(2, 1, new StarfieldSunPresetPatch { ParentFormId = 1 });

        var result = StarfieldSunPresetResolver.Resolve(1, Index(first, second));

        AssertFailure(result, StarfieldSunPresetResolutionStatus.InheritanceCycle, 1);
        Assert.Null(result.EffectivePatch);
    }

    [Fact]
    public void Depth_cap_is_enforced_before_following_an_additional_record()
    {
        var root = Root(1);
        var diff = Diff(2, 1, new StarfieldSunPresetPatch { ParentFormId = 1 });

        var result = StarfieldSunPresetResolver.Resolve(2, Index(root, diff), maxDepth: 1);

        AssertFailure(result, StarfieldSunPresetResolutionStatus.DepthLimitExceeded, 1);
    }

    private static StarfieldSunPresetRecord Root(uint formId) =>
        new()
        {
            FormId = formId,
            PayloadKind = StarfieldSunPresetPayloadKind.FullObject,
            Patch = CompletePatch()
        };

    private static StarfieldSunPresetRecord Diff(
        uint formId,
        uint outerParent,
        StarfieldSunPresetPatch patch) =>
        new()
        {
            FormId = formId,
            ParentFormId = outerParent,
            PayloadKind = StarfieldSunPresetPayloadKind.Diff,
            Patch = patch
        };

    private static StarfieldSunPresetPatch CompletePatch() =>
        new()
        {
            ParentFormId = 0,
            SunColor = FullColor(0.1f, 0.2f, 0.3f, 1),
            SunIlluminance = 20_000,
            SunGlareColor = FullColor(0.4f, 0.5f, 0.6f, 1),
            SunDiskTexture = "Data/Textures/Sky/SunDisk_color.dds",
            SunDiskScreenSizeMin = 0.02f,
            SunDiskScreenSizeMax = 0.138f,
            DuskDawnPreset = new StarfieldSunPresetDawnDuskPatch
            {
                DirectionalColor = FullColor(0.7f, 0.8f, 0.9f, 1),
                TransitionStartAngle = 50,
                TransitionEndAngle = 80
            },
            NightPreset = new StarfieldSunPresetNightPatch
            {
                DirectionalColor = FullColor(10, 11, 12, 1),
                DirectionalIlluminance = 100,
                GlareColor = FullColor(0, 0, 0, 1)
            }
        };

    private static StarfieldSunPresetFloat4Patch FullColor(
        float x,
        float y,
        float z,
        float w) =>
        new() { X = x, Y = y, Z = z, W = w };

    private static IReadOnlyDictionary<uint, StarfieldSunPresetRecord> Index(
        params StarfieldSunPresetRecord[] records) =>
        records.ToDictionary(record => record.FormId);

    private static void AssertFailure(
        StarfieldSunPresetResolution result,
        StarfieldSunPresetResolutionStatus status,
        uint failureFormId)
    {
        Assert.False(result.IsResolved);
        Assert.Equal(status, result.Status);
        Assert.Equal(failureFormId, result.FailureFormId);
        Assert.Null(result.EffectivePatch);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureDetail));
    }
}
