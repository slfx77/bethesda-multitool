using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Bsa.Parsing;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Guards the creature ambient-clip policy on the real Morrowind guar (<c>r\Guar.NIF</c>):
///     creature files concatenate EVERY animation group on one timeline (Idle 0→3.27 s, then
///     Idle2–6, Walk, Run, Attack1–3, Death, Knockout, Turns, Hit out to 27.3 s), and the viewer
///     must loop only the plain Idle group — a full-range loop cycled placed guars through their
///     whole repertoire, deaths included. Reads the real Morrowind.bsa; skips when absent (CI).
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public class Tes3CreatureIdleClipProbe
{
    private const string Bsa = @"E:\SteamLibrary\SteamApps\common\Morrowind\Data Files\Morrowind.bsa";

    [Fact]
    public void Guar_ClipIsThePlainIdleLoop_NotTheFullTimeline()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(File.Exists(Bsa), "Morrowind.bsa not present (dev-machine-only asset).");

        using var extractor = new BsaExtractor(Bsa);
        var archive = BsaParser.Parse(Bsa);
        var file = archive.AllFiles.First(f =>
            string.Equals(f.FullPath, @"meshes\r\guar.nif", StringComparison.OrdinalIgnoreCase));
        var data = extractor.ExtractFile(file);
        var nif = NifParser.Parse(data);
        Assert.NotNull(nif);

        var animation = NifNodeKeyframeTrackCollector.Collect(data, nif);
        Assert.NotNull(animation);

        // The rig itself spans the whole 27.3 s file…
        Assert.True(animation.Bones.Length > 40, $"guar rig should be large (got {animation.Bones.Length})");
        var maxKeyTime = animation.Tracks.Where(t => t is not null)
            .Max(t => t!.RotationKeys.Length > 0 ? t.RotationKeys[^1].Time : 0f);
        Assert.True(maxKeyTime > 25f, $"tracks should span the full timeline (got {maxKeyTime})");

        // …but the PLAY WINDOW is the plain Idle group's loop only.
        Assert.Equal(0f, animation.ClipStart, 2);
        Assert.Equal(3.267f, animation.ClipStop, 2);
        Assert.True(animation.ClipLoops);
    }
}