using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Core.Imaging;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Real-asset checks for the Shadowkey <c>.zsk</c>-to-scene bridge, over the 21 retail skyboxes.
///     Opt-in: set <c>RUN_BUCKET_B=1</c>.
///     <para>
///         Two of the builder's readings are re-derived here from the retail bytes rather than
///         restated: which pole is the zenith, and whether the corner UVs are normalised or texels.
///         Both were wrong in the obvious direction — the shell's vertices are mostly negative in y,
///         and <see cref="ShadowkeySkyCorner" /> presents its fields as 8.8 fixed point — so a test
///         that took the builder's word for either would be worth nothing.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class ShadowkeySkySceneRetailTests
{
    private static readonly string[] AllZones =
    [
        "azra", "broken1", "broken2", "Crypt3", "crypt1", "crypt2", "delfhide", "drgnfld",
        "dstar_e", "dstar_w", "erthcave", "fearfrst", "ffarena", "GhstPass", "GlacierCrawl",
        "lakvan", "LothCav", "raiders", "snowline", "stouttp", "twilite"
    ];

    /// <summary>The twelve zones whose shell is the 30-vertex outdoor dome.</summary>
    private static readonly string[] OutdoorZones =
    [
        "azra", "delfhide", "drgnfld", "dstar_e", "dstar_w", "fearfrst", "GhstPass",
        "GlacierCrawl", "lakvan", "raiders", "snowline", "stouttp"
    ];

    private static string RequireRoot()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Travels.ShadowkeyRoot();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Shadowkey (system/apps/6R51)"));
        return root;
    }

    private static ShadowkeySkybox Sky(string root, string zone)
    {
        return ShadowkeySkybox.Parse(
            ShadowkeyCompressedFile.Inflate(
                File.ReadAllBytes(Path.Combine(root, zone + ".zsk")), zone + ".zsk"),
            zone + ".zsk");
    }

    private static Palette ZonePalette(string root, string zone)
    {
        return Palette.FromRgb8(File.ReadAllBytes(Path.Combine(root, zone + ".pal")));
    }

    /// <summary>The normalised UVs every face corner referencing <paramref name="vertex" /> carries.</summary>
    private static List<(float U, float V)> UvsOf(ShadowkeySkybox sky, int vertex)
    {
        var uvs = new List<(float U, float V)>();
        foreach (var face in sky.Faces)
        {
            Span<int> vertices = [face.V0, face.V1, face.V2];
            Span<int> corners = [face.C0, face.C1, face.C2];
            for (var k = 0; k < 3; k++)
            {
                if (vertices[k] == vertex)
                {
                    var corner = sky.Corners[corners[k]];
                    uvs.Add((corner.U / ShadowkeySkySceneBuilder.UvScale,
                        corner.V / ShadowkeySkySceneBuilder.UvScale));
                }
            }
        }

        return uvs;
    }

    private static float Radius(ShadowkeySkyVertex v)
    {
        return MathF.Sqrt((v.X * (float)v.X) + (v.Z * (float)v.Z));
    }

    /// <summary>
    ///     ⚑ The reading that decides which way up the sky goes. The texture is a polar projection
    ///     with the zenith at its centre, so the zenith is whichever pole maps to (0.5, 0.5). On
    ///     ALL TWELVE outdoor domes — painted and blank alike — that is the pole at POSITIVE y, at
    ///     an offset of 0.004, while the -y pole reaches 0.500, the rim. So the shell is y-up,
    ///     matching the models, despite three quarters of its vertices being negative.
    /// </summary>
    [Fact]
    public void TheZenithIsThePoleAtPositiveY()
    {
        var root = RequireRoot();

        foreach (var zone in OutdoorZones)
        {
            var sky = Sky(root, zone);

            // The two poles are the vertices with (near) zero horizontal radius.
            var widest = sky.Vertices.Max(Radius);
            var poles = Enumerable.Range(0, sky.Vertices.Count)
                .Where(i => Radius(sky.Vertices[i]) < widest * 0.05f)
                .ToArray();
            Assert.Equal(2, poles.Length);

            var high = poles.MaxBy(i => sky.Vertices[i].Y);
            var low = poles.MinBy(i => sky.Vertices[i].Y);

            var highUvs = UvsOf(sky, high);
            var lowUvs = UvsOf(sky, low);
            Assert.NotEmpty(highUvs);
            Assert.NotEmpty(lowUvs);

            // EVERY corner of the +y pole is at the centre; the -y pole REACHES the rim. Stated
            // as a minimum the second half would fail on raiders, whose UV table really is
            // different (see RaidersHasItsOwnUvTable) — and loosening it for all twelve to
            // accommodate one would throw away the separation on the other eleven.
            var highOffset = highUvs.Max(uv =>
                MathF.Max(MathF.Abs(uv.U - 0.5f), MathF.Abs(uv.V - 0.5f)));
            var lowReach = lowUvs.Max(uv =>
                MathF.Max(MathF.Abs(uv.U - 0.5f), MathF.Abs(uv.V - 0.5f)));

            Assert.True(
                highOffset < 0.02f,
                $"{zone}: the +y pole maps {highOffset:F3} from the texture centre, not to it.");
            Assert.True(
                lowReach > 0.4f,
                $"{zone}: the -y pole only reaches {lowReach:F3} from the centre, so the poles do not separate.");
        }
    }

    /// <summary>
    ///     ⚑ The corner UVs are normalised, not the 8.8 texels the record's own accessor implies.
    ///     Under the texel reading V would span the 256-pixel height exactly but U would address
    ///     only half of the 512-pixel width — and the images say the whole width is real, because in
    ///     every painted sky the right half differs from the left. Normalised, both axes span the
    ///     image.
    /// </summary>
    [Fact]
    public void CornerUvsAreNormalisedAndTheWholeImageIsUsed()
    {
        var root = RequireRoot();

        var painted = 0;
        foreach (var zone in AllZones)
        {
            var sky = Sky(root, zone);

            Assert.All(sky.Corners, corner =>
            {
                Assert.InRange(corner.U / ShadowkeySkySceneBuilder.UvScale, 0f, 1f);
                Assert.InRange(corner.V / ShadowkeySkySceneBuilder.UvScale, 0f, 1f);
            });

            if (!sky.HasPaintedTexture)
            {
                continue;
            }

            painted++;

            // The half-width claim, straight off the pixels: a painted sky's right half is not a
            // copy of its left, so U cannot be addressing only 256 of the 512 columns.
            var indices = sky.Texture.Indices;
            var different = false;
            for (var row = 0; row < ShadowkeySkybox.TextureHeight && !different; row++)
            {
                var offset = row * ShadowkeySkybox.TextureWidth;
                for (var column = 0; column < ShadowkeySkybox.TextureWidth / 2; column++)
                {
                    if (indices[offset + column] !=
                        indices[offset + column + ShadowkeySkybox.TextureWidth / 2])
                    {
                        different = true;
                        break;
                    }
                }
            }

            Assert.True(different, $"{zone}: the sky's two halves are identical, so the width claim fails.");
        }

        Assert.Equal(8, painted);
    }

    /// <summary>
    ///     Every zone's sky builds into one renderable part whose geometry encloses the zone and
    ///     whose UVs stay inside the unit square.
    /// </summary>
    [Fact]
    public void EveryZoneSkyBuildsAndEnclosesTheZone()
    {
        var root = RequireRoot();

        foreach (var zone in AllZones)
        {
            var sky = Sky(root, zone);
            var scene = new BethesdaViewerScene(zone, BethesdaViewerScenePurpose.ClassicMesh);

            var part = ShadowkeySkySceneBuilder.Add(scene, sky, ZonePalette(root, zone), zone, 128, 128);
            var submesh = part.Submesh;

            Assert.Equal(sky.Faces.Count * 3, submesh.Triangles.Length);
            Assert.All(submesh.Positions, value => Assert.True(float.IsFinite(value), $"{zone}: bad vertex."));
            Assert.All(submesh.UVs!, uv => Assert.InRange(uv, 0f, 1f));

            // The horizon ring must fall outside the zone's own diagonal, or the sky would cut
            // through the terrain it is meant to surround.
            var half = MathF.Sqrt(128f * 128f + 128f * 128f) * 0.5f;
            var reach = 0f;
            for (var i = 0; i + 2 < submesh.Positions.Length; i += 3)
            {
                var dx = submesh.Positions[i] - 64f;
                var dy = submesh.Positions[i + 1] - 64f;
                reach = MathF.Max(reach, MathF.Sqrt(dx * dx + dy * dy));
            }

            Assert.True(reach > half, $"{zone}: the sky reaches {reach:F1}, inside the {half:F1} diagonal.");
        }
    }

    /// <summary>
    ///     ⚠ <c>raiders</c> contradicts the reader's own note that the twelve outdoor zones "differ
    ///     only in the texture". Its shell mesh is indeed the shared one — 30 vertices, 168 corners,
    ///     56 faces, like every other dome — but its UV TABLE is not: 35 distinct pairs against 44
    ///     everywhere else, with part of the nadir cap collapsed onto the texture centre instead of
    ///     the rim. Its image is blank, so nothing is visibly wrong in game, which is presumably how
    ///     it shipped. Pinned because it is the one dome that breaks a shared-asset assumption, and
    ///     because a future reader tempted to dedupe the twelve domes needs to know.
    /// </summary>
    [Fact]
    public void RaidersHasItsOwnUvTable()
    {
        var root = RequireRoot();

        var raiders = Sky(root, "raiders");
        var others = OutdoorZones
            .Where(zone => !string.Equals(zone, "raiders", StringComparison.Ordinal))
            .Select(zone => Sky(root, zone))
            .ToArray();

        // Same mesh as the rest.
        Assert.Equal(30, raiders.Vertices.Count);
        Assert.Equal(168, raiders.Corners.Count);
        Assert.Equal(56, raiders.Faces.Count);

        var raidersDistinct = raiders.Corners.Distinct().Count();
        Assert.Equal(35, raidersDistinct);
        Assert.All(others, sky => Assert.Equal(44, sky.Corners.Distinct().Count()));
        Assert.False(raiders.HasPaintedTexture, "raiders' sky image is blank, which is why this never showed.");
    }

    /// <summary>
    ///     The nine interior zones ship one byte-identical shell with a blank image — there is no
    ///     sky underground. Pinned because it explains why an interior scene shows a flat colour
    ///     overhead rather than a missing texture.
    /// </summary>
    [Fact]
    public void InteriorZonesShareOneBlankShell()
    {
        var root = RequireRoot();

        var interiors = AllZones.Where(zone => !OutdoorZones.Contains(zone, StringComparer.Ordinal)).ToArray();
        Assert.Equal(9, interiors.Length);

        foreach (var zone in interiors)
        {
            var sky = Sky(root, zone);
            Assert.False(sky.IsOutdoorClass, $"{zone}: expected the interior gap length.");
            Assert.False(sky.HasPaintedTexture, $"{zone}: an interior carries a painted sky.");
            Assert.Equal(98, sky.Vertices.Count);
        }
    }
}