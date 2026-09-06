using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

/// <summary>
///     Retail regression gate for Oblivion's Daedroth. Its CREA record does not name an idle in
///     KFFZ and its mesh directory has <c>idle.kf</c>, not either Fallout-style <c>mtidle.kf</c>
///     location, so the composition planner must discover and apply the sibling pose.
/// </summary>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class OblivionDaedrothAnimationRetailTests
{
    private const uint DaedrothFormId = 0x0002B19A;
    private const string IdlePath = @"meshes\creatures\daedroth\idle.kf";
    private const string RootMtIdlePath = @"meshes\creatures\daedroth\mtidle.kf";
    private const string LocomotionMtIdlePath =
        @"meshes\creatures\daedroth\locomotion\mtidle.kf";

    [Fact]
    public async Task DaedrothWithoutKffz_UsesSiblingIdlePose()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esmPath = RealAssetPaths.Masters.Oblivion();
        var meshesPath = RealAssetPaths.SteamGameFile(
            "Oblivion",
            @"Data\Oblivion - Meshes.bsa");
        Assert.SkipWhen(esmPath is null, RealAssetPaths.SkipMessage("Oblivion.esm"));
        Assert.SkipWhen(meshesPath is null, RealAssetPaths.SkipMessage("Oblivion - Meshes.bsa"));

        var cancellationToken = TestContext.Current.CancellationToken;
        var result = await RealAssetEsmCache.LoadAsync(esmPath!, cancellationToken);
        var records = result.RawResult.EsmRecords
                      ?? throw new InvalidOperationException(
                          "Retail ESM load did not retain its record descriptors.");
        var accessor = result.Accessor
                       ?? throw new InvalidOperationException(
                           "Retail ESM load did not retain its memory-mapped accessor.");

        var rawCreature = Assert.Single(records.MainRecords,
            static record => record.RecordType == "CREA" && record.FormId == DaedrothFormId);
        var storedPayload = new byte[checked((int)rawCreature.DataSize)];
        Assert.Equal(storedPayload.Length, accessor.ReadArray(
            rawCreature.Offset + rawCreature.HeaderSize,
            storedPayload,
            0,
            storedPayload.Length));
        var payload = rawCreature.IsCompressed
            ? EsmParser.DecompressRecordData(storedPayload, rawCreature.IsBigEndian)
              ?? throw new InvalidDataException("Retail Daedroth CREA could not be decompressed.")
            : storedPayload;
        Assert.DoesNotContain(
            EsmParser.ParseSubrecords(payload, rawCreature.IsBigEndian),
            static subrecord => subrecord.Signature == "KFFZ");

        var resolver = NpcAppearanceResolver.Build(
            new MmfMemoryAccessor(accessor),
            result.RawResult.FileSize,
            records.MainRecords,
            records.BigEndianRecords > 0,
            records.Game,
            cancellationToken: cancellationToken);
        var creature = Assert.Contains(DaedrothFormId, resolver.GetAllCreatures());
        Assert.True(creature.AnimationPaths is null or { Length: 0 });
        Assert.Null(creature.ResolveIdleAnimationPath());

        using var meshArchives = MeshArchiveSet.Open(meshesPath!, null);
        Assert.True(meshArchives.TryResolvePath(IdlePath, out _, out var resolvedIdlePath));
        Assert.Equal(IdlePath, resolvedIdlePath, ignoreCase: true);
        Assert.False(meshArchives.TryResolvePath(RootMtIdlePath, out _, out _));
        Assert.False(meshArchives.TryResolvePath(LocomotionMtIdlePath, out _, out _));

        var posedPlan = Assert.IsType<CreatureCompositionPlan>(CreatureCompositionPlanner.CreatePlan(
            creature,
            meshArchives,
            resolver,
            new CreatureCompositionOptions { IncludeWeapon = false }));
        Assert.NotNull(posedPlan.AnimationOverrides);
        Assert.NotNull(posedPlan.BoneTransforms);
        var animationOverrides = posedPlan.AnimationOverrides;
        var posedTransforms = posedPlan.BoneTransforms;
        Assert.Equal(IdlePath, posedPlan.AnimationSourcePath, ignoreCase: true);
        Assert.Equal(57, animationOverrides.Count);
        Assert.Contains("Bip01 Pelvis", animationOverrides);
        Assert.Contains("Bip01 Tail4", animationOverrides);
        Assert.NotEmpty(posedTransforms);

        var bindPlan = Assert.IsType<CreatureCompositionPlan>(CreatureCompositionPlanner.CreatePlan(
            creature,
            meshArchives,
            resolver,
            new CreatureCompositionOptions { IncludeWeapon = false, BindPose = true }));
        Assert.Null(bindPlan.AnimationOverrides);
        Assert.NotNull(bindPlan.BoneTransforms);
        var bindTransforms = bindPlan.BoneTransforms;
        Assert.Contains(animationOverrides.Keys, boneName =>
            posedTransforms.TryGetValue(boneName, out var posed) &&
            bindTransforms.TryGetValue(boneName, out var bind) &&
            MaximumElementDelta(posed, bind) > 1e-4f);
    }

    private static float MaximumElementDelta(Matrix4x4 left, Matrix4x4 right)
    {
        return new[]
        {
            MathF.Abs(left.M11 - right.M11), MathF.Abs(left.M12 - right.M12),
            MathF.Abs(left.M13 - right.M13), MathF.Abs(left.M14 - right.M14),
            MathF.Abs(left.M21 - right.M21), MathF.Abs(left.M22 - right.M22),
            MathF.Abs(left.M23 - right.M23), MathF.Abs(left.M24 - right.M24),
            MathF.Abs(left.M31 - right.M31), MathF.Abs(left.M32 - right.M32),
            MathF.Abs(left.M33 - right.M33), MathF.Abs(left.M34 - right.M34),
            MathF.Abs(left.M41 - right.M41), MathF.Abs(left.M42 - right.M42),
            MathF.Abs(left.M43 - right.M43), MathF.Abs(left.M44 - right.M44)
        }.Max();
    }
}
