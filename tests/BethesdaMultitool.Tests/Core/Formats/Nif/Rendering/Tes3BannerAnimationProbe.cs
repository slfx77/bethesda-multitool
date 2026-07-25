using System.Numerics;
using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Bsa.Parsing;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Guards Morrowind keyframe animation end-to-end on the real tavern banner
///     (<c>furn_banner_tavern_01.nif</c>): the collector must yield the Root Bone/Bone02/Bone03
///     tracks (rot TCB ≈10 keys, pos Bezier 3 keys — this empirically arbitrates the
///     NifKeyGroupReader stride table on real 4.0.0.2 data), the clip must be the full authored
///     controller range (0→4 s — passive decor loops the whole animation and returns to the hang,
///     not the violent Idle3 sub-window), the rest pose must reproduce the authored bone offsets,
///     and the skin export must cover every vertex. Reads the real Morrowind.bsa; skips when
///     absent (CI).
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public class Tes3BannerAnimationProbe
{
    private const string Bsa = @"E:\SteamLibrary\SteamApps\common\Morrowind\Data Files\Morrowind.bsa";

    [Fact]
    public void Banner_CollectsRig_Clip_And_Skin()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(File.Exists(Bsa), "Morrowind.bsa not present (dev-machine-only asset).");

        using var extractor = new BsaExtractor(Bsa);
        var archive = BsaParser.Parse(Bsa);
        var file = archive.AllFiles.First(f =>
            string.Equals(f.FullPath, @"meshes\f\furn_banner_tavern_01.nif", StringComparison.OrdinalIgnoreCase));
        var data = extractor.ExtractFile(file);
        var nif = NifParser.Parse(data);
        Assert.NotNull(nif);

        var animation = NifNodeKeyframeTrackCollector.Collect(data, nif);
        Assert.NotNull(animation);

        // Three keyframe-driven bones; the authored key set arbitrates the stride table.
        var tracked = animation.Tracks.Where(t => t is not null).Cast<NifNodeTrack>().ToArray();
        Assert.Equal(3, tracked.Length);
        Assert.All(tracked, t => Assert.Equal(NifKeyInterpolation.Tbc, t.RotationInterpolation));
        Assert.Contains(tracked, t => t.RotationKeys.Length >= 8);
        Assert.Contains(tracked, t => t.TranslationKeys.Length >= 2 &&
                                      t.TranslationInterpolation == NifKeyInterpolation.Quadratic);

        // Clip = the full authored range (0→4 s), looped: the banner plays its whole animation and
        // returns to the hang each cycle rather than parking in Idle3's violent sub-window.
        Assert.Equal(0f, animation.ClipStart, 2);
        Assert.Equal(4f, animation.ClipStop, 2);
        Assert.True(animation.ClipLoops);

        // Rest pose ≈ authored bone offsets: Bone02 sits ~30.7 units below its parent, Bone03
        // ~41 below Bone02 (probed from the NIF's node transforms).
        var boneNames = animation.Bones.Select(b => b.Name).ToArray();
        Assert.Contains("Bone02", boneNames);
        Assert.Contains("Bone03", boneNames);
        var bone02 = animation.Bones.First(b => b.Name == "Bone02");
        var bone03 = animation.Bones.First(b => b.Name == "Bone03");
        Assert.Equal(-30.7f, bone02.RestTranslation.Z, 1);
        Assert.Equal(-41.0f, bone03.RestTranslation.Z, 1);

        // The pose at any time inside the loop differs from the rest pose (the sway is real).
        Span<Matrix4x4> restWorlds = stackalloc Matrix4x4[animation.Bones.Length];
        var restOnly = animation with { Tracks = new NifNodeTrack?[animation.Bones.Length] };
        NifAnimationPoseEvaluator.EvaluateBoneWorlds(restOnly, animation.ClipStart, restWorlds);
        Span<Matrix4x4> posedWorlds = stackalloc Matrix4x4[animation.Bones.Length];
        NifAnimationPoseEvaluator.EvaluateBoneWorlds(animation, animation.ClipStart + 0.6f, posedWorlds);
        var moved = false;
        for (var i = 0; i < animation.Bones.Length && !moved; i++)
        {
            moved = (posedWorlds[i].Translation - restWorlds[i].Translation).Length() > 0.05f;
        }

        Assert.True(moved, "mid-loop pose should differ from the rest pose");

        // Skin export: the cloth shape fully weighted, bones mapped into the rig.
        var skins = NifSubmeshSkinExporter.Export(data, nif, animation);
        Assert.NotNull(skins);
        var skin = Assert.Single(skins).Value;
        Assert.True(skin.VertexCount > 0);
        Assert.All(skin.SkinBoneToAnimBone, idx => Assert.InRange(idx, 0, animation.Bones.Length - 1));
        var weightedVerts = 0;
        for (var v = 0; v < skin.VertexCount; v++)
        {
            var sum = skin.BoneWeights[v * 4] + skin.BoneWeights[v * 4 + 1] +
                      skin.BoneWeights[v * 4 + 2] + skin.BoneWeights[v * 4 + 3];
            if (sum > 0.99f)
            {
                weightedVerts++;
            }
        }

        Assert.Equal(skin.VertexCount, weightedVerts);
    }
}