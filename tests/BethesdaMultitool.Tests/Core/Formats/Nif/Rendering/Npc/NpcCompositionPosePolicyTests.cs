using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class NpcCompositionPosePolicyTests
{
    [Fact]
    public void Tes4OneHandAttachmentPose_DrivesActorBodyPose()
    {
        var appearance = BuildAppearance(BethesdaGame.Oblivion, WeaponType.OneHandMelee, "onehandidle.kf");

        var bodyPose = NpcCompositionPlanner.ResolveBodyPoseKfPath(
            appearance,
            new NpcCompositionOptions());

        Assert.Equal("onehandidle.kf", bodyPose);
    }

    [Theory]
    [InlineData(WeaponType.TwoHandMelee, "twohandidle.kf")]
    [InlineData(WeaponType.TwoHandHandle, "staffidle.kf")]
    [InlineData(WeaponType.TwoHandRifle, "bowidle.kf")]
    [InlineData(WeaponType.OneHandMelee, null)]
    public void UnvalidatedOrNonTes4PoseFamilies_RetainDefaultBodyIdle(
        WeaponType weaponType,
        string? attachmentPoseKfPath)
    {
        var appearance = BuildAppearance(BethesdaGame.Oblivion, weaponType, attachmentPoseKfPath);

        var bodyPose = NpcCompositionPlanner.ResolveBodyPoseKfPath(
            appearance,
            new NpcCompositionOptions());

        Assert.Null(bodyPose);
    }

    [Fact]
    public void ExplicitAnimationAndBindPose_TakePrecedenceOverEquipmentPose()
    {
        var appearance = BuildAppearance(BethesdaGame.Oblivion, WeaponType.OneHandMelee, "onehandidle.kf");

        Assert.Equal(
            "specialidle.kf",
            NpcCompositionPlanner.ResolveBodyPoseKfPath(
                appearance,
                new NpcCompositionOptions { AnimOverride = "specialidle.kf" }));
        Assert.Null(NpcCompositionPlanner.ResolveBodyPoseKfPath(
            appearance,
            new NpcCompositionOptions { BindPose = true }));
        Assert.Null(NpcCompositionPlanner.ResolveBodyPoseKfPath(
            appearance,
            new NpcCompositionOptions { IncludeWeapon = false }));
    }

    [Fact]
    public void FalloutOneHandWeapon_DoesNotActivateTes4BodyPosePolicy()
    {
        var appearance = BuildAppearance(
            BethesdaGame.FalloutNewVegas,
            WeaponType.OneHandMelee,
            "onehandidle.kf");

        Assert.Null(NpcCompositionPlanner.ResolveBodyPoseKfPath(
            appearance,
            new NpcCompositionOptions()));
    }

    private static NpcAppearance BuildAppearance(
        BethesdaGame game,
        WeaponType weaponType,
        string? attachmentPoseKfPath)
    {
        return new NpcAppearance
        {
            Game = game,
            WeaponVisual = new WeaponVisual
            {
                IsVisible = true,
                WeaponType = weaponType,
                AttachmentMode = WeaponAttachmentMode.HolsterPose,
                AttachmentPoseKfPath = attachmentPoseKfPath
            }
        };
    }
}