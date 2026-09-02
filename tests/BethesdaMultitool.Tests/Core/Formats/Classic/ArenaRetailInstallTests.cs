using System.Linq;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.FileFormat;
using BethesdaMultitool.Core.Formats.Arena;
using BethesdaMultitool.Core.Formats.Audio;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Xngine.Flic;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Imaging;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks against a retail TES Arena install (<c>RUN_BUCKET_B=1</c>). The synthetic
///     suites pin the grammars; these pin the facts that only the shipped data can establish —
///     that GLOBAL.BSA tiles exactly, that its .INF entries really are enciphered while the loose
///     ones are not, and that the whole install resolves into records end to end.
///     <para>
///         Structural assertions only where content could vary, but the Arena install is not
///         moddable in the way the Fallout ones are, so the counts here (2,441 archive entries,
///         93 .INF files) are pinned deliberately: a change in them means the data changed, which
///         is exactly what this suite should catch.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class ArenaRetailInstallTests
{
    private const int RetailArchiveEntryCount = 2441;
    private const int RetailInfFileCount = 93;

    private static string RequireArenaRoot()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Arena();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("The Elder Scrolls: Arena"));
        return root!;
    }

    [Fact]
    public void InstallRoot_IsRecognizedAsArena()
    {
        var root = RequireArenaRoot();

        var profile = ClassicGameLocator.DetectFromDirectory(root);

        Assert.NotNull(profile);
        Assert.Equal(BethesdaGame.Arena, profile.Game);
        Assert.Equal(AnalysisFileType.ClassicGameData, FileTypeDetector.Detect(root));
    }

    [Fact]
    public void GlobalBsa_OpensAndTilesExactly()
    {
        var root = RequireArenaRoot();
        var archivePath = Path.Combine(root, "GLOBAL.BSA");
        Assert.SkipWhen(!File.Exists(archivePath), RealAssetPaths.SkipMessage("GLOBAL.BSA"));

        using var archive = ArchiveReader.Open(archivePath);
        var entries = archive.ListFiles();

        // The format has no magic at all — it is claimed only when the trailing directory's sizes
        // tile the file byte-for-byte, so merely opening it is the arithmetic proof.
        Assert.Equal(RetailArchiveEntryCount, archive.TotalFiles);
        Assert.Equal(RetailArchiveEntryCount, entries.Count);
        Assert.All(entries, e => Assert.False(string.IsNullOrEmpty(e.Name)));
    }

    [Fact]
    public void ArchivedInfFiles_AreEncrypted_AndLooseOnesAreNot()
    {
        var root = RequireArenaRoot();
        var archivePath = Path.Combine(root, "GLOBAL.BSA");
        Assert.SkipWhen(!File.Exists(archivePath), RealAssetPaths.SkipMessage("GLOBAL.BSA"));

        using var archive = ArchiveReader.Open(archivePath);
        var archivedInf = archive.ListFiles()
            .Where(e => e.Name.EndsWith(".INF", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(RetailInfFileCount, archivedInf.Count);

        foreach (var entry in archivedInf)
        {
            var raw = archive.ReadFile(entry.FullPath);
            Assert.NotNull(raw);

            // Residency decides: everything inside the archive is enciphered...
            Assert.True(ArenaInfFile.IsProbablyEncrypted(raw), $"{entry.Name} should be encrypted in the BSA.");

            // ...and decrypting must yield parsable text every time.
            var inf = ArenaInfFile.Parse(raw, entry.Name, encrypted: true);
            Assert.NotEmpty(inf.Walls);
        }

        foreach (var loosePath in Directory.EnumerateFiles(root, "*.INF"))
        {
            var bytes = File.ReadAllBytes(loosePath);
            Assert.False(
                ArenaInfFile.IsProbablyEncrypted(bytes),
                $"{Path.GetFileName(loosePath)} is loose and must be plaintext.");
        }
    }

    [Fact]
    public void LooseInfFiles_OverrideTheirArchivedNamesakes_AndSomeDiffer()
    {
        var root = RequireArenaRoot();
        var archivePath = Path.Combine(root, "GLOBAL.BSA");
        Assert.SkipWhen(!File.Exists(archivePath), RealAssetPaths.SkipMessage("GLOBAL.BSA"));

        var loose = Directory.EnumerateFiles(root, "*.INF").ToList();
        Assert.SkipWhen(loose.Count == 0, "This install has no loose .INF files.");

        using var archive = ArchiveReader.Open(archivePath);
        var byName = archive.ListFiles().ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

        var differing = 0;
        foreach (var loosePath in loose)
        {
            var name = Path.GetFileName(loosePath);
            Assert.True(byName.ContainsKey(name), $"{name} is loose but absent from GLOBAL.BSA.");

            var archived = ArenaInfFile.Decrypt(archive.ReadFile(byName[name].FullPath)!);
            if (!File.ReadAllBytes(loosePath).SequenceEqual(archived))
            {
                differing++;
            }
        }

        // Loose-over-archive precedence is a real content decision here, not a formality: on the
        // retail install three of the five loose files differ from their archived versions.
        Assert.True(differing > 0, "Loose .INF files were byte-identical to the archived ones.");

        // And the enumerator must hand back the loose copy, not the archived one.
        var resolved = ArenaRecordSource.EnumerateInfFiles(root)
            .ToDictionary(x => x.Name, x => x.PlainBytes, StringComparer.OrdinalIgnoreCase);
        foreach (var loosePath in loose)
        {
            Assert.Equal(File.ReadAllBytes(loosePath), resolved[Path.GetFileName(loosePath).ToUpperInvariant()]);
        }
    }

    [Fact]
    public async Task Install_LoadsIntoRecords_WithBothSynthesizedTypes()
    {
        var root = RequireArenaRoot();

        using var result = await ClassicGameAnalyzer.LoadAsync(root, TestContext.Current.CancellationToken);

        Assert.Equal(AnalysisFileType.ClassicGameData, result.FileType);

        var byType = result.Records.GenericRecords
            .GroupBy(r => r.RecordType, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        Assert.Equal(RetailInfFileCount, byType[ArenaRecordSource.InfRecordType]);
        Assert.True(byType[ArenaRecordSource.TemplateRecordType] > 500,
            $"Expected TEMPLATE.DAT to yield hundreds of strings, got {byType[ArenaRecordSource.TemplateRecordType]}.");

        // Synthetic ids must be unique across the whole install — that is what makes them usable
        // as FormIDs downstream.
        var ids = result.Records.GenericRecords.Select(r => r.FormId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void EveryArchivedMap_ParsesAndDecompressesItsVoxelLayers()
    {
        var root = RequireArenaRoot();
        var archivePath = Path.Combine(root, "GLOBAL.BSA");
        Assert.SkipWhen(!File.Exists(archivePath), RealAssetPaths.SkipMessage("GLOBAL.BSA"));

        using var archive = ArchiveReader.Open(archivePath);
        var maps = archive.ListFiles()
            .Where(e => e.Name.EndsWith(".MIF", StringComparison.OrdinalIgnoreCase) ||
                        e.Name.EndsWith(".RMD", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 533 .MIF + 70 .RMD in the retail archive.
        Assert.True(maps.Count > 500, $"Expected hundreds of maps in GLOBAL.BSA, found {maps.Count}.");

        var levelsSeen = 0;
        var layersDecoded = 0;
        foreach (var entry in maps)
        {
            var bytes = archive.ReadFile(entry.FullPath);
            Assert.NotNull(bytes);

            if (entry.Name.EndsWith(".RMD", StringComparison.OrdinalIgnoreCase))
            {
                var chunk = ArenaRmdFile.Parse(bytes, entry.Name);
                Assert.Equal(ArenaRmdFile.VoxelsPerLayer, chunk.Floor.Length);
                layersDecoded += 3;
                continue;
            }

            var map = ArenaMifFile.Parse(bytes, entry.Name);
            Assert.True(map.Width > 0 && map.Depth > 0, $"{entry.Name} has empty dimensions.");
            Assert.NotEmpty(map.Levels);

            foreach (var level in map.Levels)
            {
                levelsSeen++;
                foreach (var layer in new[] { level.Floor, level.Map1, level.Map2 })
                {
                    if (layer.Length == 0)
                    {
                        continue;
                    }

                    // A decoded layer is exactly the map's voxel count — that is the arithmetic
                    // proof that the LZHUF stream and the declared sizes agree.
                    Assert.Equal(map.Width * map.Depth, layer.Length);
                    layersDecoded++;
                }
            }
        }

        Assert.True(levelsSeen > 500, $"Expected hundreds of levels across the archive, saw {levelsSeen}.");
        Assert.True(layersDecoded > 500, $"Expected hundreds of decoded voxel layers, got {layersDecoded}.");
    }

    [Fact]
    public void LooseMaps_ParseAndNameTheInfFilesTheyDependOn()
    {
        var root = RequireArenaRoot();
        var looseMaps = Directory.EnumerateFiles(root, "*.MIF").ToList();
        Assert.SkipWhen(looseMaps.Count == 0, "This install has no loose .MIF files.");

        var infNames = ArenaRecordSource.EnumerateInfFiles(root)
            .Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var resolved = 0;
        foreach (var path in looseMaps)
        {
            var map = ArenaMifFile.Parse(File.ReadAllBytes(path), Path.GetFileName(path));
            Assert.NotEmpty(map.Levels);

            foreach (var level in map.Levels.Where(l => !string.IsNullOrEmpty(l.InfoFile)))
            {
                // Every INFO chunk should name a real .INF in the install; that cross-check ties
                // the map layer to the record layer.
                Assert.Contains(level.InfoFile!.ToUpperInvariant(), infNames);
                resolved++;
            }
        }

        Assert.True(resolved > 0, "No loose map named an .INF file.");
    }

    [Fact]
    public void EveryArchivedCfaAnimation_Decodes()
    {
        var root = RequireArenaRoot();
        var archivePath = Path.Combine(root, "GLOBAL.BSA");
        Assert.SkipWhen(!File.Exists(archivePath), RealAssetPaths.SkipMessage("GLOBAL.BSA"));

        using var archive = ArchiveReader.Open(archivePath);
        var animations = archive.ListFiles()
            .Where(e => e.Name.EndsWith(".CFA", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 340 .CFA files ship in the retail archive.
        Assert.True(animations.Count > 300, $"Expected hundreds of .CFA files, found {animations.Count}.");

        var depths = new HashSet<int>();
        var totalFrames = 0;
        foreach (var entry in animations)
        {
            var bytes = archive.ReadFile(entry.FullPath);
            Assert.NotNull(bytes);

            depths.Add(bytes[10]);
            var frames = ArenaCfaDecoder.Decode(bytes, entry.Name);
            Assert.NotEmpty(frames);
            totalFrames += frames.Count;

            // Every frame of an animation shares one geometry, and the pixel buffer must be
            // exactly that geometry — the arithmetic check on the RLE + bit-unpacking chain.
            var first = frames[0];
            Assert.All(frames, f =>
            {
                Assert.Equal(first.Width, f.Width);
                Assert.Equal(first.Height, f.Height);
                Assert.Equal(f.Width * f.Height, f.Indices.Length);
            });
        }

        Assert.True(totalFrames > 1000, $"Expected thousands of frames, decoded {totalFrames}.");

        // The format's whole point is variable bit depth; a corpus that only exercised one depth
        // would leave most of the unpacker untested.
        Assert.True(depths.Count > 1, $"Expected several bit depths across the corpus, saw: {string.Join(", ", depths.Order())}.");
        Assert.All(depths, d => Assert.InRange(d, 1, 8));
    }

    [Fact]
    public void EveryArchivedVoc_DecodesToPlayablePcm()
    {
        var root = RequireArenaRoot();
        var archivePath = Path.Combine(root, "GLOBAL.BSA");
        Assert.SkipWhen(!File.Exists(archivePath), RealAssetPaths.SkipMessage("GLOBAL.BSA"));

        using var archive = ArchiveReader.Open(archivePath);
        var sounds = archive.ListFiles()
            .Where(e => e.Name.EndsWith(".VOC", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 76 sound effects ship in the retail archive.
        Assert.True(sounds.Count > 50, $"Expected dozens of .VOC files, found {sounds.Count}.");

        var rates = new HashSet<int>();
        foreach (var entry in sounds)
        {
            var bytes = archive.ReadFile(entry.FullPath);
            Assert.NotNull(bytes);

            var voc = VocFile.Parse(bytes, entry.Name);
            rates.Add(voc.SampleRate);

            Assert.Equal(8, voc.BitsPerSample);
            Assert.Equal(1, voc.Channels);
            Assert.NotEmpty(voc.Samples);

            // The rate must be a value the time-constant formula can actually produce.
            Assert.InRange(voc.SampleRate, 3906, 100000);

            // And the samples must survive the round trip into a WAVE container unchanged.
            var wav = WavWriter.BuildPcm(voc.Samples, voc.SampleRate, voc.BitsPerSample, voc.Channels);
            Assert.Equal(WavWriter.HeaderLength + voc.Samples.Length, wav.Length);
            Assert.Equal(voc.Samples, wav[WavWriter.HeaderLength..]);
        }

        // Arena's effects are authored at many rates; a single-rate result would mean the time
        // constant was being ignored.
        Assert.True(rates.Count > 5, $"Expected many distinct sample rates, saw {rates.Count}.");
    }

    [Fact]
    public void EveryFlicAnimation_DecodesToExactlyItsDeclaredFrameCount()
    {
        var root = RequireArenaRoot();
        var animations = Directory.EnumerateFiles(root, "*.FLC")
            .Concat(Directory.EnumerateFiles(root, "*.CEL"))
            .ToList();
        Assert.SkipWhen(animations.Count == 0, RealAssetPaths.SkipMessage("Arena FLIC animations"));

        // 17 .FLC + 3 .CEL ship loose in the retail install.
        Assert.True(animations.Count >= 20, $"Expected 20 animations, found {animations.Count}.");

        var totalFrames = 0;
        foreach (var path in animations)
        {
            var name = Path.GetFileName(path);
            var flic = FlicFile.Parse(File.ReadAllBytes(path), name);

            // The invariant that pins the whole frame walk: a FLIC stores exactly one more frame
            // block than its header declares (the loop-back frame), so once that is dropped the
            // decoded count must equal the declared count — including files where several blocks
            // carry only a palette and hold the previous picture.
            Assert.Equal(flic.DeclaredFrameCount, flic.Frames.Count);

            Assert.True(flic.Width > 0 && flic.Height > 0, $"{name} has empty geometry.");
            Assert.All(flic.Frames, f =>
            {
                Assert.Equal(flic.Width, f.Image.Width);
                Assert.Equal(flic.Height, f.Image.Height);
                Assert.Equal(flic.Width * flic.Height, f.Image.Indices.Length);
            });

            totalFrames += flic.Frames.Count;
        }

        Assert.True(totalFrames > 900, $"Expected around a thousand frames overall, decoded {totalFrames}.");
    }

    [Fact]
    public void PackedExecutable_UnpacksToItsDeclaredSizeAndRealGameText()
    {
        var root = RequireArenaRoot();
        var exePath = Path.Combine(root, "A.EXE");
        Assert.SkipWhen(!File.Exists(exePath), RealAssetPaths.SkipMessage("Arena A.EXE"));

        var packed = File.ReadAllBytes(exePath);
        Assert.True(ArenaExeUnpacker.LooksPacked(packed), "A.EXE should be PKLITE-packed.");

        var declared = ArenaExeUnpacker.ReadDeclaredSize(packed);
        var unpacked = ArenaExeUnpacker.Unpack(packed, "A.EXE");

        // The stream must terminate exactly when the declared output is full — that equality is
        // the arithmetic proof that the whole bit stream was interpreted correctly.
        Assert.Equal(declared, unpacked.Length);
        Assert.True(unpacked.Length > packed.Length, "Unpacking should expand the executable.");

        // Size alone could be satisfied by garbage, so check that the recovered image really is
        // Arena: the game keeps its province names, race names and message templates in here, and
        // these are the tables the executable is worth unpacking for.
        var text = System.Text.Encoding.Latin1.GetString(unpacked);
        foreach (var probe in new[]
                 {
                     "Daggerfall", "Sentinel", "Wayrest", "Hammerfell", "High Rock",
                     "Skyrim", "Morrowind", "Elsweyr", "Black Marsh",
                     "Argonian", "Khajiit", "Breton", "Imperial"
                 })
        {
            Assert.Contains(probe, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ColPalettes_AreFullRangeEightBit_NotSixBitVga()
    {
        var root = RequireArenaRoot();
        var palettes = Directory.EnumerateFiles(root, "*.COL").ToList();
        Assert.SkipWhen(palettes.Count == 0, RealAssetPaths.SkipMessage("Arena .COL palettes"));

        var sawAboveSixBitCeiling = false;
        foreach (var path in palettes)
        {
            var bytes = File.ReadAllBytes(path);
            var palette = Palette.LoadArenaCol(bytes);

            // The loader must reproduce the file's bytes verbatim: any 6-bit promotion would
            // change them, which is what turned creature sprites into rainbow speckle.
            for (var i = 0; i < Palette.EntryCount; i++)
            {
                var (r, g, b, _) = palette.GetEntry(i);
                Assert.Equal(bytes[8 + (i * 3)], r);
                Assert.Equal(bytes[8 + (i * 3) + 1], g);
                Assert.Equal(bytes[8 + (i * 3) + 2], b);

                if (r > 63 || g > 63 || b > 63)
                {
                    sawAboveSixBitCeiling = true;
                }
            }
        }

        Assert.True(sawAboveSixBitCeiling, "No component exceeded 63, so these could be 6-bit after all.");
    }

    [Fact]
    public void LightTables_AreThirteenPalettesAndNormalStartsAtIdentity()
    {
        var root = RequireArenaRoot();
        var normal = Path.Combine(root, "NORMAL.LGT");
        var fog = Path.Combine(root, "FOG.LGT");
        Assert.SkipWhen(!File.Exists(normal) || !File.Exists(fog), RealAssetPaths.SkipMessage("Arena .LGT files"));

        var normalTable = ArenaLightTable.Parse(File.ReadAllBytes(normal));
        var fogTable = ArenaLightTable.Parse(File.ReadAllBytes(fog));

        // NORMAL.LGT level 0 is full light — no substitution at all. FOG.LGT already tints there,
        // which is why foggy dungeons look hazy even close up.
        Assert.True(normalTable.IsIdentity(0));
        Assert.False(fogTable.IsIdentity(0));

        // The reserved interface colours must never shade, in either file, at any level.
        for (var level = 0; level < ArenaLightTable.LevelCount; level++)
        {
            foreach (var table in new[] { normalTable, fogTable })
            {
                var row = table.Level(level);
                for (var i = 0; i < ArenaLightTable.ReservedIndexCount; i++)
                {
                    Assert.Equal(i, row[i]);
                }
            }
        }
    }

    [Fact]
    public void TemplateDat_ParsesEveryKeyAndYieldsSubstitutionTokens()
    {
        var root = RequireArenaRoot();
        var path = Path.Combine(root, "TEMPLATE.DAT");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("TEMPLATE.DAT"));

        var template = ArenaTemplateDat.Parse(File.ReadAllBytes(path));

        // Every '#NNNN' line must have produced an entry, and keys #0000-#0004 ship three copies.
        var keyLines = File.ReadAllLines(path).Count(l => l.StartsWith('#'));
        Assert.Equal(keyLines, template.Entries.Count);
        Assert.Equal(3, template.Entries.Count(e => e.Key == 0 && e.Letter == 'a'));

        // The strings carry the engine's substitution tokens; if the ampersand split were wrong the
        // values would be one giant blob instead.
        var withTokens = template.Entries.SelectMany(e => e.Values).Count(v => v.Contains("%cn", StringComparison.Ordinal));
        Assert.True(withTokens > 10, $"Expected many '%cn' substitution strings, found {withTokens}.");
    }
}
