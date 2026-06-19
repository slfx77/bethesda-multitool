using BethesdaMultitool.Core.Formats.Nif.Rendering;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Pip-Boy shape filtering. The PipBoyOn/PipBoyOff variant semantics are
///     decompile-verified against BipedAnim::AttachSkinnedObject (MemDebug XEX,
///     VA 0x822FBE40): the engine lowercases each shape name and prefix-matches —
///     with a Pip-Boy worn it removes "pipboyoff*" shapes, without one it removes
///     "pipboyon*" shapes.
/// </summary>
public sealed class NifPipBoyShapeTests
{
    [Theory]
    [InlineData("ScreenLit")]
    [InlineData("ScreenLit:0")]
    [InlineData("glare")]
    [InlineData("glare:0")]
    [InlineData("PipboyLightEffect:0")]
    [InlineData("StatsGlow:0")]
    [InlineData("ItemsGlow")]
    [InlineData("DataGlow:1")]
    public void ScreenShape_MatchesWithAndWithoutInstanceSuffix(string name)
    {
        Assert.True(NifBlockParsers.IsPipBoyScreenShape(name));
    }

    [Theory]
    [InlineData("pipboyscreen")]
    [InlineData("pipboyscreen:0")]
    [InlineData("PipBoyButton01:0")]
    [InlineData("TabKnob:0")]
    [InlineData("RadNeedle:1")]
    [InlineData("PipBoyArmFemale:0")]
    [InlineData(null)]
    [InlineData("")]
    public void ScreenShape_KeepsPhysicalPipBoyGeometry(string? name)
    {
        Assert.False(NifBlockParsers.IsPipBoyScreenShape(name));
    }

    [Theory]
    [InlineData("PipBoyOff", true, true)] // worn → full sleeve suppressed
    [InlineData("PipBoyOff", false, false)] // not worn → full sleeve kept
    [InlineData("PipBoyOn", true, false)] // worn → cut sleeve kept
    [InlineData("PipBoyOn", false, true)] // not worn → cut sleeve suppressed
    [InlineData("PipboyOn", false, true)] // vault suit f/outfit.nif mixed casing
    [InlineData("pipboyoff:0", true, true)] // engine prefix-matches, suffix tolerated
    [InlineData("UpperBody", true, false)]
    [InlineData("UpperBody", false, false)]
    [InlineData(null, true, false)]
    public void PipBoyVariant_SuppressesOppositeStateShape(string? name, bool pipBoyVisible, bool expected)
    {
        Assert.Equal(expected, NifBlockParsers.IsSuppressedPipBoyVariantShape(name, pipBoyVisible));
    }

    [Fact]
    public void PipBoyVariant_OnPrefixDoesNotSwallowOffShapes()
    {
        // Without a Pip-Boy the "pipboyon" prefix must not match "PipBoyOff" —
        // they diverge at index 7, mirroring the engine's strncmp.
        Assert.False(NifBlockParsers.IsSuppressedPipBoyVariantShape("PipBoyOff", false));
        Assert.False(NifBlockParsers.IsSuppressedPipBoyVariantShape("PipBoyOff:0", false));
    }

    [Fact]
    public void AnyPipBoy_DetectsBipedSlotFlag()
    {
        var pipBoy = new EquippedItem { BipedFlags = EquippedItem.PipBoyBipedFlag };
        var outfit = new EquippedItem { BipedFlags = 0x04 };

        Assert.True(EquippedItem.AnyPipBoy([outfit, pipBoy]));
        Assert.False(EquippedItem.AnyPipBoy([outfit]));
        Assert.False(EquippedItem.AnyPipBoy(null));
        Assert.False(EquippedItem.AnyPipBoy([]));
    }
}