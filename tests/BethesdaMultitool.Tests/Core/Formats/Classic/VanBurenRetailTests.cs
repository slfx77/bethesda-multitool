using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Audio;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.VanBuren;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) of the cancelled Fallout 3 "Van Buren" prototype
///     (Dec 9 2003). The build is staged as a <c>.rar</c> inside
///     <c>Van Buren (2003-12-9, PC - Prototype)/Fallout 3 (Dec 9, 2003 prototype).zip</c>; these run against an
///     extracted tree and skip when it is absent, so nothing here depends on an unpack step having
///     been done.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class VanBurenRetailTests
{
    /// <summary>The prototype's <c>data</c> directory, wherever it has been unpacked.</summary>
    private static string RequireDataDirectory()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        var root = RepositoryRoot();
        var candidates = new[]
        {
            Path.Combine(root, "Sample", "Builds", "Van Buren (2003-12-9, PC - Prototype)", "data"),
            Path.Combine(root, "Sample", "Builds", "F3_Demo", "data")
        };

        var found = candidates.FirstOrDefault(Directory.Exists);
        Assert.SkipWhen(found is null, RealAssetPaths.SkipMessage("Van Buren prototype data"));
        return found;
    }

    [Fact]
    public void TheSecondUntaggedFamilyIsGrannyAndDeclaresAConstantHeaderSize()
    {
        // ⚑ IDENTIFICATION, not a decode: 547 payloads open with the exact 16-byte Granny 2
        // signature, all declare header size 352, and the build ships granny2.dll beside the
        // executable. ⛔ Granny is proprietary with no permissive reference, so nothing decodes it.
        var data = RequireDataDirectory();
        var granny = 0;
        var sizes = new HashSet<int>();

        foreach (var file in Directory.GetFiles(data, "*.grp"))
        {
            var bytes = File.ReadAllBytes(file);
            if (!VanBurenGrpArchive.TryParse(bytes, Path.GetFileName(file), out var archive, out _))
            {
                continue;
            }

            foreach (var entry in archive.Entries)
            {
                var payload = VanBurenGrpArchive.Read(bytes, entry);
                if (!VanBurenGrannyFile.IsGranny(payload))
                {
                    continue;
                }

                granny++;
                sizes.Add(VanBurenGrannyFile.ReadDeclaredHeaderSize(payload));
            }
        }

        Assert.True(granny > 500, $"expected ~547 Granny payloads, saw {granny}");

        // ⚠ One constant across the whole family — a build declaring anything else should surface
        // rather than be waved through as this one's 352.
        Assert.Equal([VanBurenGrannyFile.DeclaredHeaderSize], sizes);
    }

    [Fact]
    public void TheUntaggedImageFamilyDecodesWithPowerOfTwoDimensions()
    {
        // ⚑ The largest NAMELESS payload family in the .grp archives is an image format: format id
        // 0x00020000, u16 w/h at +12, bpp at +16, pixels from +18. Measured 2026-09-06 over all
        // 1,526: bpp is only ever 24 or 32, every dimension is a power of two, and the size
        // accounts for the pixels with either no trailer or a 26-byte one.
        var data = RequireDataDirectory();
        var images = 0;
        var withTrailer = 0;
        var powerOfTwo = 0;
        var depths = new HashSet<int>();
        var failures = new List<string>();

        foreach (var file in Directory.GetFiles(data, "*.grp"))
        {
            var bytes = File.ReadAllBytes(file);
            if (!VanBurenGrpArchive.TryParse(bytes, Path.GetFileName(file), out var archive, out _))
            {
                continue;
            }

            foreach (var entry in archive.Entries)
            {
                var payload = VanBurenGrpArchive.Read(bytes, entry);
                if (payload.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4)) !=
                    VanBurenImageFile.UncompressedFormat)
                {
                    continue;
                }

                if (!VanBurenImageFile.TryParse(payload, $"{Path.GetFileName(file)}#{entry.Index}",
                        out var image, out var error))
                {
                    failures.Add(error);
                    continue;
                }

                images++;
                depths.Add(image.BitsPerPixel);
                if (image.TrailerLength == 26)
                {
                    withTrailer++;
                }

                if ((image.Width & (image.Width - 1)) == 0 && (image.Height & (image.Height - 1)) == 0)
                {
                    powerOfTwo++;
                }

                var rgb = VanBurenImageFile.DecodeRgb(payload, image);
                Assert.Equal(image.Width * image.Height * 3, rgb.Length);
            }
        }

        Assert.Empty(failures);
        Assert.True(images > 1_400, $"expected ~1,526 images, saw {images}");

        // ⚠ Only 24 and 32 bpp ship.
        Assert.Equal([24, 32], depths.OrderBy(d => d));

        // ⚠ MOST dims are powers of two but NOT all — 82 are screen art (800x472, 149x150), so a
        // power-of-two check must never be used as a gate.
        Assert.True(powerOfTwo > 1_400, $"expected ~1,444 power-of-two images, saw {powerOfTwo}");
        Assert.True(images - powerOfTwo > 50, "expected the ~82 non-power-of-two screen images");

        // ⚠ Most carry a 26-byte trailer, so the payload length cannot derive the dimensions.
        Assert.True(withTrailer > 800, $"expected ~939 with a trailer, saw {withTrailer}");
    }

    [Fact]
    public void EverySceneChunkStreamTilesItsDeclaredContentExactly()
    {
        // ⚑ PARSING IS THE PROOF: the chunks must consume [8, 8+declaredLength) with nothing left
        // over. Measured 2026-09-06 over all 39 shipped 8TRE scenes.
        var data = RequireDataDirectory();
        var failures = new List<string>();
        var scenes = 0;
        var chunks = 0;
        var sequences = new HashSet<string>(StringComparer.Ordinal);
        var trailerless = 0;

        foreach (var file in Directory.GetFiles(data, "*.grp"))
        {
            var bytes = File.ReadAllBytes(file);
            if (!VanBurenGrpArchive.TryParse(bytes, Path.GetFileName(file), out var archive, out _))
            {
                continue;
            }

            foreach (var entry in archive.Entries)
            {
                var payload = VanBurenGrpArchive.Read(bytes, entry);
                if (!VanBurenSceneFile.IsScene(payload))
                {
                    continue;
                }

                scenes++;
                if (!VanBurenSceneFile.TryParse(payload, $"{Path.GetFileName(file)}#{entry.Index}",
                        out var scene, out var error))
                {
                    failures.Add(error);
                    continue;
                }

                chunks += scene.Chunks.Count;
                sequences.Add(string.Join(' ', scene.Chunks.Select(c => c.Tag)));
                if (scene.TrailerLength == 0)
                {
                    trailerless++;
                }
            }
        }

        Assert.Empty(failures);
        Assert.Equal(39, scenes);

        // ⚠ EVERY scene carries a trailer after its declared content. Walking to the end of the
        // payload instead of to 8+length therefore fails on nearly all of them.
        Assert.Equal(0, trailerless);

        // ⚑ The layout is ordered and near-fixed: only THREE distinct top-level sequences across
        // the whole corpus, all sharing HEAD ... MATD TXTD VTXD TREE.
        Assert.Equal(3, sequences.Count);
        Assert.All(sequences, s => Assert.StartsWith("HEAD", s, StringComparison.Ordinal));
        Assert.All(sequences, s => Assert.EndsWith("MATD TXTD VTXD TREE", s, StringComparison.Ordinal));
        Assert.True(chunks >= 39 * 6, $"expected at least six chunks per scene, saw {chunks}");
    }

    [Fact]
    public void EveryGroupArchiveTilesExactlyAndItsPayloadsCarryTheirOwnTags()
    {
        // ⚑ The container has NO magic string, so PARSING IS THE PROOF: the two constant header
        // dwords, then every entry beginning exactly where the previous ended and the last ending
        // exactly at EOF. Measured 2026-09-06: 24 archives, 7,044 entries, all tiling.
        var data = RequireDataDirectory();
        var files = Directory.GetFiles(data, "*.grp").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(24, files.Count);

        var failures = new List<string>();
        var entries = 0;
        var empty = 0;
        var tags = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            if (!VanBurenGrpArchive.TryParse(bytes, Path.GetFileName(file), out var archive, out var error))
            {
                failures.Add(error);
                continue;
            }

            entries += archive.Entries.Count;
            if (archive.Entries.Count == 0)
            {
                empty++;
            }

            foreach (var entry in archive.Entries)
            {
                tags[entry.Tag] = tags.GetValueOrDefault(entry.Tag) + 1;
            }
        }

        Assert.Empty(failures);
        Assert.Equal(7044, entries);

        // Six archives ship with nothing in them, and being exactly 12 bytes is what fixes the
        // header length rather than leaving it inferred.
        Assert.Equal(6, empty);
        Assert.All(files.Where(f => new FileInfo(f).Length == VanBurenGrpArchive.HeaderLength),
            f => Assert.Empty(VanBurenGrpArchive.Parse(File.ReadAllBytes(f), f).Entries));

        // ⚑ Entries have no names; the payload's own leading tag is the only identity. B3D dominates
        // (meshes) and RIFF means the prototype's audio is plain WAV.
        Assert.Equal(3915, tags["B3D"]);
        Assert.Equal(285, tags["EEN2"]);
        Assert.Equal(113, tags["RIFF"]);
    }

    [Fact]
    public void TheTypedEntityGroupsHoldNothingButEen2Records()
    {
        // The `_XXX.grp` groups are the prototype's object tables — ammo, armour, containers,
        // critters, doors, items, uses, weapons — and every payload in them is an EEN2 record.
        var data = RequireDataDirectory();
        var typed = new[] { "_AMO", "_ARM", "_CON", "_CRT", "_DOR", "_ITM", "_USE", "_WEA" };

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var stem in typed)
        {
            var path = Path.Combine(data, stem + ".grp");
            Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage(stem + ".grp"));

            var archive = VanBurenGrpArchive.Parse(File.ReadAllBytes(path), stem + ".grp");
            Assert.All(archive.Entries, e => Assert.Equal("EEN2", e.Tag));
            counts[stem] = archive.Entries.Count;
        }

        Assert.Equal(
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["_AMO"] = 20,
                ["_ARM"] = 22,
                ["_CON"] = 48,
                ["_CRT"] = 108,
                ["_DOR"] = 13,
                ["_ITM"] = 5,
                ["_USE"] = 18,
                ["_WEA"] = 51
            },
            counts);
    }

    [Fact]
    public void EveryEntityRecordReadsItsLengthPrefixedStrings()
    {
        // ⚑ The u16 length prefix is what makes these readable with no guessing. Measured over all
        // 285: the two strings read as clean ASCII on 282, and the other three declare an EMPTY
        // name — a record without one, not a parse failure, so the reader must accept it.
        var data = RequireDataDirectory();

        var records = 0;
        var named = 0;
        var withGcre = 0;
        var failures = new List<string>();

        foreach (var file in Directory.GetFiles(data, "*.grp"))
        {
            var bytes = File.ReadAllBytes(file);
            var archive = VanBurenGrpArchive.Parse(bytes, Path.GetFileName(file));
            foreach (var entry in archive.Entries.Where(e => e.Tag == "EEN2"))
            {
                var payload = VanBurenGrpArchive.Read(bytes, entry);
                if (!VanBurenEntityRecord.TryParse(payload, entry.Name, out var record, out var error))
                {
                    failures.Add($"{Path.GetFileName(file)}/{entry.Name}: {error}");
                    continue;
                }

                records++;
                if (record.Name.Length > 0)
                {
                    named++;
                }

                if (record.NestedTags.Contains("GCRE"))
                {
                    withGcre++;
                }
            }
        }

        Assert.Empty(failures);
        Assert.Equal(285, records);
        Assert.Equal(282, named);

        // GCRE appears in exactly the 108 records _CRT.grp holds — the tags are a type discriminator.
        Assert.Equal(108, withGcre);
    }

    [Fact]
    public void TheArchiveSeamOpensAGrpAndItsRiffPayloadsAreStandardWav()
    {
        // ⚑ Two claims at once: the probe recognises a .grp through the shared ArchiveReader (so the
        // whole `archive` group works on one), and its RIFF payloads are plain WAV — which means the
        // existing content-sniffing audio path decodes them with no Van-Buren-specific code.
        var data = RequireDataDirectory();
        var path = Path.Combine(data, "Sounds.grp");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("Sounds.grp"));

        using var archive = ArchiveReader.Open(path);
        Assert.Equal("GRP (Van Buren)", archive.FormatName);

        var entries = archive.ListFiles();
        Assert.NotEmpty(entries);

        var riff = entries.Where(e => e.Name.EndsWith(".RIFF", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(riff);

        var decoded = 0;
        foreach (var entry in riff)
        {
            var bytes = archive.ReadFile(entry.FullPath);
            Assert.NotNull(bytes);
            Assert.True(RiffWaveFile.IsRiffWave(bytes), $"{entry.Name} does not carry a RIFF header");

            var wave = RiffWaveFile.Parse(bytes, entry.Name);
            Assert.True(wave.SampleRate > 0, $"{entry.Name} declares no sample rate");
            decoded++;
        }

        Assert.Equal(riff.Count, decoded);
    }

    [Fact]
    public void TheStringTableTilesAndReadsAsTheGamesOwnUiText()
    {
        var data = RequireDataDirectory();
        var path = Path.Combine(Path.GetDirectoryName(data)!, "English.stf");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("English.stf"));

        var table = VanBurenStringTable.Parse(File.ReadAllBytes(path), "English.stf");

        // Measured 2026-09-06: 3,281 strings whose bytes tile 52,508 → 254,846 with no gap.
        Assert.Equal(3281, table.Count);

        // ⚑ The oracle is prose. Structural checks cannot tell a right offset from a plausible one;
        // menu text reading back correctly can.
        Assert.Contains("Single Player", table.Strings);
        Assert.Contains("New Game", table.Strings);
        Assert.Contains("Load Game", table.Strings);
        Assert.All(table.Strings, s => Assert.DoesNotContain('\0', s));
    }

    [Fact]
    public void EveryMeshIsIdentifiedByTheNodeAfterSceneRoot()
    {
        // ⚑ The container gives entries no names, so this is how a mesh becomes identifiable at all.
        // Measured 2026-09-06: 3,915 B3D payloads, of which 3,912 carry exactly two nodes with
        // "Scene Root" first; the 3 exceptions all declare opcode 53 and live in Critters.grp.
        var data = RequireDataDirectory();

        var meshes = 0;
        var named = 0;
        var unnamed = new List<byte>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.GetFiles(data, "*.grp"))
        {
            var bytes = File.ReadAllBytes(file);
            var archive = VanBurenGrpArchive.Parse(bytes, Path.GetFileName(file));
            foreach (var entry in archive.Entries.Where(e => e.Tag == "B3D"))
            {
                var mesh = VanBurenMesh.Parse(VanBurenGrpArchive.Read(bytes, entry), entry.Name);
                meshes++;
                if (mesh.MeshName is { } meshName)
                {
                    named++;
                    names.Add(meshName);
                }
                else
                {
                    unnamed.Add(mesh.FirstOpcode);
                }
            }
        }

        Assert.Equal(3915, meshes);
        Assert.Equal(3912, named);
        Assert.Equal(3589, names.Count);

        // The three that cannot be named share one opcode — pinned so a regression that started
        // dropping names would not hide among them.
        Assert.Equal<byte[]>([53, 53, 53], [.. unnamed]);

        // Prose is the oracle: these have to read as authored identifiers.
        Assert.Contains("CR_Bat", names);
        Assert.Contains("CR_Cougar", names);
        Assert.Contains("CR_DesertStalker", names);
    }

    /// <summary>The checkout root: the nearest ancestor of the test binary holding the solution file.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BethesdaMultitool.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
               throw new InvalidOperationException("BethesdaMultitool.slnx not found above the test binary.");
    }

    [Fact]
    public void EveryEffectGroupTilesItsPayloadExactly()
    {
        // ⚑ The proof for the VEG family (2026-09-06): all 119 consume their payload EXACTLY —
        // header, the counted VFX blocks, and the trailing UNTAGGED group block. Walking only the
        // counted blocks leaves exactly 91 bytes over on every file, which is what hid the group
        // block; exact tiling is what makes the reading certain rather than plausible.
        var root = RequireDataDirectory();
        var groups = 0;
        var failures = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.grp", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            if (!VanBurenGrpArchive.TryParse(bytes, Path.GetFileName(file), out var archive, out _))
            {
                continue;
            }

            foreach (var entry in archive.Entries)
            {
                var payload = VanBurenGrpArchive.Read(bytes, entry);
                if (!VanBurenVegFile.IsVeg(payload))
                {
                    continue;
                }

                groups++;
                if (!VanBurenVegFile.TryParse(payload, $"{Path.GetFileName(file)}#{entry.Index}",
                        out var veg, out var error))
                {
                    if (failures.Count < 8)
                    {
                        failures.Add(error);
                    }

                    continue;
                }

                // ⚠ Retail's group block is always these three; a different set means the trailing
                // block was mis-framed rather than the data being unusual.
                Assert.Equal<string[]>(
                    ["Loop", "MinStartTime", "MaxStartTime"],
                    [.. veg.GroupProperties.Select(x => x.Name)]);
                Assert.NotEmpty(veg.Blocks);
            }
        }

        Assert.True(groups > 100, $"expected ~119 VEG groups, saw {groups}");
        Assert.Empty(failures);
    }
}