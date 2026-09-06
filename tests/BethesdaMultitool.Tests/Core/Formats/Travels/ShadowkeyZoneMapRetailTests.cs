using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Opt-in checks of the 21 retail Shadowkey (N-Gage) zones (<c>RUN_BUCKET_B=1</c>). None of
///     these per-zone families has a magic number — the identification is entirely arithmetic, so
///     the exact numbers pinned here ARE the format evidence: a declared inflated length that must
///     match, a 132-byte map header that only the 128x128/64x64 pair of grid sizes can produce, a
///     prototype table that must tile at 36 bytes and be exactly as large as the grid's largest
///     index, a texture bank that must tile at 16,384, and two lookup tables that are always
///     131,072 bytes.
///     <para>
///         The census is exact because the fixture is a fixed retail install: 21 zones, 326
///         textures, 66,829 prototypes, 331,776 cells. A count that moves means the reader moved.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class ShadowkeyZoneMapRetailTests
{
    /// <summary>
    ///     The per-zone census measured 2026-09-05: grid size, open and blocked cell counts,
    ///     <c>.zcp</c> prototype count and <c>.ztx</c> texture count.
    /// </summary>
    public static readonly (string Zone, int Width, int Height, int Open, int Blocked, int Prototypes, int Textures)[] Census =
    [
        ("azra", 128, 128, 15659, 725, 11828, 21),
        ("broken1", 128, 128, 11890, 4494, 448, 19),
        ("broken2", 128, 128, 8733, 7651, 209, 18),
        ("crypt1", 128, 128, 12610, 3774, 1350, 17),
        ("crypt2", 128, 128, 11009, 5375, 341, 16),
        ("Crypt3", 128, 128, 3299, 13085, 270, 18),
        ("delfhide", 128, 128, 10744, 5640, 1799, 18),
        ("drgnfld", 128, 128, 15453, 931, 4725, 15),
        ("dstar_e", 128, 128, 13325, 3059, 897, 22),
        ("dstar_w", 128, 128, 13196, 3188, 1946, 19),
        ("erthcave", 128, 128, 8144, 8240, 5782, 15),
        ("fearfrst", 128, 128, 15472, 912, 6982, 10),
        ("ffarena", 64, 64, 1876, 2220, 965, 7),
        ("GhstPass", 128, 128, 15324, 1060, 10303, 19),
        ("GlacierCrawl", 128, 128, 15049, 1335, 182, 6),
        ("lakvan", 128, 128, 12971, 3413, 1057, 12),
        ("LothCav", 128, 128, 11776, 4608, 2703, 8),
        ("raiders", 128, 128, 12445, 3939, 99, 12),
        ("snowline", 128, 128, 15346, 1038, 6243, 19),
        ("stouttp", 128, 128, 15296, 1088, 8245, 14),
        ("twilite", 128, 128, 8384, 8000, 455, 21),
    ];

    /// <summary>The zone names the census covers, as xUnit member data.</summary>
    public static TheoryData<string> ZoneNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var row in Census)
            {
                data.Add(row.Zone);
            }

            return data;
        }
    }

    /// <summary>The six per-zone extensions that use the size-prefixed zlib envelope.</summary>
    private static readonly string[] CompressedExtensions = [".zmp", ".zcp", ".zsk", ".ztx", ".zlu", ".zfg"];

    private static string RequireRoot()
    {
        var root = RealAssetPaths.Travels.ShadowkeyRoot();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("the Shadowkey application directory"));
        return root!;
    }

    private static byte[] Inflate(string root, string zone, string extension)
    {
        var path = Path.Combine(root, zone + extension);
        return ShadowkeyCompressedFile.Inflate(File.ReadAllBytes(path), Path.GetFileName(path));
    }

    private static (int Width, int Height, int Open, int Blocked, int Prototypes, int Textures) CensusFor(string zone)
    {
        foreach (var row in Census)
        {
            if (string.Equals(row.Zone, zone, StringComparison.Ordinal))
            {
                return (row.Width, row.Height, row.Open, row.Blocked, row.Prototypes, row.Textures);
            }
        }

        throw new InvalidOperationException($"'{zone}' is not in the census.");
    }

    [Fact]
    public void TheInstallHoldsExactlyTheTwentyOneCensusZones()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RequireRoot();

        var stems = Directory.GetFiles(root, "*.zmp")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(stem => stem, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(
            Census.Select(row => row.Zone).OrderBy(zone => zone, StringComparer.OrdinalIgnoreCase).ToArray(),
            stems);
    }

    [Theory]
    [MemberData(nameof(ZoneNames))]
    public void EveryCompressedFamilyInflatesToItsDeclaredLength(string zone)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RequireRoot();

        foreach (var extension in CompressedExtensions)
        {
            var path = Path.Combine(root, zone + extension);
            Assert.True(File.Exists(path), $"{zone}{extension} is missing from the install.");

            var file = File.ReadAllBytes(path);
            Assert.True(ShadowkeyCompressedFile.LooksLike(file), $"{zone}{extension} does not open a zlib stream.");

            // Inflate itself throws unless the payload length equals the declared one.
            var payload = ShadowkeyCompressedFile.Inflate(file, zone + extension);
            Assert.NotEmpty(payload);
        }
    }

    [Theory]
    [MemberData(nameof(ZoneNames))]
    public void EveryZoneMapTilesAndCarriesItsOwnNameAndCensus(string zone)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RequireRoot();
        var expected = CensusFor(zone);

        var payload = Inflate(root, zone, ".zmp");
        var map = ShadowkeyZoneMap.Parse(payload, zone + ".zmp");

        Assert.Equal(expected.Width, map.Width);
        Assert.Equal(expected.Height, map.Height);
        Assert.Equal(
            ShadowkeyZoneMap.HeaderLength + (map.Width * map.Height * ShadowkeyZoneMap.CellLength),
            payload.Length);

        // The embedded name is the file stem, case aside (the install mixes Crypt3 and crypt1).
        Assert.Equal(zone, map.ZoneName, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(expected.Blocked, map.BlockedCellCount);
        Assert.Equal(expected.Open, map.Cells.Count - map.BlockedCellCount);
        Assert.True(map.RawLowBitsClear, $"{zone}.zmp has a cell with the low 6 bits of +2 set.");
    }

    [Fact]
    public void TwentyZonesAre128SquareAndOnlyFfarenaIs64()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RequireRoot();

        var sizes = new List<(string Zone, int Width, int Height, int Length)>();
        foreach (var row in Census)
        {
            var payload = Inflate(root, row.Zone, ".zmp");
            var map = ShadowkeyZoneMap.Parse(payload, row.Zone + ".zmp");
            sizes.Add((row.Zone, map.Width, map.Height, payload.Length));
        }

        Assert.Equal(20, sizes.Count(s => s.Width == 128 && s.Height == 128 && s.Length == 98436));
        var arena = Assert.Single(sizes.Where(s => s.Width == 64));
        Assert.Equal("ffarena", arena.Zone);
        Assert.Equal(64, arena.Height);
        Assert.Equal(24708, arena.Length);
    }

    [Theory]
    [MemberData(nameof(ZoneNames))]
    public void EveryPrototypeTableTilesAndMatchesItsGridExactly(string zone)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RequireRoot();
        var expected = CensusFor(zone);

        var payload = Inflate(root, zone, ".zcp");
        var table = ShadowkeyCellPrototypes.Parse(payload, zone + ".zcp");
        var map = ShadowkeyZoneMap.Parse(Inflate(root, zone, ".zmp"), zone + ".zmp");

        Assert.Equal(expected.Prototypes, table.Count);
        Assert.Equal(
            ShadowkeyCellPrototypes.HeaderLength + (table.Count * ShadowkeyCellPrototype.RecordLength),
            payload.Length);

        // No cell indexes past the table, and no record is slack: max index + 1 == count.
        table.ValidateAgainst(map, zone + ".zcp");
        Assert.Equal(table.Count - 1, map.MaxPrototypeIndex);
        Assert.Equal(0, table.CountUnreferencedBy(map));
    }

    [Theory]
    [MemberData(nameof(ZoneNames))]
    public void EveryBlockedCellReferencesASolidPrototype(string zone)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RequireRoot();

        var map = ShadowkeyZoneMap.Parse(Inflate(root, zone, ".zmp"), zone + ".zmp");
        var table = ShadowkeyCellPrototypes.Parse(Inflate(root, zone, ".zcp"), zone + ".zcp");

        var blockedButOpenGeometry = 0;
        foreach (var cell in map.Cells)
        {
            if (cell.IsBlocked && !table.Records[cell.PrototypeIndex].IsSolid)
            {
                blockedButOpenGeometry++;
            }
        }

        Assert.Equal(0, blockedButOpenGeometry);
    }

    [Theory]
    [MemberData(nameof(ZoneNames))]
    public void EveryTextureBankTilesAtOnePlusCountTimes16384(string zone)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RequireRoot();
        var expected = CensusFor(zone);

        var payload = Inflate(root, zone, ".ztx");
        var bank = ShadowkeyTextureBank.Parse(payload, zone + ".ztx");

        Assert.Equal(expected.Textures, bank.Count);
        Assert.Equal(
            ShadowkeyTextureBank.HeaderLength + (bank.Count * ShadowkeyTextureBank.TextureLength),
            payload.Length);
        Assert.All(bank.Textures, texture =>
        {
            Assert.Equal(128, texture.Width);
            Assert.Equal(128, texture.Height);
        });
    }

    [Fact]
    public void TheInstallHolds326TexturesAcrossThe21Banks()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RequireRoot();

        var total = 0;
        foreach (var row in Census)
        {
            total += ShadowkeyTextureBank.Parse(Inflate(root, row.Zone, ".ztx"), row.Zone + ".ztx").Count;
        }

        Assert.Equal(326, total);
    }

    [Theory]
    [MemberData(nameof(ZoneNames))]
    public void EveryTextureDecodesThroughAn8BitZonePalette(string zone)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RequireRoot();

        var paletteBytes = File.ReadAllBytes(Path.Combine(root, zone + ".pal"));
        Assert.Equal(ShadowkeyZonePalette.FileLength, paletteBytes.Length);

        // 8-bit components, never 6-bit VGA: promoting them would scramble every texture.
        Assert.Equal(255, paletteBytes.Max());

        var palette = ShadowkeyZonePalette.Parse(paletteBytes, zone + ".pal");
        var bank = ShadowkeyTextureBank.Parse(Inflate(root, zone, ".ztx"), zone + ".ztx");
        var texture = bank.Decode(0, palette);

        Assert.Equal(ShadowkeyTextureBank.TextureWidth, texture.Width);
        Assert.Equal(ShadowkeyTextureBank.TextureHeight, texture.Height);
        var (r, g, b, _) = palette.GetEntry(bank.Textures[0].Indices[0]);
        Assert.Equal(r, texture.Pixels[0]);
        Assert.Equal(g, texture.Pixels[1]);
        Assert.Equal(b, texture.Pixels[2]);
    }

    [Fact]
    public void ThirteenZonesCarryTheMagentaColourKeyAtIndexSix()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RequireRoot();

        var keyed = new List<string>();
        foreach (var row in Census)
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, row.Zone + ".pal"));
            var key = ShadowkeyZonePalette.FindColourKeyIndex(bytes);
            if (key.HasValue)
            {
                Assert.Equal(6, key.Value);
                keyed.Add(row.Zone);
            }
        }

        Assert.Equal(13, keyed.Count);
    }

    [Theory]
    [MemberData(nameof(ZoneNames))]
    public void EverySkyboxTilesWithA512x256ImageLocatedFromTheEnd(string zone)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RequireRoot();

        var payload = Inflate(root, zone, ".zsk");
        var sky = ShadowkeySkybox.Parse(payload, zone + ".zsk");

        Assert.Equal(ShadowkeySkybox.TextureWidth, sky.Texture.Width);
        Assert.Equal(ShadowkeySkybox.TextureHeight, sky.Texture.Height);
        Assert.Equal(1, sky.Footer[0]);
        Assert.Equal(0, sky.Footer[1]);
        Assert.Equal(1, sky.Footer[2]);
        Assert.Contains(sky.Footer[3], new ushort[] { 1, 10 });

        if (sky.IsOutdoorClass)
        {
            Assert.Equal(30, sky.Vertices.Count);
            Assert.Equal(168, sky.Corners.Count);
            Assert.Equal(56, sky.Faces.Count);
            Assert.Equal(132624, payload.Length);
        }
        else
        {
            Assert.Equal(98, sky.Vertices.Count);
            Assert.Equal(145, sky.Corners.Count);
            Assert.Equal(192, sky.Faces.Count);
            Assert.Equal(134570, payload.Length);
        }
    }

    [Fact]
    public void NineInteriorZonesShareTheBlankBoxSkyAndTwelveOutdoorOnesTheDome()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RequireRoot();

        var interior = new List<string>();
        var painted = new List<string>();
        foreach (var row in Census)
        {
            var sky = ShadowkeySkybox.Parse(Inflate(root, row.Zone, ".zsk"), row.Zone + ".zsk");
            if (!sky.IsOutdoorClass)
            {
                interior.Add(row.Zone);
                Assert.False(sky.HasPaintedTexture, $"{row.Zone}.zsk is an interior box with a painted image.");
            }
            else if (sky.HasPaintedTexture)
            {
                painted.Add(row.Zone);
            }
        }

        Assert.Equal(9, interior.Count);
        Assert.Equal(12, Census.Length - interior.Count);
        Assert.Equal(
            new[] { "GhstPass", "GlacierCrawl", "azra", "drgnfld", "dstar_e", "dstar_w", "snowline", "stouttp" },
            painted.OrderBy(zone => zone, StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [MemberData(nameof(ZoneNames))]
    public void EveryLightTableIs131072BytesAndIsThePaletteRamp(string zone)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RequireRoot();

        var payload = Inflate(root, zone, ".zlu");
        Assert.Equal(ShadowkeyLightTable.PayloadLength, payload.Length);

        var table = ShadowkeyLightTable.Parse(payload, zone + ".zlu");
        var palette = ShadowkeyZonePalette.Parse(
            File.ReadAllBytes(Path.Combine(root, zone + ".pal")), zone + ".pal");

        Assert.True(table.MatchesPalette(palette), $"{zone}.zlu is not the light ramp of {zone}.pal.");
    }

    [Theory]
    [MemberData(nameof(ZoneNames))]
    public void EveryFogTableIs131072BytesAndIsTheBlendOfOneFogColour(string zone)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RequireRoot();

        var payload = Inflate(root, zone, ".zfg");
        Assert.Equal(ShadowkeyFogTable.PayloadLength, payload.Length);

        var table = ShadowkeyFogTable.Parse(payload, zone + ".zfg");

        Assert.True(table.IsLevelZeroIdentity, $"{zone}.zfg level 0 is not the identity ramp.");
        Assert.True(table.MatchesBlendRecipe, $"{zone}.zfg is not a blend towards one fog colour.");
    }

    [Fact]
    public void FiveDistinctFogColoursServeThe21Zones()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RequireRoot();

        var colours = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var row in Census)
        {
            var table = ShadowkeyFogTable.Parse(Inflate(root, row.Zone, ".zfg"), row.Zone + ".zfg");
            var key = string.Create(
                CultureInfo.InvariantCulture,
                $"{table.FogRed},{table.FogGreen},{table.FogBlue}");
            if (!colours.TryGetValue(key, out var zones))
            {
                zones = [];
                colours[key] = zones;
            }

            zones.Add(row.Zone);
        }

        Assert.Equal(5, colours.Count);
        Assert.Equal(14, colours["0,0,0"].Count);
        Assert.Equal(new[] { "GhstPass", "GlacierCrawl" }, colours["10,10,11"].OrderBy(z => z, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "drgnfld", "dstar_e", "dstar_w" }, colours["10,10,12"].OrderBy(z => z, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "snowline" }, colours["9,9,11"]);
        Assert.Equal(new[] { "stouttp" }, colours["8,8,9"]);
    }

    [Fact]
    public void TheInstallHolds331776CellsAnd66829Prototypes()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RequireRoot();

        var cells = 0;
        var blocked = 0;
        var prototypes = 0;
        foreach (var row in Census)
        {
            var map = ShadowkeyZoneMap.Parse(Inflate(root, row.Zone, ".zmp"), row.Zone + ".zmp");
            cells += map.Cells.Count;
            blocked += map.BlockedCellCount;
            prototypes += ShadowkeyCellPrototypes.Parse(Inflate(root, row.Zone, ".zcp"), row.Zone + ".zcp").Count;
        }

        Assert.Equal(331776, cells);
        Assert.Equal(83775, blocked);
        Assert.Equal(66829, prototypes);
    }
}
