using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BethesdaMultitool.Core.Formats.Archives;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Fallout;
using BethesdaMultitool.Core.Formats.Interplay;
using BethesdaMultitool.Core.Imaging;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) of the Fallout 1 and 2 Steam installs. Both installs
///     are MODDED (the Hi-Res patch; Fallout 2 also sfall and Killap's patch), so only the
///     original archives are pinned by count — the patch DATs are asserted structurally.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class FalloutRetailTests
{
    private static string Require(Func<string?> resolve, string label)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = resolve();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage(label));
        return root;
    }

    [Fact]
    public void EveryGamFileDeclaresVariablesWhoseCommentIndexMatchesItsPosition()
    {
        // ⚑ THE ORACLE IS IN THE DATA: scripts address a global by DECLARATION ORDER, and the
        // authors wrote that index into each trailing comment as "// (n)". A parse that drops or
        // invents a declaration shows up as an off-by-one from that point on — which is exactly
        // how the missing-semicolon trap below was found.
        var root = Require(RealAssetPaths.Classics.Fallout1, "Fallout");

        var master = Dat1Archive.Parse(Path.Combine(root, "MASTER.DAT"));
        using var backend = new Dat1Backend(master);
        var gam = backend.ListFiles()
            .Where(e => e.FullPath.EndsWith(".GAM", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.SkipWhen(gam.Count == 0, RealAssetPaths.SkipMessage("Fallout .GAM files"));

        var declarations = 0;
        var commented = 0;
        var matching = 0;
        var missingSemicolon = 0;
        var mapScoped = 0;

        foreach (var entry in gam)
        {
            var text = Encoding.Latin1.GetString(backend.Extract(entry));
            var vars = FalloutGameVariables.Parse(text, entry.FullPath);
            declarations += vars.Variables.Count;
            if (vars.Scope == FalloutVariableScope.Map)
            {
                mapScoped++;
            }

            foreach (var v in vars.Variables)
            {
                var m = Regex.Match(v.Comment, @"^\s*\((\d+)\)");
                if (!m.Success)
                {
                    continue;
                }

                commented++;
                if (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) == v.Index)
                {
                    matching++;
                }
            }

            foreach (var line in text.Split('\n'))
            {
                var code = line.Split("//")[0];
                if (code.Contains(":=", StringComparison.Ordinal) && !code.TrimEnd().EndsWith(';'))
                {
                    missingSemicolon++;
                }
            }
        }

        // ⚠ The install is MODDED, so these assert the INVARIANT rather than exact counts.
        Assert.True(declarations > 500, $"expected hundreds of declarations, saw {declarations}");
        Assert.True(commented > 300, $"expected most to carry an index comment, saw {commented}");

        // ⚑ The gate: every commented index equals its declaration position.
        Assert.Equal(commented, matching);

        // ⚠⚠ Retail declarations DO omit the terminating semicolon. Requiring it drops them and
        // shifts every later index — which the equality above would then fail on.
        Assert.True(missingSemicolon > 0, "expected retail declarations missing their semicolon");
        Assert.True(mapScoped > 20, $"expected most .GAM files to be map-scoped, saw {mapScoped}");
    }

    [Fact]
    public void Fallout1ArchivesTileAndDecompress()
    {
        var root = Require(RealAssetPaths.Classics.Fallout1, "Fallout");

        var master = Dat1Archive.Parse(Path.Combine(root, "MASTER.DAT"));
        Assert.Equal(65, master.Directories.Count);
        Assert.Equal(19784, master.Entries.Count);
        Assert.Equal(14997, master.Entries.Count(e => e.IsCompressed));

        var critter = Dat1Archive.Parse(Path.Combine(root, "CRITTER.DAT"));
        Assert.Equal(["ART\\CRITTERS"], critter.Directories);
        Assert.Equal(5459, critter.Entries.Count);
        Assert.All(critter.Entries, e => Assert.True(e.IsCompressed));

        // One compressed and one stored entry inflate to exactly their declared sizes.
        using var backend = new Dat1Backend(master);
        var listed = backend.ListFiles();
        var compressed = listed.First(e => e.Compressed);
        var stored = listed.First(e => !e.Compressed);
        Assert.Equal(compressed.Size, backend.Extract(compressed).Length);
        Assert.Equal(stored.Size, backend.Extract(stored).Length);

        // color.pal is the game's 256-colour palette, stored at the root.
        var palette = listed.Single(e => e.FullPath.Equals("COLOR.PAL", StringComparison.OrdinalIgnoreCase));
        Assert.True(backend.Extract(palette).Length >= 768);
    }

    [Fact]
    public void EveryFrmSpriteWalksAndItsFramesAreExact()
    {
        var root = Require(RealAssetPaths.Classics.Fallout1, "Fallout");

        var master = Dat1Archive.Parse(Path.Combine(root, "MASTER.DAT"));
        using var backend = new Dat1Backend(master);
        var entries = backend.ListFiles()
            .Where(e => FalloutFrmFile.IsFrmFileName(e.Name))
            .OrderBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Equal(4928, entries.Count);

        var frames = 0;
        var failures = new List<string>();
        var versions = new HashSet<uint>();
        var byVersion = new Dictionary<uint, List<string>>();
        foreach (var entry in entries)
        {
            try
            {
                // Parsing IS the proof: every frame's declared size must equal width x height and
                // each direction's run must stay inside the file.
                var frm = FalloutFrmFile.Parse(backend.Extract(entry), entry.Name);
                versions.Add(frm.Version);
                byVersion.TryAdd(frm.Version, []);
                byVersion[frm.Version].Add(entry.FullPath);
                frames += frm.DistinctFrames.Count();
            }
            catch (InvalidDataException e)
            {
                failures.Add($"{entry.FullPath}: {e.Message}");
            }
        }

        Assert.Empty(failures);
        Assert.True(frames >= entries.Count, $"{frames} frames across {entries.Count} sprites");

        // The version word is 4 on all but ONE retail sprite: the throwing-knife inventory icon
        // declares 3. Confirmed by an independent walk of MASTER.DAT 2026-09-05 - and its frames
        // tile exactly like every other file, so version 3 is a stray word, not a second layout.
        // Pinned by name so a future reader that starts dispatching on version has to face it.
        Assert.Equal<uint>([3, FalloutFrmFile.RetailVersion], versions.Order());
        var olderThanRetail = byVersion[3];
        Assert.Equal(["ART/INVEN/OKNIFE.FRM"], olderThanRetail);
    }

    [Fact]
    public void EveryPrototypeParsesAndIsExactlyTheSizeItsTypeAndSubtypeDemand()
    {
        var root = Require(RealAssetPaths.Classics.Fallout1, "Fallout");

        var master = Dat1Archive.Parse(Path.Combine(root, "MASTER.DAT"));
        using var backend = new Dat1Backend(master);
        var entries = backend.ListFiles()
            .Where(e => e.Name.EndsWith(".PRO", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(4306, entries.Count);

        var failures = new List<string>();
        var byType = new Dictionary<FalloutProType, int>();
        var typedFid = 0;
        var scripted = 0;
        foreach (var entry in entries)
        {
            if (!FalloutProFile.TryParse(backend.Extract(entry), entry.Name, out var pro, out var error))
            {
                failures.Add($"{entry.FullPath}: {error}");
                continue;
            }

            byType[pro.Type] = byType.GetValueOrDefault(pro.Type) + 1;

            // ⚑ The oracle that settled the header: a prototype's ART reference carries the SAME
            // type byte as the prototype itself. A little-endian read moves that byte to the far
            // end and the agreement disappears, so this is what pins the endianness.
            if (pro.FrameId >> 24 == (uint)pro.Type)
            {
                typedFid++;
            }

            if (pro.HasScript)
            {
                scripted++;
            }

            // The directory the file came from must agree with the type byte, on every one.
            Assert.Contains(FalloutProList.DirectoryFor(pro.Type), entry.FullPath, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(failures);
        Assert.Equal(4306, typedFid);
        Assert.Equal(new Dictionary<FalloutProType, int>
        {
            [FalloutProType.Item] = 242,
            [FalloutProType.Critter] = 312,
            [FalloutProType.Scenery] = 908,
            [FalloutProType.Wall] = 1176,
            [FalloutProType.Tile] = 1622,
            [FalloutProType.Misc] = 46
        }, byType);

        // Only 31 prototypes name a script; the other 2,607 that carry the field hold -1, and the
        // 1,668 tiles and misc records have no script field at all.
        Assert.Equal(31, scripted);
    }

    [Fact]
    public async Task AnalyzerSynthesizesAPrototypeRecordPerProtoWithItsNameResolved()
    {
        var root = Require(RealAssetPaths.Classics.Fallout1, "Fallout");

        var result = await ClassicGameAnalyzer.LoadAsync(root, TestContext.Current.CancellationToken);
        var protos = result.Records.GenericRecords
            .Where(r => r.RecordType == FalloutRecordSource.PrototypeRecordType)
            .ToList();

        Assert.Equal(4306, protos.Count);

        // Identity needs no hashing here — the PID's family byte and 24-bit index are already
        // unique — so uniqueness is a property of the scheme, and this is what proves it holds.
        Assert.Equal(protos.Count, protos.Select(r => r.FormId).Distinct().Count());

        // The name comes from the family's PRO_*.MSG, and it has to read as prose.
        var dweller = protos.First(r =>
            r.FormId == ClassicFormIdScheme.Compose(FalloutRecordSource.DomainFor(FalloutProType.Critter), 1));
        Assert.Equal("Vault Dweller", dweller.FullName);
        Assert.Equal("Critter", dweller.Fields["Family"]);
        Assert.Equal(100u, dweller.Fields["TextId"]);

        // Tiles carry no script field at all — distinct from carrying one set to -1.
        var tile = protos.First(r => (string?)r.Fields["Family"] == "Tile");
        Assert.False(tile.Fields.ContainsKey("Script"));
        Assert.False(tile.Fields.ContainsKey("ExtendedFlags"));
        Assert.True(tile.Fields.ContainsKey("Material"));

        // Every family is represented, in the census the prototypes themselves give.
        var byFamily = protos.GroupBy(r => (string?)r.Fields["Family"]).ToDictionary(g => g.Key!, g => g.Count());
        Assert.Equal(new Dictionary<string, int>
        {
            ["Item"] = 242,
            ["Critter"] = 312,
            ["Scenery"] = 908,
            ["Wall"] = 1176,
            ["Tile"] = 1622,
            ["Misc"] = 46
        }, byFamily);
    }

    [Fact]
    public void FalloutTwoUsesTheSamePrototypeAndMapLayoutWithItsOwnVersionAndCritterSize()
    {
        // ⚑ The whole point of B6: Fallout 2 is a parameterisation, not a second format. The same
        // readers take it with two differences, both measured — map version 20, and a critter block
        // that grew to 416 while two of FO2's own critters stayed at Fallout 1's 412.
        var root = Require(RealAssetPaths.Classics.Fallout2, "Fallout 2");

        var master = Dat2Archive.Parse(Path.Combine(root, "master.dat"));
        using var backend = new Dat2Backend(master);
        var files = backend.ListFiles().ToList();

        var protos = files.Where(e => e.Name.EndsWith(".pro", StringComparison.OrdinalIgnoreCase)).ToList();
        var parsed = 0;
        var critterSizes = new Dictionary<int, int>();
        var failures = new List<string>();
        foreach (var entry in protos)
        {
            var bytes = backend.Extract(entry);
            if (!FalloutProFile.TryParse(bytes, entry.Name, out var pro, out var error))
            {
                failures.Add(error);
                continue;
            }

            parsed++;
            if (pro.Type == FalloutProType.Critter)
            {
                critterSizes[bytes.Length] = critterSizes.GetValueOrDefault(bytes.Length) + 1;
            }
        }

        Assert.Empty(failures);
        Assert.Equal(7650, parsed);

        // 481 at Fallout 2's 416 and 2 still at Fallout 1's 412 — which is why the size is a set.
        Assert.Equal(new Dictionary<int, int> { [416] = 481, [412] = 2 }, critterSizes);

        var maps = files.Where(e => e.Name.EndsWith(".map", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(155, maps.Count);

        var byFlags = new Dictionary<uint, int>();
        var elevationZeroAbsent = new List<int>();
        foreach (var entry in maps)
        {
            var map = FalloutMapFile.Parse(backend.Extract(entry), entry.Name);
            if (map.ElevationFlags == 0x2)
            {
                elevationZeroAbsent = [.. map.Elevations.Select(e => e.Index)];
            }

            Assert.Equal(FalloutMapFile.Version20, map.Version);
            Assert.Equal(entry.Name, map.MapName, StringComparer.OrdinalIgnoreCase);
            byFlags[map.ElevationFlags] = byFlags.GetValueOrDefault(map.ElevationFlags) + 1;

            // Elevations are numbered by their flag bit, so they must be strictly ascending and in
            // range whichever bits are clear.
            Assert.Equal(map.Elevations.Select(e => e.Index).Order(), map.Elevations.Select(e => e.Index));
            Assert.All(map.Elevations, e => Assert.InRange(e.Index, 0, FalloutMapFile.MaxElevations - 1));
        }

        // ⚑ Flags 0x2 appears ONLY in Fallout 2 (2 maps): elevation 0 absent, so the first stored
        // grid is elevation 1. Fallout 1 never exercises that path.
        Assert.Equal(new Dictionary<uint, int> { [0x0] = 20, [0x2] = 2, [0x8] = 24, [0xC] = 109 }, byFlags);
        Assert.Equal([1, 2], elevationZeroAbsent);
    }

    [Fact]
    public void EveryMapDeclaresVersionNineteenAndItsGridsIndexTheTileList()
    {
        var root = Require(RealAssetPaths.Classics.Fallout1, "Fallout");

        var master = Dat1Archive.Parse(Path.Combine(root, "MASTER.DAT"));
        using var backend = new Dat1Backend(master);
        var maps = backend.ListFiles()
            .Where(e => e.Name.EndsWith(".MAP", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Equal(72, maps.Count);

        var tileList = FalloutProList.Parse(
            backend.Extract(backend.ListFiles().First(e =>
                e.FullPath.Replace('\\', '/').Equals(FalloutProList.PathFor(FalloutProType.Tile),
                    StringComparison.OrdinalIgnoreCase))),
            "TILES.LST");
        Assert.Equal(1622, tileList.Count);

        var byElevationCount = new Dictionary<int, int>();
        var outOfRange = new List<string>();
        var failures = new List<string>();
        foreach (var entry in maps)
        {
            if (!FalloutMapFile.TryParse(backend.Extract(entry), entry.Name, out var map, out var error))
            {
                failures.Add(error);
                continue;
            }

            // The header's own name equals the file's on every retail map — prose that has to read
            // back correctly, which is what makes the 236-byte header offsets trustworthy.
            Assert.Equal(entry.Name, map.MapName, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(FalloutMapFile.Version19, map.Version);
            byElevationCount[map.Elevations.Count] = byElevationCount.GetValueOrDefault(map.Elevations.Count) + 1;

            var strays = map.Elevations
                .SelectMany(e => e.Tiles)
                .Count(t => t.Roof > tileList.Count || t.Floor > tileList.Count);
            if (strays > 0)
            {
                outOfRange.Add($"{entry.Name}:{strays}");
            }
        }

        Assert.Empty(failures);
        Assert.Equal(new Dictionary<int, int> { [1] = 41, [2] = 17, [3] = 14 }, byElevationCount);

        // ⚑ 71 of 72 maps have every tile inside TILES.LST. The one that does not is a DATA anomaly,
        // not a decode failure — pinned by name and count so a future change that starts mangling
        // grids shows up as this list growing rather than as silently different terrain.
        // ⚠ An earlier note here claimed HUBDWNTN had a stray too; that was an artefact of reading
        // the grid at the wrong offset 56, where header bytes land inside the first tiles.
        Assert.Equal(["LAGUNRUN.MAP:257"], outOfRange.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryPrototypeIdResolvesThroughItsListRatherThanItsFileName()
    {
        // ⚠ The rule that looks right and is not: `PROTO\<TYPE>\<pid>.PRO`. It fails for 1,151 of
        // the 4,306 retail prototypes — 886 scenery alone — while the .LST line number resolves
        // every single one. A map reader that guessed the file name would silently load the wrong
        // prototype for a quarter of the game's objects.
        var root = Require(RealAssetPaths.Classics.Fallout1, "Fallout");

        var master = Dat1Archive.Parse(Path.Combine(root, "MASTER.DAT"));
        using var backend = new Dat1Backend(master);
        var files = backend.ListFiles()
            .ToDictionary(e => e.FullPath.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);

        var resolved = 0;
        var nameWouldHaveWorked = 0;
        foreach (var type in Enum.GetValues<FalloutProType>())
        {
            var listPath = FalloutProList.PathFor(type);
            Assert.True(files.ContainsKey(listPath), $"{listPath} is missing from MASTER.DAT");
            var list = FalloutProList.Parse(backend.Extract(files[listPath]), listPath);

            var directory = FalloutProList.DirectoryFor(type);
            foreach (var entry in files.Values.Where(e =>
                         e.Name.EndsWith(".PRO", StringComparison.OrdinalIgnoreCase) &&
                         e.FullPath.Replace('\\', '/')
                             .StartsWith($"PROTO/{directory}/", StringComparison.OrdinalIgnoreCase)))
            {
                var pro = FalloutProFile.Parse(backend.Extract(entry), entry.Name);
                var named = list.Resolve(pro.ProtoId);
                Assert.Equal(entry.Name, named, StringComparer.OrdinalIgnoreCase);
                resolved++;

                if (string.Equals(pro.ListIndex.ToString("D8", CultureInfo.InvariantCulture) + ".PRO",
                        entry.Name, StringComparison.OrdinalIgnoreCase))
                {
                    nameWouldHaveWorked++;
                }
            }
        }

        Assert.Equal(4306, resolved);
        Assert.Equal(3155, nameWouldHaveWorked);
    }

    [Fact]
    public void EverySplashPlateIsSixFortyByFourEightyLittleEndian()
    {
        var root = Require(RealAssetPaths.Classics.Fallout1, "Fallout");

        var master = Dat1Archive.Parse(Path.Combine(root, "MASTER.DAT"));
        using var backend = new Dat1Backend(master);
        var plates = backend.ListFiles()
            .Where(e => e.Name.EndsWith(".RIX", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(plates);

        foreach (var entry in plates)
        {
            var bytes = backend.Extract(entry);

            // ⚠⚠ RIX is the ONE little-endian format in Fallout's line-up. Read big-endian these
            // same bytes give 32770x57345, so the dimensions are the check.
            var image = FalloutRixImage.Parse(bytes, entry.Name);
            Assert.Equal((640, 480), (image.Bitmap.Width, image.Bitmap.Height));
            Assert.Equal(307_978, bytes.Length);

            // 6-bit VGA like every other Fallout palette; unpromoted it renders 4x too dark.
            Assert.All(bytes.AsSpan(FalloutRixImage.HeaderLength, Palette.RgbByteCount).ToArray(),
                component => Assert.InRange(component, 0, 63));
        }
    }

    [Fact]
    public void EveryMovieWalksAsChunksAndDeclaresItsScreenSize()
    {
        // ⛔ Identify-only by design: FFmpeg's MVE decoder is LGPL, so nothing here decodes video or
        // audio. What it does prove is that the container walks — all 13 retail movies tile exactly
        // from +26 to EOF as u16 length + u16 type chunks.
        var root = Require(RealAssetPaths.Classics.Fallout1, "Fallout");

        var master = Dat1Archive.Parse(Path.Combine(root, "MASTER.DAT"));
        using var backend = new Dat1Backend(master);
        var movies = backend.ListFiles()
            .Where(e => e.Name.EndsWith(".MVE", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(movies);

        var failures = new List<string>();
        var chunks = 0;
        foreach (var entry in movies)
        {
            var bytes = backend.Extract(entry);
            if (bytes.Length == 0)
            {
                continue;
            }

            if (!InterplayMveFile.TryParse(bytes, entry.Name, out var movie, out var error))
            {
                failures.Add(error);
                continue;
            }

            chunks += movie.Chunks.Count;
            Assert.Equal((640, 480), (movie.Width, movie.Height));
            Assert.True(movie.PaletteChunks > 0, $"{entry.Name} declares no palette");
        }

        Assert.Empty(failures);
        Assert.True(chunks > 1000, $"only {chunks} chunks across {movies.Count} movies");
    }

    [Fact]
    public void BothMessagePopulationsReadExactlyIncludingMultiLineDialogue()
    {
        // ⚠⚠ The game text and the dialogue are the SAME format but not the same shape: none of the
        // 13,189 game entries wraps across lines, while 3,962 of the 23,126 dialogue entries do.
        // A reader validated on the first silently mangles a sixth of the second, so both are
        // asserted here by exact count.
        var root = Require(RealAssetPaths.Classics.Fallout1, "Fallout");

        var master = Dat1Archive.Parse(Path.Combine(root, "MASTER.DAT"));
        using var backend = new Dat1Backend(master);

        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        var multiLine = 0;
        foreach (var (label, prefix) in new[] { ("game", "TEXT/ENGLISH/GAME/"), ("dialog", "TEXT/ENGLISH/DIALOG/") })
        {
            var count = 0;
            foreach (var entry in backend.ListFiles().Where(e =>
                         e.FullPath.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                         e.Name.EndsWith(".MSG", StringComparison.OrdinalIgnoreCase)))
            {
                var bytes = backend.Extract(entry);
                if (bytes.Length == 0)
                {
                    continue;
                }

                var msg = FalloutMessageFile.Parse(bytes, entry.Name);
                count += msg.Count;
                multiLine += msg.Strings.Count(s => s.Contains('\n', StringComparison.Ordinal));
            }

            totals[label] = count;
        }

        // Distinct ids AFTER the last-wins rule, so these sit below the raw entry counts (13,189
        // and 23,126) by the 21 and 18 duplicated ids respectively.
        Assert.Equal(13_168, totals["game"]);
        Assert.Equal(23_108, totals["dialog"]);

        // ⚑ The count that proves the reader is not line-oriented — a line-based parse yields ZERO.
        // ⚠ It is 3,874 rather than the 3,962 raw multi-line entries for two compounding reasons:
        // last-wins collapses duplicates, and Trim() removes newlines that sit only at the edges
        // (including ones adjacent to Unicode whitespace such as 0xA0, which is why a Python
        // str.strip() cross-check lands 10 higher).
        Assert.Equal(3_874, multiLine);

        // A concrete entry, immune to those trimming subtleties: AGATHA.MSG 102 wraps mid-sentence.
        // ⚠ The wrap is CRLF — the reader preserves the file's own bytes rather than normalising
        // line endings, which is why this expects "\r\n". A consumer that wants "\n" normalises.
        var agatha = backend.ListFiles().First(e =>
            e.FullPath.Replace('\\', '/').Equals("TEXT/ENGLISH/DIALOG/AGATHA.MSG", StringComparison.OrdinalIgnoreCase));
        var line = FalloutMessageFile.Parse(backend.Extract(agatha), "AGATHA.MSG").Find(102);
        Assert.Equal(
            "This woman is wrinkled and gray, yet has a spry step and a bright\r\n personality.",
            line);
    }

    [Fact]
    public void ColorPaletteIsSixBitWithSentinelWhites()
    {
        var root = Require(RealAssetPaths.Classics.Fallout1, "Fallout");

        var master = Dat1Archive.Parse(Path.Combine(root, "MASTER.DAT"));
        using var backend = new Dat1Backend(master);
        var entry = backend.ListFiles()
            .Single(e => e.Name.Equals(FalloutPalette.FileName, StringComparison.OrdinalIgnoreCase));
        var bytes = backend.Extract(entry);

        // 768 palette bytes plus a 32x32x32 RGB-to-index lookup cube.
        Assert.Equal(FalloutPalette.RetailFileLength, bytes.Length);

        // The 255s are SENTINELS, not 8-bit colour: index 0 (transparent) and the 27 entries of
        // the colour-cycling range. Everything else is within the 6-bit maximum, which is why a
        // range sniff would render the whole game four times too dark.
        var sentinels = Enumerable.Range(0, Palette.EntryCount)
            .Where(i => FalloutPalette.IsSentinel(bytes.AsSpan(0, Palette.RgbByteCount), i))
            .ToList();
        Assert.Equal(28, sentinels.Count);
        Assert.Equal(0, sentinels[0]);
        Assert.Equal(Enumerable.Range(FalloutPalette.CycleStart, 27), sentinels.Skip(1));

        var palette = FalloutPalette.Parse(bytes, FalloutPalette.FileName);
        Assert.Equal((byte)0, palette.GetEntry(0).A);
        Assert.Equal((byte)255, palette.GetEntry(1).A);
    }

    [Fact]
    public void FullScreenSlidesCarryTheirOwnPaletteRatherThanTheGlobalOne()
    {
        var root = Require(RealAssetPaths.Classics.Fallout1, "Fallout");

        var master = Dat1Archive.Parse(Path.Combine(root, "MASTER.DAT"));
        using var backend = new Dat1Backend(master);
        var files = backend.ListFiles();

        // Every per-image palette but one pairs with a same-stem FRM in the same directory. The
        // odd one out, ART\CUTS\SUBTITLE.PAL, overlays cutscene video and has no sprite.
        var perImage = files
            .Where(e => e.Name.EndsWith(".PAL", StringComparison.OrdinalIgnoreCase) &&
                        !e.Name.Equals(FalloutPalette.FileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(18, perImage.Count);

        var frms = files
            .Where(e => FalloutFrmFile.IsFrmFileName(e.Name))
            .Select(e => Stem(e.FullPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unpaired = perImage.Select(e => Stem(e.FullPath)).Where(s => !frms.Contains(s)).ToList();
        Assert.Equal(["ART/CUTS/SUBTITLE"], unpaired);

        // The rule the pipeline follows: the image's own palette first, the global one second.
        Assert.Equal(["DEATH.PAL", FalloutPalette.FileName], FalloutPalette.CandidatesFor("DEATH.FRM"));

        // ⚠ Reading a slide through COLOR.PAL is not a near miss, it is speckle: the sky dithers
        // across indices that DEATH.PAL makes near-identical and COLOR.PAL makes wildly different.
        var death = FalloutFrmFile.Parse(
            backend.Extract(files.Single(e =>
                e.FullPath.EndsWith("INTRFACE/DEATH.FRM", StringComparison.OrdinalIgnoreCase))),
            "DEATH.FRM");
        var own = FalloutPalette.Parse(
            backend.Extract(files.Single(e =>
                e.FullPath.EndsWith("INTRFACE/DEATH.PAL", StringComparison.OrdinalIgnoreCase))),
            "DEATH.PAL");
        var global = FalloutPalette.Parse(
            backend.Extract(
                files.Single(e => e.Name.Equals(FalloutPalette.FileName, StringComparison.OrdinalIgnoreCase))),
            FalloutPalette.FileName);

        var frame = death.DistinctFrames.First();
        Assert.Equal((640, 480), (frame.Width, frame.Height));
        Assert.True(MeanAdjacentJump(frame, own) < 25, "the slide's own palette must render smoothly");
        Assert.True(MeanAdjacentJump(frame, global) > 75, "COLOR.PAL must be visibly wrong here");
    }

    private static string Stem(string fullPath)
    {
        var slash = fullPath.LastIndexOf('/');
        var name = slash < 0 ? fullPath : fullPath[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        return (slash < 0 ? "" : fullPath[..(slash + 1)]) + (dot < 0 ? name : name[..dot]);
    }

    /// <summary>Mean max-channel difference between horizontally adjacent rendered pixels.</summary>
    private static double MeanAdjacentJump(FalloutFrmFrame frame, Palette palette)
    {
        var bitmap = frame.Bitmap;
        double total = 0;
        var count = 0;
        for (var y = 0; y < bitmap.Height; y += 3)
        {
            for (var x = 0; x < bitmap.Width - 1; x++)
            {
                var a = palette.GetEntry(bitmap.Indices[y * bitmap.Width + x]);
                var b = palette.GetEntry(bitmap.Indices[y * bitmap.Width + x + 1]);
                total += Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
                count++;
            }
        }

        return total / count;
    }

    [Fact]
    public void Fallout2ArchivesAccountForThemselvesAndInflate()
    {
        var root = Require(RealAssetPaths.Classics.Fallout2, "Fallout 2");

        var master = Dat2Archive.Parse(Path.Combine(root, "master.dat"));
        Assert.Equal(23140, master.Entries.Count);
        var critter = Dat2Archive.Parse(Path.Combine(root, "critter.dat"));
        Assert.Equal(7120, critter.Entries.Count);

        // The mod DATs are structural only: they change with the mods installed.
        foreach (var name in new[] { "patch000.dat", "f2_res.dat" })
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path))
            {
                Assert.NotEmpty(Dat2Archive.Parse(path).Entries);
            }
        }

        using var backend = new Dat2Backend(master);
        var listed = backend.ListFiles();
        var zlib = listed.First(e => e.Compressed);
        var stored = listed.First(e => !e.Compressed);
        Assert.Equal(zlib.Size, backend.Extract(zlib).Length);
        Assert.Equal(stored.Size, backend.Extract(stored).Length);

        // Paths are backslash in the tree and forward-slash in the archive view.
        Assert.All(listed, e => Assert.DoesNotContain('\\', e.FullPath));
        Assert.Contains(listed, e => e.FullPath.Equals("color.pal", StringComparison.OrdinalIgnoreCase));
    }
}
