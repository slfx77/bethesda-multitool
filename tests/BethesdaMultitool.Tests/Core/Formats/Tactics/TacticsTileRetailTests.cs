using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Tactics;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Tactics;

/// <summary>
///     Opt-in (<c>RUN_BUCKET_B=1</c>) exact-tiling check of every <c>.til</c> in the retail Fallout
///     Tactics install.
///     <para>
///         Every number pinned here was measured 2026-09-07 by an INDEPENDENT Python transcription
///         of the same decompiled functions (scratchpad <c>close/tactics-art/measure_til.py</c> and
///         <c>probe_enum.py</c>) over the 29,957 files extracted from <c>tiles_0.bos</c> — not by
///         the reader under test. The test can fail: a wrong flag-word shape for any of the five
///         versions, a missing 8-byte record after an image, or a missed trailing palette all break
///         "consume the file exactly" for thousands of files at once.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class TacticsTileRetailTests
{
    private const string TileArchive = "tiles_0.bos";

    /// <summary>
    ///     The tile naming convention is <c>Set_Type_Material_Name_...</c>, e.g.
    ///     <c>BOS_Floor_Metal_PipeGrateCentre_F_1_NE.til</c>. It is authored text that owes nothing
    ///     to the binary layout, which makes it a control INDEPENDENT of the decompiled editor the
    ///     enums were read off — and it is drawn from the affected population, since every tile has
    ///     one. The words each enum value is allowed to pair with are listed here; a mapping that is
    ///     off by one, or numbered differently, cannot score highly against them.
    /// </summary>
    private static readonly Dictionary<int, string[]> TypeWords = new()
    {
        [0] = ["wall", "cap"],
        [1] = ["floor"],
        [2] = ["object"],
        [3] = ["stair", "step"],
        [4] = ["roof"]
    };

    private static readonly Dictionary<int, string[]> MaterialWords = new()
    {
        [0] = ["stone"],
        [1] = ["gravel"],
        [2] = ["metal"],
        [3] = ["wood"],
        [4] = ["water"]
    };

    private static string RequireArchive()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var core = RealAssetPaths.Classics.FalloutTactics();
        Assert.SkipWhen(core is null, RealAssetPaths.SkipMessage("Fallout Tactics"));
        var path = Path.Combine(core, TileArchive);
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage(TileArchive));
        return path;
    }

    /// <summary>The name's type word and material word, or null when it does not carry three parts.</summary>
    private static (string Type, string Material)? NameWords(string fileName)
    {
        var parts = Path.GetFileNameWithoutExtension(fileName).Split('_');
        return parts.Length >= 3
            ? (parts[1].ToLowerInvariant(), parts[2].ToLowerInvariant())
            : null;
    }

    [Fact]
    public void Every29957TileTilesExactly_WithTheMeasuredHeaderCensus()
    {
        var path = RequireArchive();
        using var archive = ArchiveReader.Open(path);

        var files = 0;
        var images = 0;
        var declaredSizeMatchesFirstImage = 0;
        var everyImageCarriesItsOwnPalette = 0;
        var paletteEqualsShared = 0;
        var versions = new Dictionary<int, int>();
        var flagBits = new int[16];
        var flaggedTilesByVersion = new Dictionary<int, int>();
        var bit1ByVersion = new Dictionary<int, int>();
        var legacyAIs100 = 0;
        var legacyBIsZeroOnV6 = 0;
        var typeInRange = 0;
        var materialInRange = 0;
        var failures = new List<string>();

        foreach (var entry in archive.ListFiles())
        {
            if (!entry.Name.EndsWith(".til", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bytes = archive.ReadFile(entry.FullPath);
            Assert.NotNull(bytes);
            files++;
            try
            {
                // Parse REQUIRES the walk to land on the last byte; CountRuns re-walks every
                // embedded pixel block under the row rule, so both halves have to tile.
                var tile = TacticsTileFile.Parse(bytes, entry.FullPath);
                var header = tile.Header;
                versions[header.Version] = versions.GetValueOrDefault(header.Version) + 1;
                for (var bit = 0; bit < 16; bit++)
                {
                    if (((int)header.Flags & (1 << bit)) != 0)
                    {
                        flagBits[bit]++;
                    }
                }

                if (header.LegacyByteA == 100)
                {
                    legacyAIs100++;
                }

                if (header.Version == 6 && header.LegacyByteB == 0)
                {
                    legacyBIsZeroOnV6++;
                }

                if (header.Flags != TacticsTileFlags.None)
                {
                    flaggedTilesByVersion[header.Version] =
                        flaggedTilesByVersion.GetValueOrDefault(header.Version) + 1;
                }

                if (((int)header.Flags & 2) != 0)
                {
                    bit1ByVersion[header.Version] = bit1ByVersion.GetValueOrDefault(header.Version) + 1;
                }

                if ((int)header.Type <= 4)
                {
                    typeInRange++;
                }

                if ((int)header.Material <= 5)
                {
                    materialInRange++;
                }

                if (tile.Images.Count > 0
                    && header.ImageWidth == tile.Images[0].Image.Width
                    && header.ImageHeight == tile.Images[0].Image.Height)
                {
                    declaredSizeMatchesFirstImage++;
                }

                var allOwnPalettes = tile.Images.Count > 0;
                foreach (var image in tile.Images)
                {
                    images++;
                    if (!image.Image.HasPalette)
                    {
                        allOwnPalettes = false;
                        continue;
                    }

                    if (image.Image.PaletteBgrx.Span.SequenceEqual(tile.SharedPaletteBgrx.Span))
                    {
                        paletteEqualsShared++;
                    }

                    var census = image.Image.CountRuns();
                    Assert.True(census.Total > 0, $"{entry.FullPath}: empty run census");
                }

                if (allOwnPalettes)
                {
                    everyImageCarriesItsOwnPalette++;
                }
            }
            catch (InvalidDataException e)
            {
                failures.Add($"{entry.FullPath}: {e.Message}");
            }
        }

        Assert.Empty(failures.Take(10));
        Assert.Equal(29957, files);
        Assert.Equal(31127, images);
        Assert.Equal(31127, paletteEqualsShared);
        Assert.Equal(29957, everyImageCarriesItsOwnPalette);
        Assert.Equal(29922, declaredSizeMatchesFirstImage);

        Assert.Equal(
            new Dictionary<int, int> { [6] = 1485, [7] = 4479, [8] = 1850, [9] = 11250, [10] = 10893 },
            versions);

        // ⚑ The falsifiable half of the flag mapping: exactly nine bits are ever set and they are
        // the editor's eight plus bit 1. A contiguous 0..7 reading predicts traffic on bit 6 and
        // none on 10/11; the retail data says the opposite on both.
        Assert.Equal(1433, flagBits[0]);
        Assert.Equal(154, flagBits[1]);
        Assert.Equal(784, flagBits[2]);
        Assert.Equal(2362, flagBits[3]);
        Assert.Equal(26, flagBits[4]);
        Assert.Equal(7, flagBits[5]);
        Assert.Equal(0, flagBits[6]);
        Assert.Equal(1, flagBits[7]);
        Assert.Equal(0, flagBits[8]);
        Assert.Equal(0, flagBits[9]);
        Assert.Equal(120, flagBits[10]);
        Assert.Equal(2154, flagBits[11]);
        for (var bit = 12; bit < 16; bit++)
        {
            Assert.Equal(0, flagBits[bit]);
        }

        // ⚠ How far that census reaches. Every set bit in the corpus is on a v9 or a v10 tile —
        // v6, v7 and v8 set NOTHING — so the refutation of the contiguous 0..7 reading rests on
        // v10 alone, and the sub-v8 path that rebuilds bits 0 and 1 out of legacy bytes is never
        // exercised by a non-zero value. Bit 1 in particular is a v9/v10 flag, not a legacy
        // reconstruction: 103 + 51, and none below v9.
        Assert.Equal(new Dictionary<int, int> { [9] = 215, [10] = 4470 }, flaggedTilesByVersion);
        Assert.Equal(new Dictionary<int, int> { [9] = 103, [10] = 51 }, bit1ByVersion);

        // The discarded bytes themselves: 100 on every v6/v7/v8 tile, and v6's second one always 0.
        Assert.Equal(1485 + 4479 + 1850, legacyAIs100);
        Assert.Equal(1485, legacyBIsZeroOnV6);

        // ⚠ Not all-or-nothing: the type/material enums hold on every version 8-10 tile and the
        // stragglers are all v6/v7 authoring values the game itself tolerates.
        Assert.Equal(29575, typeInRange);
        Assert.Equal(29743, materialInRange);
    }

    /// <summary>The framing length — '&lt;' name '&gt;' NUL version NUL — read here, not by the reader under test.</summary>
    private static int FramingLength(byte[] bytes)
    {
        var close = Array.IndexOf(bytes, (byte)'>');
        Assert.True(close > 0);
        var end = close + 2;
        while (bytes[end] != 0)
        {
            end++;
        }

        return end + 1;
    }

    /// <summary>
    ///     Scores the WRONG reading — "the flag word is always a u16" — against the corpus, because
    ///     the honest cost of that mistake is two different numbers and it used to be reported as
    ///     one (a single "19,064 desynchronise", corrected 2026-09-07).
    ///     <para>
    ///         A u16 ends the header on the wrong byte for v9, v7 and v6 (11,250 + 4,479 + 1,485 =
    ///         17,214 files that fail to tile), but v8's real shape — a throwaway byte then a whole
    ///         u8 — spans exactly the same two bytes, so those 1,850 files still tile and are merely
    ///         MIS-VALUED, reading 0x0064 = 100 where the flags are 0. v10 is the one version the
    ///         wrong rule gets right outright.
    ///     </para>
    ///     <para>
    ///         ⚑ This is the point: an exact-tiling check cannot see the v8 error at all. The test
    ///         can fail — if the reader ever gave v8 the v7/v6 two-bit shape its record length would
    ///         change and <c>silentlyMisvalued</c> would drop to 0 while <c>desynchronised</c> rose
    ///         to 19,064, which is precisely the claim being corrected.
    ///     </para>
    /// </summary>
    [Fact]
    public void AU16FlagWordDesynchronises17214FilesAndSilentlyMisvalues1850More()
    {
        var path = RequireArchive();
        using var archive = ArchiveReader.Open(path);

        const int BodyBeforeFlags = 3 + 4 * 4 + 2; // box[3], foot x/y + image w/h, type, material
        var desynchronised = 0;
        var silentlyMisvalued = 0;
        var correctOutright = 0;
        var misvaluedAs100 = 0;
        var misvaluedVersions = new Dictionary<int, int>();

        foreach (var entry in archive.ListFiles())
        {
            if (!entry.Name.EndsWith(".til", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bytes = archive.ReadFile(entry.FullPath);
            Assert.NotNull(bytes);
            var header = TacticsTileFile.Parse(bytes!, entry.FullPath).Header;

            var flagsAt = FramingLength(bytes) + BodyBeforeFlags;
            var wrongRecordLength = flagsAt + 2;
            var wrongFlags = bytes[flagsAt] | (bytes[flagsAt + 1] << 8);

            if (wrongRecordLength != header.RecordLength)
            {
                desynchronised++;
            }
            else if (wrongFlags != (int)header.Flags)
            {
                silentlyMisvalued++;
                misvaluedVersions[header.Version] = misvaluedVersions.GetValueOrDefault(header.Version) + 1;
                if (wrongFlags == 100)
                {
                    misvaluedAs100++;
                }
            }
            else
            {
                correctOutright++;
            }
        }

        Assert.Equal(17214, desynchronised);
        Assert.Equal(1850, silentlyMisvalued);
        Assert.Equal(10893, correctOutright);

        // Every silently mis-valued file is a v8 tile, and every one of them reads the discarded
        // byte 100 as its flags.
        Assert.Equal(new Dictionary<int, int> { [8] = 1850 }, misvaluedVersions);
        Assert.Equal(1850, misvaluedAs100);
    }

    /// <summary>
    ///     The independent control on the two enums: do the values agree with the words the tile
    ///     ARTISTS put in the file names? Nothing about the naming convention is derivable from the
    ///     binary, so this can fail — and it would, loudly, if the type and material bytes were
    ///     swapped, off by one, or numbered in any other order.
    ///     <para>
    ///         ⚠ The one value the names do NOT confirm is material 5: all 808 of its tiles are
    ///         named "Gravel". Their PATHS are what carry it — see the second half of this test.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheTypeAndMaterialEnumsAgreeWithTheNamesTheArtistsUsed()
    {
        var path = RequireArchive();
        using var archive = ArchiveReader.Open(path);

        var typeAgreements = 0;
        var typeJudged = 0;
        var materialAgreements = 0;
        var materialJudged = 0;
        var materialFive = 0;
        var materialFiveOnASnowPath = 0;

        foreach (var entry in archive.ListFiles())
        {
            if (!entry.Name.EndsWith(".til", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bytes = archive.ReadFile(entry.FullPath);
            Assert.NotNull(bytes);
            var header = TacticsTileFile.Parse(bytes!, entry.FullPath).Header;
            if (NameWords(entry.Name) is not { } words)
            {
                continue;
            }

            if (TypeWords.TryGetValue((int)header.Type, out var typeWords))
            {
                typeJudged++;
                if (Array.IndexOf(typeWords, words.Type) >= 0)
                {
                    typeAgreements++;
                }
            }

            if (MaterialWords.TryGetValue((int)header.Material, out var materialWords))
            {
                materialJudged++;
                if (Array.IndexOf(materialWords, words.Material) >= 0)
                {
                    materialAgreements++;
                }
            }

            if (header.Material == TacticsTileMaterial.Snow)
            {
                materialFive++;
                if (entry.FullPath.Contains("snow", StringComparison.OrdinalIgnoreCase))
                {
                    materialFiveOnASnowPath++;
                }
            }
        }

        Assert.Equal(29572, typeJudged);
        Assert.Equal(28606, typeAgreements);
        Assert.Equal(28932, materialJudged);
        Assert.Equal(28311, materialAgreements);
        Assert.Equal(808, materialFive);

        // 96.7% and 97.9%. A shuffled or off-by-one numbering could not reach either.
        Assert.True(typeAgreements > typeJudged * 0.96, $"{typeAgreements} of {typeJudged}");
        Assert.True(materialAgreements > materialJudged * 0.97, $"{materialAgreements} of {materialJudged}");

        // Material 5 is the one the file names cannot vouch for — all 808 are named "Gravel". The
        // directory tree settles it instead, and it does so completely: Mountain FLOORS\Slopes\Snow
        // (480), SnowCaps (104), RockWALLS\CapsSNOW (100), SnowDrifts (62), ExternalToSnow (32),
        // SnowPiles (28), Mountain FLOORS\Snow (2). Not one material-5 tile lies off a Snow path.
        Assert.Equal(808, materialFiveOnASnowPath);
    }

    /// <summary>
    ///     A named tile, pinned by its own bytes rather than by a census: the reader must place the
    ///     footprint, foot position and declared size where a hex dump of the file shows them.
    /// </summary>
    [Fact]
    public void ANamedTileReadsTheValuesItsBytesCarry()
    {
        var path = RequireArchive();
        using var archive = ArchiveReader.Open(path);

        var entry = archive.ListFiles()
            .FirstOrDefault(f =>
                f.Name.Equals("BOS_Floor_Metal_PipeGrateCentre_F_1_NE.til", StringComparison.OrdinalIgnoreCase));
        Assert.SkipWhen(entry is null, "BOS_Floor_Metal_PipeGrateCentre_F_1_NE.til is not in tiles_0.bos");

        var bytes = archive.ReadFile(entry.FullPath);
        Assert.NotNull(bytes);
        var tile = TacticsTileFile.Parse(bytes!, entry.FullPath);

        // A floor tile: the bounding box is flat in y, which is the branch FUN_006f02d0 answers 2 to.
        Assert.Equal(TacticsTileType.Floor, tile.Header.Type);
        Assert.Equal(1, tile.Header.BoundingBoxY);
        Assert.Equal(2, tile.Header.BoundingBoxClass);
        Assert.Single(tile.Images);
        Assert.Equal(tile.Header.ImageWidth, tile.Images[0].Image.Width);
        Assert.Equal(tile.Header.ImageHeight, tile.Images[0].Image.Height);

        var texture = tile.Images[0].Image.Decode();
        Assert.Equal(tile.Header.ImageWidth * tile.Header.ImageHeight * 4, texture.Pixels.Length);
    }
}