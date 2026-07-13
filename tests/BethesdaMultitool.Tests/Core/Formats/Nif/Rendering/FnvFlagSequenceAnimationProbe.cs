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
///     Guards modern NiControllerManager animation end-to-end on the real FNV NCR cloth flag
///     (<c>nv_ncr_flag_s.nif</c>, NIF 20.2.0.7/BS 34): the idle sequence's controlled blocks must
///     yield all 9 transform tracks (the cloth rig TTail1-3 / BTail1 / MTail1-3 plus the
///     exporter-mangled "MTail2@#0"/"MTail3@#1" branch bones, resolved through the
///     NiDefaultAVObjectPalette), the clip window must come from the sequence tail (0→42.87 s,
///     CYCLE_LOOP), and the skin export must bind bones block-exactly. Reads the real FNV meshes
///     BSA; skips when absent (CI).
/// </summary>
public class FnvFlagSequenceAnimationProbe
{
    private const string FnvMeshesBsa =
        @"C:\Users\mmc99\source\repos\Xbox360MemoryCarver\Sample\Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Meshes.bsa";

    [Fact]
    public void NcrFlag_CollectsSequenceRig_Clip_And_Skin()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(File.Exists(FnvMeshesBsa), "FNV meshes BSA not present (dev-machine-only asset).");

        using var extractor = new BsaExtractor(FnvMeshesBsa);
        var archive = BsaParser.Parse(FnvMeshesBsa);
        var file = archive.AllFiles.First(f =>
            string.Equals(f.FullPath, @"meshes\clutter\flags\nv_ncr_flag_s.nif", StringComparison.OrdinalIgnoreCase));
        var data = extractor.ExtractFile(file);
        var nif = NifParser.Parse(data);
        Assert.NotNull(nif);

        var signature = NifAnimationDetector.Detect(data, nif);
        Assert.True(signature.HasInternalSkin);
        Assert.True(signature.HasControllerSequenceTracks);
        Assert.False(signature.HasNodeKeyframeTracks); // manager-driven, no per-node controllers

        var animation = NifControllerSequenceTrackCollector.Collect(data, nif);
        Assert.NotNull(animation);

        // The Idle sequence controls the full 9-bone cloth rig, including the exporter-mangled
        // branch bones that resolve through the object palette.
        var trackedSlots = Enumerable.Range(0, animation.Tracks.Length)
            .Where(i => animation.Tracks[i] is not null)
            .ToArray();
        Assert.Equal(9, trackedSlots.Length);
        var trackedNames = trackedSlots.Select(i => animation.Bones[i].Name).ToArray();
        Assert.Contains("MTail2", trackedNames);
        Assert.Contains("MTail2@#0", trackedNames);
        Assert.Contains("MTail3@#1", trackedNames);

        // The wave is authored long-form: every track carries a dense key set (~1270 keys / 42.9 s).
        Assert.All(trackedSlots, i => Assert.True(animation.Tracks[i]!.RotationKeys.Length > 1000));

        // Clip = the sequence's own authored window, looping.
        Assert.Equal(0f, animation.ClipStart, 2);
        Assert.Equal(42.867f, animation.ClipStop, 2);
        Assert.True(animation.ClipLoops);

        // The pose actually moves inside the clip.
        Span<Matrix4x4> restWorlds = stackalloc Matrix4x4[animation.Bones.Length];
        var restOnly = animation with { Tracks = new NifNodeTrack?[animation.Bones.Length] };
        NifAnimationPoseEvaluator.EvaluateBoneWorlds(restOnly, 1f, restWorlds);
        Span<Matrix4x4> posedWorlds = stackalloc Matrix4x4[animation.Bones.Length];
        NifAnimationPoseEvaluator.EvaluateBoneWorlds(animation, 1f, posedWorlds);
        var moved = false;
        for (var i = 0; i < animation.Bones.Length && !moved; i++)
        {
            moved = (posedWorlds[i].Translation - restWorlds[i].Translation).Length() > 0.05f;
        }

        Assert.True(moved, "mid-clip pose should differ from the rest pose");

        // Skin export: block-exact bone binding — within each skinned shape every mapped anim bone
        // must be a distinct source node (name-only mapping collapses the twin chains).
        var skins = NifSubmeshSkinExporter.Export(data, nif, animation);
        Assert.NotNull(skins);
        Assert.True(skins.Count >= 1);
        foreach (var skin in skins.Values)
        {
            var mapped = skin.SkinBoneToAnimBone.Where(idx => idx >= 0).ToArray();
            Assert.NotEmpty(mapped);
            Assert.All(mapped, idx => Assert.InRange(idx, 0, animation.Bones.Length - 1));

            var sourceBlocks = mapped.Select(idx => animation.Bones[idx].SourceBlockIndex).ToArray();
            Assert.All(sourceBlocks, b => Assert.True(b >= 0));
            Assert.Equal(sourceBlocks.Length, sourceBlocks.Distinct().Count());
        }
    }
}
