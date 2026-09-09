using System.Text;
using BethesdaMultitool.Core.Formats.Arena;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Png;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Arena;

/// <summary>
///     Opt-in checks of <see cref="ArenaSaveGame" /> and <see cref="ArenaSaveEngine" /> against the
///     retail Arena install's SAVEGAME.00/.01/.02 and SAVEENGN.00/.01/.02 (<c>RUN_BUCKET_B=1</c>).
///     Every expectation is an external oracle or a literal measured 2026-09-06: PAL.COL shifted
///     in the test, the decompressed MIF layers, retail P1.IMG, and the six record offsets the
///     populated-record oracle returns. The saves are one playthrough's — .00/.01 in START.MIF,
///     .02 in MAGE6.MIF — so their figures are pinned, not ranged.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class ArenaSavesRetailTests
{
    private static readonly string[] SaveSlots = ["00", "01", "02"];

    private static string RequireArenaRoot()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Arena();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("The Elder Scrolls: Arena"));
        return root;
    }

    private static string RequireFile(string root, string name)
    {
        var path = Path.Combine(root, name);
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage(name));
        return path;
    }

    private static ArenaSaveGame LoadSave(string root, string slot)
    {
        var name = $"SAVEGAME.{slot}";
        return ArenaSaveGame.Parse(File.ReadAllBytes(RequireFile(root, name)), name);
    }

    /// <summary>Resolves a .MIF by name: loose beside the saves first, then GLOBAL.BSA.</summary>
    private static (byte[] Bytes, string Source) ResolveMif(string root, string mifName)
    {
        var loose = Path.Combine(root, mifName.ToUpperInvariant());
        if (File.Exists(loose))
        {
            return (File.ReadAllBytes(loose), "loose");
        }

        using var archive = ArchiveReader.Open(RequireFile(root, "GLOBAL.BSA"));
        var entry = archive.ListFiles()
            .FirstOrDefault(e => string.Equals(e.Name, mifName, StringComparison.OrdinalIgnoreCase));
        Assert.True(entry is not null, $"{mifName} is neither loose nor in GLOBAL.BSA.");
        var bytes = archive.ReadFile(entry.FullPath);
        Assert.NotNull(bytes);
        return (bytes, "GLOBAL.BSA");
    }

    [Fact]
    public void EveryRetailSave_IsClaimedByTheProbe()
    {
        var root = RequireArenaRoot();

        foreach (var slot in SaveSlots)
        {
            var bytes = File.ReadAllBytes(RequireFile(root, $"SAVEGAME.{slot}"));
            Assert.Equal(166_631, bytes.Length);
            Assert.True(ArenaSaveGame.IsSaveGame(bytes), $"SAVEGAME.{slot} was not claimed.");

            var engine = File.ReadAllBytes(RequireFile(root, $"SAVEENGN.{slot}"));
            Assert.Equal(17_983, engine.Length);
            Assert.True(ArenaSaveEngine.IsSaveEngine(engine));
        }
    }

    [Fact]
    public void Palette_EqualsPalColBodyShiftedRightByTwo_On768Of768()
    {
        var root = RequireArenaRoot();
        var palCol = File.ReadAllBytes(RequireFile(root, "PAL.COL"));
        Assert.Equal(776, palCol.Length);

        // The shift is done HERE, on bytes read from PAL.COL — never by the reader under test.
        var expected = new byte[768];
        for (var i = 0; i < 768; i++)
        {
            expected[i] = (byte)(palCol[8 + i] >> 2);
        }

        // PAL.COL itself is 8-bit: a >> 2 relation is only meaningful if the source exceeds 63.
        Assert.True(palCol.Skip(8).Count(b => b > 63) > 300, "PAL.COL should be an 8-bit palette.");

        foreach (var slot in SaveSlots)
        {
            var save = LoadSave(root, slot);
            var matches = 0;
            for (var i = 0; i < 768; i++)
            {
                if (save.Palette6Bit[i] == expected[i])
                {
                    matches++;
                }
            }

            Assert.Equal(768, matches);
        }
    }

    [Fact]
    public void LevelIdentity_NamesResolveToRealMaps()
    {
        var root = RequireArenaRoot();

        var s00 = LoadSave(root, "00");
        var s01 = LoadSave(root, "01");
        var s02 = LoadSave(root, "02");

        Assert.Equal("start level 1", s00.LevelName);
        Assert.Equal("start.inf", s00.InfName);
        Assert.Equal("start.mif", s00.MifName);
        Assert.Equal("start.mif", s01.MifName);

        // The shorter name was written over the longer one without clearing the field: the raw
        // bytes read "level1\0evel 1\0", and the reader stops at the first NUL.
        Assert.Equal("level1", s02.LevelName);
        Assert.Equal("evel 1", Encoding.ASCII.GetString(s02.State, 0x1048C - 64_768 + 7, 6));
        Assert.Equal("mage.inf", s02.InfName);
        Assert.Equal("mage6.mif", s02.MifName);

        // START.MIF ships only loose; MAGE6.MIF only inside GLOBAL.BSA.
        var (startBytes, startSource) = ResolveMif(root, s00.MifName);
        Assert.Equal("loose", startSource);
        Assert.Equal(2_328, startBytes.Length);

        var (mageBytes, mageSource) = ResolveMif(root, s02.MifName);
        Assert.Equal("GLOBAL.BSA", mageSource);
        Assert.Equal(804, mageBytes.Length);

        using var archive = ArchiveReader.Open(RequireFile(root, "GLOBAL.BSA"));
        Assert.DoesNotContain(archive.ListFiles(),
            e => string.Equals(e.Name, "START.MIF", StringComparison.OrdinalIgnoreCase));

        // The 61-byte MHDR copy is verbatim: it equals the resolved file's payload at [6, 67).
        Assert.Equal(startBytes.AsSpan(6, 61).ToArray(), s00.State.AsSpan(0x1053A - 64_768, 61).ToArray());
        Assert.Equal(mageBytes.AsSpan(6, 61).ToArray(), s02.State.AsSpan(0x1053A - 64_768, 61).ToArray());
    }

    [Theory]
    [InlineData("00", 50, 50, 1)]
    [InlineData("01", 50, 50, 1)]
    [InlineData("02", 25, 25, 2)]
    public void LiveExtent_MeasuredFromTheMap1Sentinel_EqualsTheMhdrCopy(
        string slot, int width, int depth, int levelCount)
    {
        var root = RequireArenaRoot();
        var save = LoadSave(root, slot);

        Assert.Equal(width, save.MapHeader.Width);
        Assert.Equal(depth, save.MapHeader.Depth);
        Assert.Equal(levelCount, save.MapHeader.DeclaredLevelCount);

        // Independent measurement: the extent of cells that are not the 0xE000 sentinel.
        var maxColumn = -1;
        var maxRow = -1;
        var live = 0;
        for (var row = 0; row < 128; row++)
        {
            for (var column = 0; column < 128; column++)
            {
                if (save.Map1At(column, row) == 0xE000)
                {
                    continue;
                }

                live++;
                maxColumn = Math.Max(maxColumn, column);
                maxRow = Math.Max(maxRow, row);
            }
        }

        Assert.Equal(width, maxColumn + 1);
        Assert.Equal(depth, maxRow + 1);
        Assert.Equal(width * depth, live);
        Assert.Equal(width * depth, save.GetLiveMap1().Length);
        Assert.Equal(width * depth, save.GetLiveFloor().Length);
    }

    [Fact]
    public void Save02Planes_EqualMage6Level0_CellForCell()
    {
        var root = RequireArenaRoot();
        var save = LoadSave(root, "02");
        var (mifBytes, _) = ResolveMif(root, save.MifName);
        var mif = ArenaMifFile.Parse(mifBytes, save.MifName);

        Assert.Equal(25, mif.Width);
        Assert.Equal(25, mif.Depth);
        var level = mif.Levels[0];

        Assert.Equal(625, CountEqual(save.GetLiveMap1(), level.Map1));
        Assert.Equal(625, CountEqual(save.GetLiveFloor(), level.Floor));
    }

    [Fact]
    public void Save00Planes_EqualStartLevel0_ExceptFlaggedRuntimeCells()
    {
        var root = RequireArenaRoot();
        var save = LoadSave(root, "00");
        var (mifBytes, _) = ResolveMif(root, save.MifName);
        var mif = ArenaMifFile.Parse(mifBytes, save.MifName);

        Assert.Equal(50, mif.Width);
        Assert.Equal(50, mif.Depth);
        var level = mif.Levels[0];

        var liveMap1 = save.GetLiveMap1();
        var liveFloor = save.GetLiveFloor();

        Assert.True(CountEqual(liveFloor, level.Floor) >= 2_499,
            "FLOR should match START.MIF on at least 2,499/2,500.");

        // MAP1 differs only on 0x8000-flag cells (the MAP1 high bit): every delta carries the
        // flag on at least one side. Measured 2026-09-07 on SAVEGAME.00 vs START.MIF level 0
        // with an independent Python LZHUF decoder: 41 cells gained bit 0x0200 with the low byte
        // preserved (29 x 0x802F -> 0x822F, 12 x 0x8030 -> 0x8230), 31 flagged cells were
        // cleared to 0x0000 (18 x 0x8030, 8 x 0x802F, 3 x 0x800A, 2 x 0x8008) and 9 flagged
        // cells appeared where the MIF has 0x0000 (7 x 0x8230, 2 x 0x822F). NOT every delta
        // preserves the low byte and NOT every MIF-side value is flagged — an earlier reading
        // asserted both and failed on cell 302 (0x0000 -> 0x822F).
        var map1Matches = CountEqual(liveMap1, level.Map1);
        Assert.Equal(2_419, map1Matches);

        var flaggedOnEitherSide = 0;
        var gainedBit9WithLowByteKept = 0;
        var clearedToZero = 0;
        var appearedFromZero = 0;
        for (var i = 0; i < liveMap1.Length; i++)
        {
            var authored = level.Map1[i];
            var live = liveMap1[i];
            if (live == authored)
            {
                continue;
            }

            if ((authored & 0x8000) != 0 || (live & 0x8000) != 0)
            {
                flaggedOnEitherSide++;
            }

            if (authored != 0 && live != 0 && (authored ^ live) == 0x0200)
            {
                gainedBit9WithLowByteKept++;
            }
            else if (live == 0 && (authored & 0x8000) != 0)
            {
                clearedToZero++;
            }
            else if (authored == 0 && (live & 0x8000) != 0)
            {
                appearedFromZero++;
            }
        }

        Assert.Equal(81, flaggedOnEitherSide);
        Assert.Equal(41, gainedBit9WithLowByteKept);
        Assert.Equal(31, clearedToZero);
        Assert.Equal(9, appearedFromZero);

        // Transposing the plane must NOT match: that is what fixes the row/column orientation.
        var transposed = new ushort[2_500];
        for (var z = 0; z < 50; z++)
        {
            for (var x = 0; x < 50; x++)
            {
                transposed[x + z * 50] = liveMap1[z + x * 50];
            }
        }

        Assert.True(CountEqual(transposed, level.Map1) < 2_000, "the transposed plane should not fit.");
    }

    [Fact]
    public void Screenshot_RendersToPng_AndItsBottomRowsAreRetailP1Img()
    {
        var root = RequireArenaRoot();
        var save = LoadSave(root, "00");

        var png = save.RenderScreenshotPng();
        Assert.True(PngImageDecoder.HasPngSignature(png));
        var info = PngImageDecoder.ReadInfo(png);
        Assert.NotNull(info);
        Assert.Equal(320, info.Value.Width);
        Assert.Equal(200, info.Value.Height);

        var directory = Path.Combine(Path.GetTempPath(), "arena-save-screenshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "SAVEGAME.00.png");
            save.SaveScreenshotPng(path);
            Assert.True(new FileInfo(path).Length > 1_000);
        }
        finally
        {
            Directory.Delete(directory, true);
        }

        // P1.IMG (headerless 320x53) is the HUD strip; it sits at rows 147..199 of the screen.
        using var archive = ArchiveReader.Open(RequireFile(root, "GLOBAL.BSA"));
        var p1 = archive.ReadFile(archive.ListFiles()
            .Single(e => string.Equals(e.Name, "P1.IMG", StringComparison.OrdinalIgnoreCase)).FullPath);
        Assert.NotNull(p1);
        Assert.Equal(16_960, p1.Length);

        var hud = ArenaImgDecoder.Decode(p1, "P1.IMG").Image;
        Assert.Equal(320, hud.Width);
        Assert.Equal(53, hud.Height);

        var equal = 0;
        var screen = save.Screenshot.Indices.AsSpan(147 * 320, 53 * 320);
        for (var i = 0; i < hud.Indices.Length; i++)
        {
            if (screen[i] == hud.Indices[i])
            {
                equal++;
            }
        }

        // Measured 94.298% in all three saves; the differences are the live health/stamina bars
        // and the portrait. A random alignment scores about 8%.
        Assert.True(equal >= 0.94 * hud.Indices.Length, $"P1.IMG matched only {equal}/{hud.Indices.Length}.");
    }

    [Fact]
    public void SaveEngine_OracleFindsExactlySixRecords_AtTheMeasuredOffsets()
    {
        var root = RequireArenaRoot();
        int[] expected = [3_664, 4_718, 5_772, 6_826, 7_880, 8_934];

        foreach (var slot in SaveSlots)
        {
            var bytes = File.ReadAllBytes(RequireFile(root, $"SAVEENGN.{slot}"));

            // The whole-file scan: six hits and nothing else, including inside the dense head.
            Assert.Equal(expected, ArenaSaveEngine.FindOracleHits(bytes));

            var engine = ArenaSaveEngine.Parse(bytes, $"SAVEENGN.{slot}");
            Assert.Equal(expected, engine.PopulatedRecordOffsets);
            Assert.Equal(13, engine.Records.Count);
            Assert.All(engine.Tail, b => Assert.Equal(0, b));
        }
    }

    private static int CountEqual(ushort[] a, ushort[] b)
    {
        Assert.Equal(a.Length, b.Length);
        var equal = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i])
            {
                equal++;
            }
        }

        return equal;
    }
}