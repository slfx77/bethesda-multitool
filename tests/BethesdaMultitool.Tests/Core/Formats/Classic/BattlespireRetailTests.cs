using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.Battlespire;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Xngine.Flic;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks of the Battlespire install (<c>RUN_BUCKET_B=1</c>). These decode WHOLE
///     records, not signatures: the per-entry LZSS bug that survived until 2026-09-03 produced a
///     correct <c>v2.7</c> tag on every mesh and garbage after it, so a signature check proves
///     nothing about this codec.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class BattlespireRetailTests
{
    private static string RequireGameData()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Battlespire();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Battlespire (GAMEDATA)"));
        return root;
    }

    [Fact]
    public void EveryMovie_ParsesWholeAtOffsetZeroAndIsSixFortyByFourEighty()
    {
        var path = Path.Combine(RequireGameData(), "FLC.BSA");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("FLC.BSA"));

        using var archive = ArchiveReader.Open(path);
        var entries = archive.ListFiles();
        Assert.Equal(164, entries.Count);

        var movies = 0;
        var strays = new List<string>();
        var failures = new List<string>();
        var frames = 0;

        foreach (var entry in entries)
        {
            var bytes = archive.ReadFile(entry.FullPath);
            if (bytes is null || !FlicFile.IsFlic(bytes))
            {
                // ADR.TXT is an authoring directory listing, the same stray shape 3D.BSA carries.
                strays.Add(entry.Name);
                continue;
            }

            try
            {
                var flic = FlicFile.Parse(bytes, entry.Name);
                Assert.Equal((640, 480), (flic.Width, flic.Height));
                frames += flic.Frames.Count;
                movies++;
            }
            catch (InvalidDataException e)
            {
                failures.Add($"{entry.Name}: {e.Message}");
            }
        }

        Assert.Empty(failures);
        Assert.Equal(163, movies);
        Assert.Equal<string[]>(["ADR.TXT"], [.. strays]);
        Assert.True(frames > 1_000, $"only {frames} frames decoded");
    }

    [Fact]
    public void EveryMovie_StartsAtOffsetZero_NoJunkLeadBytes()
    {
        // ⛔ REFUTES a standing note that "some records carry 2 junk lead bytes — sniff 0xAF12 at
        // +4 AND +6". Measured 2026-09-06: all 163 movies carry the magic at +4 and their declared
        // size equals the whole entry, so NONE needs the +2 offset. The size equality is exact
        // arithmetic, not a signature sniff — a +2 record could not satisfy it.
        var path = Path.Combine(RequireGameData(), "FLC.BSA");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("FLC.BSA"));

        using var archive = ArchiveReader.Open(path);
        var shifted = new List<string>();
        var mismatched = new List<string>();

        foreach (var entry in archive.ListFiles())
        {
            var bytes = archive.ReadFile(entry.FullPath);
            if (bytes is null || bytes.Length < 8 || !FlicFile.IsFlic(bytes))
            {
                continue;
            }

            if (BitConverter.ToUInt16(bytes, 6) == FlicFile.FlcMagic &&
                BitConverter.ToUInt16(bytes, 4) != FlicFile.FlcMagic)
            {
                shifted.Add(entry.Name);
            }

            if (BitConverter.ToUInt32(bytes, 0) != bytes.Length)
            {
                mismatched.Add($"{entry.Name}: declared {BitConverter.ToUInt32(bytes, 0)}, actual {bytes.Length}");
            }
        }

        Assert.Empty(shifted);
        Assert.Empty(mismatched);
    }

    [Fact]
    public void EveryLooseMesh_ParsesWithTheTenByteePlaneHeader()
    {
        var root = RequireGameData();
        var paths = Directory.EnumerateFiles(root, "*.3D")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(245, paths.Count);

        var planes = 0;
        foreach (var path in paths)
        {
            var mesh = BattlespireMeshArchive.ParseLoose(File.ReadAllBytes(path), Path.GetFileName(path));
            Assert.Equal(XnGineMeshVersion.V27, mesh.Version);
            Assert.Equal(XnGineMeshLayout.Battlespire, mesh.Layout);
            Assert.Equal(6, mesh.Planes[0].HeaderTail.Length);
            planes += mesh.Planes.Count;
        }

        Assert.Equal(7_945, planes);

        // Daggerfall's 8-byte layout must NOT accept these files — that is the whole reason the
        // layout follows the source rather than the shared "v2.7" tag.
        var armor = File.ReadAllBytes(Path.Combine(root, "ARMOR.3D"));
        Assert.Throws<InvalidDataException>(() => XnGineMesh.Parse(armor, 0));

        var mesh339 = BattlespireMeshArchive.ParseLoose(armor, "ARMOR.3D");
        Assert.Equal(339, mesh339.Points.Count);
        Assert.Equal(373, mesh339.Planes.Count);
        Assert.Equal(504, XnGineMeshDecomposer.Decompose(mesh339).TriangleCount);
    }

    /// <summary>
    ///     Both mesh archives, with their entry count, how many entries are NOT meshes, and the
    ///     points and planes the rest decode to. 3D.BSA carries five strays — a DOS executable, a
    ///     text file, a copy of its own name and two <c>.3DT</c> records — so "every entry is a
    ///     mesh" would be a false expectation.
    /// </summary>
    [Theory]
    [InlineData("3D.BS6", 2_115, 0, 106_135, 123_235, 0)]
    [InlineData("3D.BSA", 2_400, 5, 117_988, 134_754, 25)]
    public void EveryArchivedMesh_DecompressesAndParsesWhole(string archiveName, int expectedCount, int expectedStrays,
        int expectedPoints, int expectedPlanes, int expectedV26)
    {
        var path = Path.Combine(RequireGameData(), archiveName);
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage(archiveName));

        using var archive = BattlespireMeshArchive.Open(path);
        Assert.Equal(expectedCount, archive.Count);

        var points = 0;
        var planes = 0;
        var v26 = 0;
        var strays = new List<string>();
        for (var i = 0; i < archive.Count; i++)
        {
            if (!archive.TryParse(i, out var mesh, out var error))
            {
                strays.Add(archive.EntryName(i));
                Assert.False(string.IsNullOrEmpty(error));
                continue;
            }

            // Battlespire is not uniformly v2.7: 3D.BSA holds 25 v2.6 meshes (water planes and
            // islands), all of which still use the 10-byte plane header.
            Assert.Contains(mesh.Version, new[] { XnGineMeshVersion.V26, XnGineMeshVersion.V27 });
            if (mesh.Version == XnGineMeshVersion.V26)
            {
                v26++;
            }

            // A record that decompressed wrongly still carries the tag, so assert the geometry:
            // every plane must reference points that exist, which Parse enforces, and a mesh must
            // have some.
            Assert.True(mesh.Points.Count > 0, $"{archive.EntryName(i)} decoded to no points.");
            Assert.True(mesh.Planes.Count > 0, $"{archive.EntryName(i)} decoded to no planes.");
            points += mesh.Points.Count;
            planes += mesh.Planes.Count;
        }

        Assert.Equal(expectedStrays, strays.Count);
        Assert.Equal(expectedPoints, points);
        Assert.Equal(expectedPlanes, planes);
        Assert.Equal(expectedV26, v26);

        if (expectedStrays > 0)
        {
            // 3D.BSA's five non-mesh entries, by name.
            Assert.Equal(["ARCH3.EXE", "ADR.TXT", "3D.BSA", "L4STACK.3DT", "L4STACK2.3DT"], strays);
        }
    }

    [Fact]
    public void EveryBsiEntry_DecodesToExactlyItsDeclaredPixels()
    {
        var path = Path.Combine(RequireGameData(), "BSI.BSA");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("BSI.BSA"));

        using var archive = ArchiveReader.Open(path);
        var entries = archive.ListFiles();
        Assert.Equal(2_599, entries.Count);

        var images = 0;
        var frames = 0;
        var compression = new Dictionary<int, int>();
        var strays = new List<string>();
        var withColorMap = 0;
        var empty = 0;
        foreach (var entry in entries)
        {
            var bytes = archive.ReadFile(entry.FullPath);
            Assert.NotNull(bytes);

            BsiFile file;
            try
            {
                file = BsiFile.Parse(bytes, entry.Name);
            }
            catch (InvalidDataException)
            {
                strays.Add(entry.Name);
                continue;
            }

            if (file.Images.Count == 0)
            {
                empty++;
            }

            foreach (var image in file.Images)
            {
                images++;
                frames += image.Frames.Count;
                compression[image.Compression] = compression.GetValueOrDefault(image.Compression) + 1;
                if (image.ColorMap is not null)
                {
                    withColorMap++;
                }

                Assert.Equal(image.FrameCount, image.Frames.Count);
                Assert.All(image.Frames, f => Assert.Equal(image.Width * image.Height, f.Indices.Length));
            }
        }

        // Seven entries are not images at all — a batch file, DOS listings, an executable and
        // friends — so a caller must tolerate a rejected entry.
        Assert.Equal(
            ["ADR.TXT", "ARCH3.EXE", "CEL", "CHECK.TXT", "CW.ERR", "SMP_BAK.BAT", "TMP.LST"],
            strays.Order(StringComparer.Ordinal));

        // BIP.BSI is an authored-empty file: eight bytes holding only an END chunk.
        Assert.Equal(1, empty);
        Assert.Equal(2_621, images);
        Assert.Equal(2_621, withColorMap);
        Assert.Equal(2_085, compression[0]);
        Assert.Equal(521, compression[6]);
        Assert.Equal(15, compression[4]);
        Assert.True(frames > images, $"Some images are animations, so {frames} frames should exceed {images} images.");
    }

    [Fact]
    public void APinnedBsiImage_DecodesWithItsOwnPalette()
    {
        var path = Path.Combine(RequireGameData(), "BSI.BSA");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("BSI.BSA"));

        using var archive = ArchiveReader.Open(path);
        var terrain = BsiFile.Parse(archive.ReadFile("TERR02.BSI")!, "TERR02.BSI");
        var image = Assert.Single(terrain.Images);
        Assert.Equal((128, 128, 1, 0), (image.Width, image.Height, image.FrameCount, image.Compression));

        // A terrain texture uses the whole palette range, which is why CMAP rather than HICL is
        // the palette this reader renders with — HICL only defines the 128 even slots.
        Assert.Contains(image.Frames[0].Indices, index => index > 127);
        Assert.NotNull(image.ColorMap);
        Assert.NotNull(image.HighColor);

        // A compressed animation: twelve 32x64 frames behind the line table.
        var fire = BsiFile.Parse(archive.ReadFile("FIRE10.BSI")!, "FIRE10.BSI");
        var animation = Assert.Single(fire.Images);
        Assert.Equal(6, animation.Compression);
        Assert.Equal(12, animation.Frames.Count);
        Assert.All(animation.Frames, f => Assert.Equal(32 * 64, f.Indices.Length));
        Assert.True(animation.Frames.Select(f => Convert.ToBase64String(f.Indices)).Distinct().Count() > 1,
            "An animation's frames should not all be identical.");
    }

    [Fact]
    public void EveryLevel_ResolvesItsMeshListAndPlacements()
    {
        var path = Path.Combine(RequireGameData(), "BS6.BSA");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("BS6.BSA"));

        using var archive = ArchiveReader.Open(path);
        var entries = archive.ListFiles();
        Assert.Equal(47, entries.Count);

        var levels = 0;
        var meshNames = 0;
        var placements = 0;
        var resolved = 0;
        var lights = 0;
        var flats = 0;
        var rejected = new List<string>();
        foreach (var entry in entries)
        {
            var bytes = archive.ReadFile(entry.FullPath);
            Assert.NotNull(bytes);

            Bs6File level;
            try
            {
                level = Bs6File.Parse(bytes, entry.Name);
            }
            catch (InvalidDataException)
            {
                rejected.Add(entry.Name);
                continue;
            }

            levels++;
            meshNames += level.MeshNames.Count;
            lights += level.Lights.Count;
            flats += level.Flats.Count;
            foreach (var placed in level.Objects)
            {
                placements++;
                if (placed.MeshIndex >= 0 && placed.MeshIndex < level.MeshNames.Count)
                {
                    resolved++;
                }
            }
        }

        // ADR.TXT is a text file; the entry named "C" is truncated and has a second level's GNRL
        // spliced into its light list.
        Assert.Equal(["ADR.TXT", "C"], rejected.Order(StringComparer.Ordinal));

        Assert.Equal(45, levels);
        Assert.Equal(3_500, meshNames);
        Assert.Equal(7_428, placements);
        Assert.Equal(7_426, resolved);
        Assert.Equal(1_499, lights);
        Assert.Equal(2_272, flats);
    }

    [Fact]
    public void APinnedLevel_NamesTheMeshesItPlaces()
    {
        var path = Path.Combine(RequireGameData(), "BS6.BSA");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("BS6.BSA"));

        using var archive = ArchiveReader.Open(path);
        var level = Bs6File.Parse(archive.ReadFile("L8.BS6")!, "L8.BS6");

        Assert.Equal(116, level.MeshNames.Count);
        Assert.Equal(248, level.Objects.Count);
        Assert.Equal(112, level.Flats.Count);
        Assert.Empty(level.Lights);
        Assert.Equal(4, level.ViewCount);
        Assert.Equal(2, level.SnapCount);
        Assert.Equal(403_770, level.Radius);
        Assert.Equal("e:" + "\\" + "projects" + "\\" + "batspire" + "\\" + "art" + "\\" + "tex_cels" + "\\" + "hicolor",
            level.TextureDirectory);

        // Every name in the list is a mesh file that exists beside or inside the mesh archives.
        using var meshes = BattlespireMeshArchive.Open(Path.Combine(RequireGameData(), "3D.BSA"));
        var known = new HashSet<string>(
            Enumerable.Range(0, meshes.Count).Select(i => Path.GetFileNameWithoutExtension(meshes.EntryName(i))),
            StringComparer.OrdinalIgnoreCase);
        foreach (var name in Directory.EnumerateFiles(RequireGameData(), "*.3D"))
        {
            known.Add(Path.GetFileNameWithoutExtension(name));
        }

        var missing = level.MeshNames.Where(n => !known.Contains(n)).ToList();
        Assert.True(missing.Count <= 4, $"Unresolved mesh names in L8: {string.Join(", ", missing)}");

        // The most-placed mesh in this level is the potion pickup.
        var top = level.Objects
            .Where(o => o.MeshIndex < level.MeshNames.Count)
            .GroupBy(o => level.MeshNames[o.MeshIndex])
            .MaxBy(g => g.Count())!;
        Assert.Equal("potion", top.Key);
        Assert.Equal(36, top.Count());
    }

    [Fact]
    public void EveryLevel_AssemblesAgainstTheMeshLibrary()
    {
        var root = RequireGameData();
        var path = Path.Combine(root, "BS6.BSA");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("BS6.BSA"));

        using var meshes = BattlespireMeshLibrary.Open(root);
        Assert.Equal(2_400 + 2_115, meshes.ArchivedCount);
        Assert.Equal(245, meshes.LooseCount);

        using var archive = ArchiveReader.Open(path);
        var levels = 0;
        var placed = 0;
        var resolved = 0;
        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.ListFiles())
        {
            Bs6File level;
            try
            {
                level = Bs6File.Parse(archive.ReadFile(entry.FullPath)!, entry.Name);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            levels++;
            var assembly = Bs6SceneAssembler.Assemble(level, meshes.Resolve);
            placed += assembly.Placed;
            resolved += assembly.Resolved;
            unresolved.UnionWith(assembly.MissingNames);
        }

        Assert.Equal(45, levels);
        Assert.Equal(7_426, placed);

        // 44 names across the whole archive resolve to no record in 3D.BSA, 3D.BS6 or the loose
        // files — l8land2, l8drg1, spike1 and friends are simply not shipped. The assembler
        // reports them rather than dropping the level.
        Assert.Equal(7_265, resolved);
        Assert.Equal(44, unresolved.Count);
        Assert.Contains("l8land2", unresolved);
        Assert.Empty(meshes.Failures);
    }

    [Fact]
    public void APinnedLevel_StandsItsWallRingUpAndTurnsItInward()
    {
        // The mechanism that fixes the ANGS convention. L8's arena is ringed by 14 7volc panels,
        // each 5,120 units long in Z and as little as 0 thick in Y, each placed at ANGS (512,0,0).
        // Reading component 0 as a pitch stands them up; the raw (unnegated) sign turns them in.
        var root = RequireGameData();
        var path = Path.Combine(root, "BS6.BSA");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("BS6.BSA"));

        using var archive = ArchiveReader.Open(path);
        var level = Bs6File.Parse(archive.ReadFile("L8.BS6")!, "L8.BS6");
        using var meshes = BattlespireMeshLibrary.Open(root);
        var assembly = Bs6SceneAssembler.Assemble(level, meshes.Resolve);

        Assert.Equal(248, assembly.Placed);
        Assert.Equal(248, assembly.Resolved);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var instance in assembly.Instances)
        {
            foreach (var vertex in instance.Mesh.SubMeshes.SelectMany(s => s.Vertices))
            {
                var world = Vector3.Transform(vertex.Position, instance.Transform);
                min = Vector3.Min(min, world);
                max = Vector3.Max(max, world);
            }
        }

        // A level is wide and shallow. Its height comes from the stood-up ring: 5,410 against
        // 18,432 and 16,898 on the ground plane.
        Assert.Equal(18_432f, max.X - min.X, 1f);
        Assert.Equal(5_410f, max.Y - min.Y, 1f);
        Assert.Equal(16_898f, max.Z - min.Z, 1f);

        var ring = assembly.Instances
            .Where(i => i.Name.StartsWith("7volc", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(14, ring.Count);

        var centre = new Vector3(777f, 0f, -3_584f);
        foreach (var panel in ring)
        {
            var vertices = panel.Mesh.SubMeshes
                .SelectMany(s => s.Vertices)
                .Select(v => Vector3.Transform(v.Position, panel.Transform))
                .ToList();

            // Standing up puts each panel's 5,120-unit length onto the vertical.
            var height = vertices.Max(v => v.Y) - vertices.Min(v => v.Y);
            Assert.True(height >= 5_000f, $"{panel.Name} is only {height:F0} units tall");

            // ...and its faces look back at the arena, not out of it.
            var normal = Vector3.TransformNormal(
                panel.Mesh.SubMeshes.SelectMany(s => s.Vertices).Aggregate(Vector3.Zero, (sum, v) => sum + v.Normal),
                panel.Transform);
            var outward = new Vector3(panel.Transform.Translation.X - centre.X, 0f,
                panel.Transform.Translation.Z - centre.Z);
            Assert.True(Vector3.Dot(new Vector3(normal.X, 0f, normal.Z), outward) < 0f,
                $"{panel.Name} faces away from the arena");
        }
    }

    [Fact]
    public async Task Analyzer_SynthesizesMeshAndTextRecords()
    {
        var root = RequireGameData();
        var installRoot = Path.GetDirectoryName(root)!;

        var result = await ClassicGameAnalyzer.LoadAsync(installRoot, TestContext.Current.CancellationToken);

        var records = result.Records.GenericRecords;
        Assert.Equal(254, records.Count(r => r.RecordType == BattlespireRecordSource.TextRecordType));

        // 3D.BSA's 2,400 entries minus its five non-mesh strays, plus the 245 loose .3D files.
        Assert.Equal(2_400 - 5 + 245, records.Count(r => r.RecordType == BattlespireRecordSource.MeshRecordType));
        Assert.Equal(records.Count, records.Select(r => r.FormId).Distinct().Count());

        var text = records.First(r => r.RecordType == BattlespireRecordSource.TextRecordType);
        Assert.True((int)text.Fields["Lines"]! > 0);
    }


    [Fact]
    public void EveryFlatSpriteResolvesThroughBsiExceptTheOneThatIsNotASprite()
    {
        // ⛔⛔ The refutation this pins (2026-09-06): flats were recorded as blocked on the undecoded
        // MESH texture mapping. They never were. A mesh carries an unnamed u16 reference; a flat
        // NAMES its sprite, and named BSI images decode. Retail: EVERY flat resolves — 2,272 of
        // 2,272, the flat count this suite already pins.
        // ⚠ A raw regex scan for FILN over the whole file first suggested 47 references to a name
        // `structs` that does not resolve. That was an artefact: FILN also occurs outside FLAT
        // chunks, so the scan counted tags that are not flats at all. Going through the PARSER
        // gives a clean 100%.
        var gameData = RequireGameData();
        var levelsPath = Path.Combine(gameData, "BS6.BSA");
        Assert.SkipWhen(!File.Exists(levelsPath), RealAssetPaths.SkipMessage("BS6.BSA"));
        Assert.SkipWhen(!File.Exists(Path.Combine(gameData, "BSI.BSA")), RealAssetPaths.SkipMessage("BSI.BSA"));

        using var archive = ArchiveReader.Open(levelsPath);
        using var sprites = BattlespireFlatSpriteSource.Open(gameData);

        var references = 0;
        var placed = 0;
        var missing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.ListFiles())
        {
            var bytes = archive.ReadFile(entry.FullPath);
            if (bytes is null)
            {
                continue;
            }

            Bs6File level;
            try
            {
                level = Bs6File.Parse(bytes, entry.Name);
            }
            catch (InvalidDataException)
            {
                continue; // ADR.TXT and the entry "C" are not levels
            }

            var assembly = Bs6SceneAssembler.AssembleFlats(level.Flats, sprites.SizeOf, sprites.Register);
            references += assembly.Placed;
            placed += assembly.Instances.Count;
            foreach (var name in assembly.MissingSprites)
            {
                missing[name] = missing.TryGetValue(name, out var n) ? n + 1 : 1;
            }
        }

        Assert.True(references > 2_200, $"expected ~2,272 flat references, saw {references}");

        // ⚑ Every one resolves. Anything landing in `missing` means the BSI lookup regressed.
        Assert.Empty(missing);
        Assert.Equal(references, placed);
    }


    [Fact]
    public void TheHtblChunkIsASixteenStepLinearLightRamp()
    {
        // ⚑ Settles "believed to be lighting ramps" (2026-09-06). Every BSI entry carrying an HTBL
        // holds exactly 16 tables of 256 colours whose mean luminance INCREASES monotonically, and
        // the step brightnesses track i/15 to within a percent — a linear ramp from near-black to
        // full. ⛔ The old note said 32 tables; it is 16.
        var gameData = RequireGameData();
        var path = Path.Combine(gameData, "BSI.BSA");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("BSI.BSA"));

        using var archive = ArchiveReader.Open(path);
        var withRamp = 0;
        var monotonic = 0;

        foreach (var entry in archive.ListFiles())
        {
            var bytes = archive.ReadFile(entry.FullPath);
            if (bytes is null || !BsiFile.IsBsiFileName(entry.Name))
            {
                continue;
            }

            BsiFile file;
            try
            {
                file = BsiFile.Parse(bytes, entry.Name);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            var table = file.Images.Count > 0 ? file.Images[0].HighColorTable : default;
            if (table.Length == 0)
            {
                continue;
            }

            withRamp++;

            // 16 steps x 256 colours x 2 bytes.
            Assert.Equal(BsiImage.LightRampSteps * BsiImage.LightRampColors * 2, table.Length);

            var span = table.Span;
            var means = new double[BsiImage.LightRampSteps];
            for (var step = 0; step < BsiImage.LightRampSteps; step++)
            {
                double sum = 0;
                for (var c = 0; c < BsiImage.LightRampColors; c++)
                {
                    var v = BinaryPrimitives.ReadUInt16LittleEndian(
                        span.Slice((step * BsiImage.LightRampColors + c) * 2, 2));
                    sum += 0.299 * ((v >> 10) & 0x1F) + 0.587 * ((v >> 5) & 0x1F) + 0.114 * (v & 0x1F);
                }

                means[step] = sum / BsiImage.LightRampColors;
            }

            var rising = true;
            for (var step = 1; step < means.Length; step++)
            {
                if (means[step] < means[step - 1])
                {
                    rising = false;
                    break;
                }
            }

            if (rising)
            {
                monotonic++;
            }
        }

        Assert.True(withRamp > 1_900, $"expected ~1,937 entries with an HTBL, saw {withRamp}");
        Assert.Equal(withRamp, monotonic);
    }


    [Fact]
    public void TheIfhdChunkIsInvariantAcrossEveryRetailImage()
    {
        // ⚑ Settles "the IFHD chunk's two words are unread" (2026-09-06) — and the chunk is ELEVEN
        // dwords, not two. Every one of them is CONSTANT across all 2,015 retail entries that carry
        // an IFHD: dword 0 is 1 and dwords 1-10 are zero.
        // ⚠ That means it carries NO per-file information, so nothing can be derived from it and
        // nothing should depend on it. It does NOT mean the fields are meaningless — retail simply
        // only ever ships one configuration, so the data cannot reveal what they would select.
        var gameData = RequireGameData();
        var path = Path.Combine(gameData, "BSI.BSA");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("BSI.BSA"));

        using var archive = ArchiveReader.Open(path);
        var seen = 0;

        foreach (var entry in archive.ListFiles())
        {
            var bytes = archive.ReadFile(entry.FullPath);
            if (bytes is null || !BsiFile.IsBsiFileName(entry.Name))
            {
                continue;
            }

            var at = FindChunk(bytes, "IFHD"u8);
            if (at < 0)
            {
                continue;
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 4));
            if (length != 44)
            {
                continue;
            }

            seen++;
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 8)));
            for (var d = 1; d < 11; d++)
            {
                Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 8 + d * 4)));
            }
        }

        Assert.True(seen > 2_000, $"expected ~2,015 entries with a 44-byte IFHD, saw {seen}");
    }

    /// <summary>Offset of a top-level BSI chunk tag, or -1. ⚠ BSI chunk lengths are BIG-endian.</summary>
    private static int FindChunk(byte[] bytes, ReadOnlySpan<byte> tag)
    {
        var at = 0;
        while (at + 8 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 4));
            if (bytes.AsSpan(at, 4).SequenceEqual(tag))
            {
                return at;
            }

            if (length > (uint)bytes.Length || at + 8 + length > bytes.Length)
            {
                return -1;
            }

            at += 8 + (int)length;
        }

        return -1;
    }

    [Fact]
    public void TheMagicalItemTableParsesAndOnlyTwoEntriesAreTables()
    {
        // ⚑ Measured 2026-09-06: of the 253 .TXT entries only 22 are tab-keyed and 231 are prose.
        // FOUR pass the item-key gate, not the two the big files suggest: MG2_SPC.TXT (232) and
        // MG0_GEN.TXT (212), plus the small ITEML2.TXT (7) and SITEML2.TXT (6) = 457 records.
        // ⚑ Those two small files are ALSO the entire explanation of the missing ids: they carry
        // no ID# at all, while the two big files have one on every record. 7 + 6 == 13.
        // ⛔ Nine of the tab-keyed entries are dated DEVELOPER LOGS; gating on tabs rather than on
        // the item keys would emit changelog lines as game data, so the gate is checked here too.
        var gameData = RequireGameData();
        var path = Path.Combine(gameData, "TXT.BSA");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("TXT.BSA"));

        using var archive = ArchiveReader.Open(path);
        var tables = 0;
        var records = 0;
        var withoutId = 0;

        foreach (var entry in archive.ListFiles())
        {
            var bytes = archive.ReadFile(entry.FullPath);
            if (bytes is null || !entry.Name.EndsWith(".TXT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = Encoding.Latin1.GetString(bytes);
            if (!BattlespireItemTable.LooksLikeItemTable(text))
            {
                continue;
            }

            tables++;
            foreach (var item in BattlespireItemTable.Parse(text))
            {
                records++;
                Assert.NotEqual(string.Empty, item.Name);
                if (item.Id is null)
                {
                    withoutId++;
                }
            }
        }

        // The four item files pass the gate — the nine dev logs and the prose do not.
        Assert.Equal(4, tables);
        Assert.Equal(457, records);

        // ⚠ Exactly 13 records carry no ID# — the whole of ITEML2 (7) and SITEML2 (6). That is why
        // the parser treats the id as optional and never keys on it.
        Assert.Equal(13, withoutId);
    }
}