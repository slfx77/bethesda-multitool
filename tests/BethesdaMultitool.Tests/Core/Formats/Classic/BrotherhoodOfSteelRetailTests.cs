using BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using ArchiveEntry = BethesdaMultitool.Core.Formats.Archives.ArchiveEntry;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) of the Fallout: Brotherhood of Steel disc image. The
///     image mounts through the shared <c>DiscImageBackend</c>, so these read the shipped files
///     directly out of the ISO with no extraction step.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class BrotherhoodOfSteelRetailTests
{
    private static string RequireIso()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var iso = RealAssetPaths.Consoles.BrotherhoodOfSteelIso();
        Assert.SkipWhen(iso is null, RealAssetPaths.SkipMessage("Fallout: Brotherhood of Steel disc image"));
        return iso;
    }

    [Fact]
    public void EveryStringDatabaseResolvesExactlyTheCountItsHeaderDeclares()
    {
        // ⚑ The count IS the proof of the 8-byte slot stride: on the shipped BAR.SDB it yields the
        // 85 strings the header declares, where a 16-byte stride yields only 40. The reader refuses
        // any file whose resolved count disagrees, so parsing every database is the check.
        var iso = RequireIso();
        using var disc = ArchiveReader.Open(iso);

        var databases = disc.ListFiles()
            .Where(e => e.Name.EndsWith(".SDB", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // ⚠ 56, not the 18 an earlier census claimed: that number came from `archive list`, which
        // prints only the first 100 of the disc's 352 files and says "... and 252 more". Confirmed
        // independently by extracting every *.SDB — 56 files.
        Assert.Equal(56, databases.Count);

        var failures = new List<string>();
        var strings = 0;
        var terminatedFiles = 0;
        var terminatedUnresolved = 0;
        var unterminatedFiles = 0;
        var unterminatedUnresolved = 0;
        var unterminatedWithSlack = 0;
        foreach (var entry in databases)
        {
            var bytes = disc.ReadFile(entry.FullPath);
            Assert.NotNull(bytes);

            if (!BosStringDatabase.TryParse(bytes, entry.Name, out var database, out var error))
            {
                failures.Add(error);
                continue;
            }

            strings += database.Entries.Count;
            if (database.Terminated)
            {
                terminatedFiles++;
                terminatedUnresolved += database.UnresolvedSlots;
            }
            else
            {
                unterminatedFiles++;
                unterminatedUnresolved += database.UnresolvedSlots;
                if (database.UnresolvedSlots > 0)
                {
                    unterminatedWithSlack++;
                }
            }

            // The text is UTF-16LE; read as ASCII every string would carry NULs between letters.
            Assert.All(database.Entries, e => Assert.DoesNotContain('\0', e.Value));
        }

        Assert.Empty(failures);
        Assert.True(strings > 0, "no strings were read from any database");

        // ⚠⚠ UNRESOLVED SLOTS ARE NOT ALL THE SAME THING, and a bare "expect 0" over the disc is
        // wrong: it held for a while only because this suite skipped. Measured 2026-09-09 over all
        // 56 databases (independently reproduced by a 7-Zip extraction plus a Python walk):
        // the 34 that carry the explicit terminator slot resolve EVERY populated slot, and the 22
        // that do not contribute 940 between 21 of them — a table's real length is its
        // power-of-two slot count, which an unterminated file never states, so the walk runs into
        // trailing bytes. Splitting the total is what makes this discriminate: a regression in the
        // terminator handling moves the first number off zero, which no single total could show.
        Assert.Equal(34, terminatedFiles);
        Assert.Equal(0, terminatedUnresolved);
        Assert.Equal(22, unterminatedFiles);
        Assert.Equal(940, unterminatedUnresolved);
        Assert.Equal(21, unterminatedWithSlack);
    }

    [Fact]
    public void EveryDataFileDirectoryTilesFromItsFixedHeader()
    {
        // ⚑ PARSING IS THE PROOF: 772 + 12 * count must equal the first record's offset, offsets
        // must ascend, and the third field must be zero. Measured 2026-09-06 over all 55 files and
        // 43,006 records.
        var iso = RequireIso();
        using var disc = ArchiveReader.Open(iso);

        var files = disc.ListFiles()
            .Where(e => e.Name.EndsWith(".DDF", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Equal(55, files.Count);

        var failures = new List<string>();
        var records = 0;
        var withinFileCollisions = 0;
        var distinctAcrossFiles = new HashSet<uint>();

        foreach (var entry in files)
        {
            var bytes = disc.ReadFile(entry.FullPath);
            Assert.NotNull(bytes);

            if (!BosDataFile.TryParse(bytes, entry.Name, out var file, out var error))
            {
                failures.Add(error);
                continue;
            }

            records += file.Records.Count;
            var seen = new HashSet<uint>();
            foreach (var record in file.Records)
            {
                Assert.True(record.Size > 0, $"{entry.Name} record {record.Index} is empty");
                if (!seen.Add(record.Hash))
                {
                    withinFileCollisions++;
                }

                distinctAcrossFiles.Add(record.Hash);
            }
        }

        Assert.Empty(failures);
        Assert.Equal(43_006, records);

        // ⚑ Hashes are keys WITHIN a file — exactly one collision across all 55, so a hash addresses
        // a record unambiguously in the file that holds it.
        Assert.Equal(1, withinFileCollisions);

        // ⚠ But they are NOT file-unique identities: only 2,242 distinct values back all 43,006
        // records. Keying a cross-file index on the hash alone would alias ~95% of the corpus.
        Assert.Equal(2_242, distinctAcrossFiles.Count);

        // ⚑ The reason: ALL.DDF is the MASTER table (2,243 records, 2,242 distinct after its single
        // collision) and every per-level file is a SUBSET of it — 54/54, with zero hashes occurring
        // anywhere outside it. So a level ships the records it needs, drawn from one global table.
        var master = ReadHashes(disc, files.Single(e => e.Name.Equals("ALL.DDF", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(2_242, master.Count);
        foreach (var entry in files.Where(e => !e.Name.Equals("ALL.DDF", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.ProperSubset(master, ReadHashes(disc, entry));
        }
    }

    [Fact]
    public void EveryClumpReproducesItsTableCrcAtExactlyOnePageUnit()
    {
        // ⚑ PARSING IS THE PROOF, read off the two loaders in SLUS_205.39: the hash table sits at
        // +8 × unit, its CRC-32 is +12, and exactly ONE unit — 4096 for the streaming loader, 256
        // for the resident one — reproduces it. Then the populated slots must equal +16 and the
        // sections must chain from page 1 to exactly the page count. Measured 2026-09-07 over all
        // 227 shipped .CLP; every gate is inside TryParse, so parsing every file is the check.
        var iso = RequireIso();
        using var disc = ArchiveReader.Open(iso);

        var clumps = disc.ListFiles()
            .Where(e => e.Name.EndsWith(".CLP", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Equal(227, clumps.Count);

        var failures = new List<string>();
        var streamed = new List<string>();
        var resident = 0;
        var populated = 0;
        var misplaced = 0;
        BosClumpFile? barT = null;
        foreach (var entry in clumps)
        {
            var bytes = disc.ReadFile(entry.FullPath);
            Assert.NotNull(bytes);

            if (!BosClumpFile.TryParse(bytes, entry.Name, out var clump, out var error))
            {
                failures.Add(error);
                continue;
            }

            populated += clump.Sections.Count;
            misplaced += clump.Misplaced;
            if (clump.IsStreamed)
            {
                streamed.Add(entry.Name.ToUpperInvariant());
            }
            else
            {
                resident++;
            }

            if (entry.Name.Equals("BAR_T.CLP", StringComparison.OrdinalIgnoreCase))
            {
                barT = clump;
            }
        }

        Assert.Empty(failures);

        // ⚑ The unit follows the LOADER, not the family name: the 54 _T clumps plus the six
        // CLUMP.DIR entries stream at 4096; the 112 plain + 55 _S resident clumps use 256.
        Assert.Equal(60, streamed.Count);
        Assert.Equal(167, resident);
        Assert.Equal(54, streamed.Count(n => n.EndsWith("_T.CLP", StringComparison.Ordinal)));
        Assert.Equal(["ARMOR.CLP", "HUD.CLP", "MOVIES.CLP", "SFX.CLP", "SOUND.CLP", "VA1.CLP"],
            streamed.Where(n => !n.EndsWith("_T.CLP", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.Equal(21_092, populated);
        Assert.Equal(0, misplaced);

        // ⚑⚑ THE HASH IS SOLVED: hash('bar.tex') is BAR_T's section at page 2 — the tag is a disc
        // literal, not a value computed by the routine under test.
        Assert.NotNull(barT);
        Assert.Equal(3, barT.Sections.Count);
        Assert.Equal(8, barT.SlotCount);
        Assert.True(barT.TryFind("bar.tex", out var barTex));
        Assert.Equal(0x8DE8D8C8u, barTex.Tag);
        Assert.Equal(2, barTex.StartPage);
        Assert.Equal(5_187_485, barTex.Size);
        Assert.True(barT.TryFind("bar.hsh", out var barHsh));
        Assert.Equal(1, barHsh.StartPage);
        Assert.True(barT.TryFind("bar.vat", out var barVat));
        Assert.Equal(15_992_832, barVat.Size);
    }

    [Fact]
    public void EveryTextureClumpCarriesOneNameTable()
    {
        // ⚑ Each _T clump's <display>.hsh section is nothing but hash16 keys the string databases
        // know — the level's preload list. Measured 2026-09-06 over all 54 shipped _T.CLP.
        var iso = RequireIso();
        using var disc = ArchiveReader.Open(iso);

        var clumps = disc.ListFiles()
            .Where(e => e.Name.EndsWith("_T.CLP", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Equal(54, clumps.Count);

        // The union of every shipped string database — the corpus a name table is recognised against.
        var known = disc.ListFiles()
            .Where(e => e.Name.EndsWith(".SDB", StringComparison.OrdinalIgnoreCase))
            .Select(e => disc.ReadFile(e.FullPath))
            .Where(bytes => bytes is not null)
            .SelectMany(bytes => BosStringDatabase.TryParse(bytes!, "sdb", out var db, out _)
                ? db.Entries.Select(x => x.Hash)
                : [])
            .ToHashSet();

        var failures = new List<string>();
        var sections = 0;
        var nameTables = 0;
        var names = 0;

        foreach (var entry in clumps)
        {
            var bytes = disc.ReadFile(entry.FullPath);
            Assert.NotNull(bytes);

            if (!BosClumpFile.TryParse(bytes, entry.Name, out var clump, out var error))
            {
                failures.Add(error);
                continue;
            }

            sections += clump.Sections.Count;

            Assert.Equal(1, clump.Sections[0].StartPage);

            // ⚑ EXACTLY ONE section is nothing but hashes the string databases know — the NAME
            // TABLE. ⚠ Its page VARIES: the four WARE levels carry theirs LAST, not first, which is
            // why searching a fixed offset wrongly reported them as having none.
            if (BosClumpFile.TryFindNameTable(bytes, clump, known, out var nameTable))
            {
                nameTables++;
                names += nameTable.Size / 4;
            }

            // The clump names itself in its header string table.
            Assert.NotEmpty(clump.Strings);
        }

        Assert.Empty(failures);
        Assert.Equal(54, nameTables);
        Assert.Equal(8_010, names);
        Assert.True(sections >= 54 * 2, $"expected at least two sections per clump, saw {sections}");
    }

    [Fact]
    public void EverySoundIsAVagpFileCarryingItsOwnRate()
    {
        // ⚑ PARSING IS THE PROOF: every section of a 256-page bank is a VAGp file whose data
        // length fills it, and the section count is the sound count. ⛔ The terminator-pair count
        // this test once used is superseded — it closed 54 of 55 banks and never saw the headers.
        // Measured 2026-09-07 over all 55 shipped banks.
        var iso = RequireIso();
        using var disc = ArchiveReader.Open(iso);

        var banks = disc.ListFiles()
            .Where(e => e.Name.EndsWith("_S.CLP", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Equal(55, banks.Count);

        var failures = new List<string>();
        var sounds = 0;
        var samples = 0L;
        var endBlocks = 0;
        var misplaced = 0;
        var rates = new Dictionary<int, int>();
        BosSoundBank? barBank = null;

        foreach (var entry in banks)
        {
            var bytes = disc.ReadFile(entry.FullPath);
            Assert.NotNull(bytes);

            // ⚑ PARSING IS THE PROOF, and it is exact: every entry of the tail hash table must be a
            // VAGp file whose data length fills its slot, and the populated slots must equal +16.
            if (!BosSoundBank.TryParse(bytes, entry.Name, out var bank, out var error))
            {
                failures.Add(error);
                continue;
            }

            sounds += bank.Sounds.Count;
            samples += bank.Sounds.Sum(s => (long)s.SampleCount);
            endBlocks += bank.Sounds.Count(s => s.HasEndBlock);
            misplaced += bank.Clump.Misplaced;
            foreach (var sound in bank.Sounds)
            {
                rates[sound.SampleRate] = rates.GetValueOrDefault(sound.SampleRate) + 1;
            }

            if (entry.Name.Equals("BAR_S.CLP", StringComparison.OrdinalIgnoreCase))
            {
                barBank = bank;
            }

            // Every sound decodes to exactly 28 samples per block, inside 16-bit range.
            var first = BosSoundBank.Decode(bytes, bank.Sounds[0]);
            Assert.Equal(bank.Sounds[0].SampleCount, first.Length);
        }

        Assert.Empty(failures);

        // ⚑ 55 of 55 now — including GLOBAL_S.CLP, whose 7 looping sounds the old terminator-pair
        // walk could not close. The header walk needs no terminator.
        Assert.Equal(3_430, sounds);
        Assert.Equal(0, misplaced);

        // ⚑⚑ 3,423 of the 3,430 sounds close with the SPU2 end marker — the byte-identical block
        // `00 07 77 … 77`, whose dummy nibble decodes to 28 samples of a constant +28,672 (87% of
        // full scale). It is a marker, not audio, and is not decoded. The 7 that lack it are
        // GLOBAL_S's version-32 loopers, which end on flag 3.
        Assert.Equal(3_423, endBlocks);
        Assert.True(samples > 10_000_000, $"expected minutes of audio, saw {samples} samples");

        // ⚑⚑ THE RATES ARE PER SOUND AND MIXED (measured 2026-09-07 with an independent Python walk
        // of the 55 banks): a single constant — 22,050 or 11,025 — could never have been right.
        Assert.Equal(1_466, rates[16000]);
        Assert.Equal(1_250, rates[18000]);
        Assert.Equal(567, rates[22050]);
        Assert.Equal(1_000, rates.Keys.Min());
        Assert.Equal(32_000, rates.Keys.Max());
        Assert.DoesNotContain(11025, rates.Keys);

        // BAR_S.CLP: 34 sounds at 256-byte pages, the first BF_Dirt_Large_1 at 18 kHz, keyed by the
        // engine's hash of its /Final_Assets/sound/ path.
        Assert.NotNull(barBank);
        Assert.Equal(34, barBank.Sounds.Count);
        Assert.Equal("BF_Dirt_Large_1", barBank.Sounds[0].Name);
        Assert.Equal(18000, barBank.Sounds[0].SampleRate);
        Assert.Equal(1536, barBank.Sounds[0].Pitch);
        Assert.Equal(256, barBank.Sounds[0].HeaderOffset);
        Assert.Equal(10_672 / 16, barBank.Sounds[0].BlockCount);

        // 667 blocks are stored, 666 carry audio, and the export must not end on the marker's DC.
        Assert.True(barBank.Sounds[0].HasEndBlock);
        Assert.Equal(18_648, barBank.Sounds[0].SampleCount);
        var barBytes = disc.ReadFile(banks.Single(e => e.Name.Equals("BAR_S.CLP", StringComparison.OrdinalIgnoreCase))
            .FullPath)!;
        var barFirst = BosSoundBank.Decode(barBytes, barBank.Sounds[0]);
        Assert.Equal(18_648, barFirst.Length);
        Assert.DoesNotContain((short)28_672, barFirst);
        Assert.True(barBank.TryFind("/Final_Assets/sound/FS_Linoleum_4.vag", out var linoleum));
        Assert.Equal(16000, linoleum.SampleRate);
    }

    [Fact]
    public void TwentySixLevelsShipAVoiceBankWhoseDirectoryTilesItExactly()
    {
        // ⚑ The .vat section is keyed <stem>.vat by the engine's own hash and its line directory
        // lives in the level's resident .CLP, terminated by a record whose offset is the section's
        // exact size. Measured 2026-09-07 over the 54 texture clumps: 26 carry a .vat, 28 do not.
        var iso = RequireIso();
        using var disc = ArchiveReader.Open(iso);

        var textureClumps = disc.ListFiles()
            .Where(e => e.Name.EndsWith("_T.CLP", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Equal(54, textureClumps.Count);

        var withVoice = 0;
        var lines = 0;
        var failures = new List<string>();
        BosVoiceBank? bar = null;
        foreach (var entry in textureClumps)
        {
            var streamed = disc.ReadFile(entry.FullPath)!;
            var residentPath = entry.FullPath[..^"_T.CLP".Length] + ".CLP";
            var resident = disc.ReadFile(residentPath);
            Assert.NotNull(resident);

            if (!BosVoiceBank.TryOpen(streamed, resident, entry.Name, out var bank, out var error))
            {
                if (!error.Contains("ships no voice bank", StringComparison.Ordinal))
                {
                    failures.Add(error);
                }

                continue;
            }

            withVoice++;
            lines += bank.Lines.Count;
            if (entry.Name.Equals("BAR_T.CLP", StringComparison.OrdinalIgnoreCase))
            {
                bar = bank;
            }
        }

        Assert.Empty(failures);
        Assert.Equal(26, withVoice);
        Assert.Equal(1_118, lines);

        // BAR: 188 entries (120 .vag + 68 .anm), 179 distinct offsets, the section 15,992,832 bytes.
        Assert.NotNull(bar);
        Assert.Equal(15_992_832, bar.Section.Size);
        Assert.Equal(188, bar.Lines.Count);
        Assert.Equal(120, bar.Lines.Count(l => l.Kind == BosVoiceLineKind.Voice));
        Assert.Equal(68, bar.Lines.Count(l => l.Kind == BosVoiceLineKind.Animation));
        Assert.Equal(179, bar.Lines.Select(l => l.Offset).Distinct().Count());
        Assert.Equal("Ruby_02.vag", bar.Lines[0].Name);
        Assert.Equal(51_200, bar.Lines[0].Length);
    }

    [Fact]
    public void EveryResidentTextureDecodesThroughTheGsLayout()
    {
        // ⚑ The .tex format: 0x80 header, CLUT transfer, PSMT8 texels uploaded as PSMCT32 at half
        // size. The packet walk must consume every section exactly, and the pixel values below are
        // pinned from an INDEPENDENT Python decode of the same GS layout (2026-09-07), so a wrong
        // block, column or CSM1 table moves the pinned texels. Measured over all 227 clumps.
        var iso = RequireIso();
        using var disc = ArchiveReader.Open(iso);

        var clumps = disc.ListFiles()
            .Where(e => e.Name.EndsWith(".CLP", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var failures = new List<string>();
        var textures = 0;
        var trueColour = 0;
        var fullyCovered = 0;
        var alphaFlag = 0;
        var uploads = 0;
        var perClump = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var streamedPacks = 0;
        var packsThatAreSingleTextures = 0;
        BosTexture? logo = null;
        BosTexture? loading = null;
        BosTexture? slayer = null;
        foreach (var entry in clumps)
        {
            var bytes = disc.ReadFile(entry.FullPath)!;
            var clump = BosClumpFile.Parse(bytes, entry.Name);

            // The streamed <display>.tex of a _T clump is a PACK indexed by the level's .lmp, not a
            // single texture: the key resolves on 54/54 and the shape probe rejects all of them.
            if (clump.IsStreamed && entry.Name.EndsWith("_T.CLP", StringComparison.OrdinalIgnoreCase))
            {
                var stem = Path.GetFileNameWithoutExtension(clump.Strings[0]).ToLowerInvariant();
                if (clump.TryFind(stem + ".tex", out var pack))
                {
                    streamedPacks++;
                    if (BosTexture.IsTexture(BosClumpFile.Read(bytes, pack)))
                    {
                        packsThatAreSingleTextures++;
                    }
                }
            }

            foreach (var section in BosTexture.FindTextureSections(bytes, clump))
            {
                var name = BosKnownAssetNames.NameOrTag(section.Tag);
                if (!BosTexture.TryParse(BosClumpFile.Read(bytes, section), $"{entry.Name}:{name}", out var texture,
                        out var error))
                {
                    failures.Add(error);
                    continue;
                }

                textures++;
                uploads += texture.Uploads.Count;
                perClump[entry.Name] = perClump.GetValueOrDefault(entry.Name) + 1;
                if (texture.IsTrueColour)
                {
                    trueColour++;
                }

                if (texture.IsFullyCovered)
                {
                    fullyCovered++;
                }

                if (texture.AlphaIsFlag)
                {
                    alphaFlag++;
                }

                if (name == "bos_logo")
                {
                    logo = texture;
                }
                else if (name == "loading")
                {
                    loading = texture;
                }
                else if (name == "01_slayer")
                {
                    slayer = texture;
                }
            }
        }

        Assert.Empty(failures);
        Assert.Equal(2_876, textures);
        Assert.Equal(3, trueColour);

        // 2,411 of the 2,873 CLUT textures upload their whole w/2 × h/2, and the three true-colour
        // ones upload their whole w × h — 2,414 in all, 462 partial (independent Python walk,
        // 2026-09-07). ⚠ A count of 2,411 here means true colour is being excluded.
        Assert.Equal(2_414, fullyCovered);
        Assert.Equal(6_326, uploads);
        Assert.Equal(130, perClump["HUD.CLP"]);
        Assert.Equal(138, perClump["GLOBAL.CLP"]);
        Assert.Equal(25, perClump["SFX.CLP"]);
        Assert.Equal(27, perClump["INV_SWAP.CLP"]);
        Assert.Equal(92, perClump["ARMOR.CLP"]);
        Assert.Equal(54, streamedPacks);
        Assert.Equal(0, packsThatAreSingleTextures);

        // bos_logo.tex (HUD.CLP): 192×192, uploaded as 96×96 PSMCT32 at dbw 2; centre is the
        // near-white of the logo, the corner the transparent dark backing.
        Assert.NotNull(logo);
        Assert.Equal((192, 192), (logo.Width, logo.Height));
        Assert.Equal(2, logo.Uploads[1].BufferWidth);
        Assert.Equal((96, 96), (logo.Uploads[1].Width, logo.Uploads[1].Height));
        Assert.Equal(new byte[] { 220, 216, 216, 253 }, logo.Rgba.AsSpan((96 * 192 + 96) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 4, 4, 4, 0 }, logo.Rgba.AsSpan(0, 4).ToArray());

        // ⚑ ALPHA IS A FLAG on 2,137 of the 2,873 CLUT textures: every texel they sample carries
        // a GS alpha of 0 or 1 (1,885 from an all-≤1 CLUT, 252 from the low entries of a graded
        // one — independent Python census 2026-09-07). Scaled from 0x80 they export as blank
        // PNGs (alpha 2 of 255); exported as coverage they are the level art.
        Assert.Equal(2_137, alphaFlag);
        Assert.False(logo.AlphaIsFlag);

        // loading.tex (SFX.CLP): 256×256 at dbw 2; its CLUT's only alpha is 1, so it is opaque.
        Assert.NotNull(loading);
        Assert.Equal((256, 256), (loading.Width, loading.Height));
        Assert.True(loading.AlphaIsFlag);
        Assert.Equal(new byte[] { 140, 138, 138, 255 }, loading.Rgba.AsSpan((128 * 256 + 128) * 4, 4).ToArray());

        // 01_slayer.tex (INV_SWAP.CLP): the perk drawing, 128×128 at dbw 1.
        Assert.NotNull(slayer);
        Assert.Equal((128, 128), (slayer.Width, slayer.Height));
        Assert.Equal(1, slayer.Uploads[1].BufferWidth);
        Assert.Equal(new byte[] { 122, 112, 98, 116 }, slayer.Rgba.AsSpan((64 * 128 + 64) * 4, 4).ToArray());
    }

    /// <summary>The distinct record hashes in one .DDF.</summary>
    private static HashSet<uint> ReadHashes(ArchiveReader disc, ArchiveEntry entry)
    {
        var file = BosDataFile.Parse(disc.ReadFile(entry.FullPath)!, entry.Name);
        return [.. file.Records.Select(r => r.Hash)];
    }

    [Fact]
    public void TheBarLevelDatabaseHasItsMeasuredShape()
    {
        var iso = RequireIso();
        using var disc = ArchiveReader.Open(iso);

        var entry = disc.ListFiles().FirstOrDefault(e =>
            e.FullPath.Replace('\\', '/').Equals("DATA/C1/BAR/BAR.SDB", StringComparison.OrdinalIgnoreCase));
        Assert.SkipWhen(entry is null, RealAssetPaths.SkipMessage("BAR.SDB"));

        var database = BosStringDatabase.Parse(disc.ReadFile(entry.FullPath)!, "BAR.SDB");

        // Measured 2026-09-06 on the shipped file: 85 strings, table at 3,436. Nothing is left
        // unresolved once the walk stops at the terminator — the slot that used to show up as a
        // stray WAS the terminator, sitting exactly 128 slots in (the hashtablesize the disc's own
        // debug dump names).
        Assert.Equal(85, database.Entries.Count);
        Assert.Equal(0, database.UnresolvedSlots);
        Assert.Equal(3436, database.HashTableOffset);

        // Prose, which is the oracle that the UTF-16 decode and the offsets are both right.
        Assert.Contains(database.Entries, e => e.Value == "Sleeping Man");
        Assert.Contains(database.Entries, e => e.Value == "Freezer Chest");
    }
}
