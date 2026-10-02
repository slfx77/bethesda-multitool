using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

/// <summary>Protects creature list and render asset paths from host-dependent separator behavior.</summary>
public sealed class CreatureAssetPathTests
{
    /// <summary>Resolves bare NIFZ names from their skeleton while retaining already-qualified source paths.</summary>
    /// <param name="skeleton">Authored skeleton path or absent skeleton metadata.</param>
    /// <param name="body">The first authored NIFZ body entry.</param>
    /// <param name="expected">The independently specified source-relative model identity.</param>
    [Theory]
    [InlineData(@"meshes\creatures\daedroth\skeleton.nif", "daedroth.nif", @"meshes\creatures\daedroth\daedroth.nif")]
    [InlineData("meshes/creatures/daedroth/skeleton.nif", "daedroth.nif", @"meshes\creatures\daedroth\daedroth.nif")]
    [InlineData(@"meshes/creatures\daedroth/skeleton.nif", "daedroth.nif", @"meshes\creatures\daedroth\daedroth.nif")]
    [InlineData(@"meshes\creatures\daedroth\skeleton.nif", @"creatures\other\body.nif", @"creatures\other\body.nif")]
    [InlineData(@"meshes\creatures\daedroth\skeleton.nif", "creatures/other/body.nif", "creatures/other/body.nif")]
    [InlineData("skeleton.nif", "body.nif", "body.nif")]
    [InlineData(null, "body.nif", "body.nif")]
    [InlineData(@"meshes\creatures\daedroth\skeleton.nif", "", @"meshes\creatures\daedroth")]
    public void BodyNamesResolveFromVirtualSkeleton(string? skeleton, string body, string expected)
    {
        var creature = new CreatureScanEntry(null, null, skeleton, [body], null, null, 0);
        Assert.Equal(expected, creature.ResolveBodyModelPath());
    }

    /// <summary>Idle lookup uses the same virtual parent and preserves qualified animation paths.</summary>
    /// <param name="skeleton">Authored skeleton path or absent skeleton metadata.</param>
    /// <param name="animation">A candidate KFFZ animation entry.</param>
    /// <param name="expected">The expected idle asset identity, or null when no idle can be resolved.</param>
    [Theory]
    [InlineData(@"meshes\creatures\daedroth\skeleton.nif", "mtidle.kf", @"meshes\creatures\daedroth\mtidle.kf")]
    [InlineData("meshes/creatures/daedroth/skeleton.nif", "mtidle.kf", @"meshes\creatures\daedroth\mtidle.kf")]
    [InlineData(@"meshes\creatures\daedroth\skeleton.nif", @"locomotion\mtidle.kf", @"locomotion\mtidle.kf")]
    [InlineData("skeleton.nif", "mtidle.kf", null)]
    [InlineData(null, "mtidle.kf", null)]
    [InlineData(@"meshes\creatures\daedroth\skeleton.nif", "attack.kf", null)]
    public void IdleNamesResolveFromVirtualSkeleton(string? skeleton, string animation, string? expected)
    {
        var creature = new CreatureScanEntry(null, null, skeleton, null, [animation], null, 0);
        Assert.Equal(expected, creature.ResolveIdleAnimationPath());
    }

    /// <summary>The render lookup does not duplicate a forward-slash meshes root or retain mixed separators.</summary>
    /// <param name="path">An authored or resolved body path.</param>
    /// <param name="expected">The exact archive lookup spelling.</param>
    [Theory]
    [InlineData("meshes/creatures/daedroth/body.nif", @"meshes\creatures\daedroth\body.nif")]
    [InlineData(@"meshes\creatures\daedroth\body.nif", @"meshes\creatures\daedroth\body.nif")]
    [InlineData(@"\meshes\creatures\daedroth\body.nif", @"meshes\creatures\daedroth\body.nif")]
    [InlineData("/meshes/creatures/daedroth/body.nif", @"meshes\creatures\daedroth\body.nif")]
    [InlineData("creatures/daedroth/body.nif", @"meshes\creatures\daedroth\body.nif")]
    [InlineData(@"\creatures\daedroth\body.nif", @"meshes\creatures\daedroth\body.nif")]
    [InlineData("body.nif", @"meshes\body.nif")]
    public void RenderPathsUseOneMeshRoot(string path, string expected)
    {
        Assert.Equal(expected, CreatureAssetPath.NormalizeMeshPath(path));
    }
}
