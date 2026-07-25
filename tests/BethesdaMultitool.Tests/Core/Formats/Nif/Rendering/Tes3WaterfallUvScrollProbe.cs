using System.Numerics;
using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Bsa.Parsing;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Guards Morrowind UV-scroll animation ("waterfalls render static"): the Vivec waterfall's two
///     shapes are driven by NiUVController + NiUVData with a constant looping V ramp (t=0 v=0 →
///     t=1 v=−4), which the extractor must reduce to a per-submesh scroll velocity of (0,−4)/sec.
///     Lava (in_lava_1024.nif) rides the same mechanism. Empirically arbitrates the
///     NifKeyGroupReader stride table against real 4.0.0.2 data. Reads the real Morrowind.bsa;
///     skips when absent (CI).
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public class Tes3WaterfallUvScrollProbe
{
    private const string Bsa = @"E:\SteamLibrary\SteamApps\common\Morrowind\Data Files\Morrowind.bsa";

    [Fact]
    public void Waterfall_BothShapes_ResolveConstantVScroll()
    {
        var model = ExtractModel(@"meshes\x\ex_vivec_waterfall_05.nif");

        // The two sheets scroll at different authored speeds (parallax layering): −4 and −3 V/sec.
        Assert.Equal(2, model.Submeshes.Count);
        var velocities = model.Submeshes
            .Select(s => s.UvScrollVelocity)
            .OrderBy(v => v.Y)
            .ToArray();
        Assert.All(velocities, v => Assert.Equal(0f, v.X, 3));
        Assert.Equal(-4f, velocities[0].Y, 3);
        Assert.Equal(-3f, velocities[1].Y, 3);
    }

    [Fact]
    public void Lava_ScrollingShapes_ResolveNonZeroVelocity()
    {
        var model = ExtractModel(@"meshes\i\in_lava_1024.nif");

        // The magma shapes scroll their molten texture via the same NiUVController mechanism.
        // Exact velocity is authored data — assert the mechanism fires, not a magic constant.
        Assert.Contains(model.Submeshes, s => s.UvScrollVelocity != Vector2.Zero);
    }

    private static NifRenderableModel ExtractModel(string meshPath)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(File.Exists(Bsa), "Morrowind.bsa not present (dev-machine-only asset).");

        using var extractor = new BsaExtractor(Bsa);
        var archive = BsaParser.Parse(Bsa);
        var file = archive.AllFiles.First(f => string.Equals(f.FullPath, meshPath, StringComparison.OrdinalIgnoreCase));
        var data = extractor.ExtractFile(file);

        var nif = NifParser.Parse(data);
        Assert.NotNull(nif);
        var model = NifGeometryExtractor.Extract(data, nif);
        Assert.NotNull(model);
        return model;
    }
}