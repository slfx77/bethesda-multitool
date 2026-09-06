using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Redguard;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Core.Imaging;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks of the Redguard install (<c>RUN_BUCKET_B=1</c>). <c>WORLD.INI</c> is the
///     master registry the whole game hangs off, so these pin its shape and — the finding that
///     matters for every later reader — exactly which of its references are shipped.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class RedguardRetailTests
{
    private static readonly int[] RetailSampleRates = [11025, 22050];

    private static string RequireDataRoot()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Redguard();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Redguard"));
        return root!;
    }

    [Fact]
    public void WorldIniHasTheRetailShape()
    {
        var registry = RedguardWorldIni.Load(RequireDataRoot());

        Assert.Equal(29, registry.Worlds.Count);

        // The indices are NOT contiguous: 9, 10 and 16 are absent and 99 exists, so a reader that
        // keyed by position rather than by the bracket value would silently misattribute worlds.
        Assert.Equal(
            [0, 1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 13, 14, 15, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 99],
            registry.Worlds.Select(w => w.Index));

        // Every world declares a map and a palette; only the 7 outdoor ones carry terrain and sky.
        Assert.All(registry.Worlds, world => Assert.NotNull(world.MapPath));
        Assert.All(registry.Worlds, world => Assert.NotNull(world.PalettePath));
        Assert.Equal(7, registry.Worlds.Count(w => w.TerrainPath is not null));
        Assert.Equal(7, registry.Worlds.Count(w => w.SkyPath is not null));

        Assert.NotNull(registry.StartWorld);
        Assert.Equal(0, registry.StartWorld!.Index);
    }

    [Fact]
    public void EveryShippedAssetClassResolvesAndTheTwoAbsentOnesNeverDo()
    {
        // The split is by KIND, not by world: .GXA/.RGM/.COL/.WLD all resolve, while every .NOO
        // node map and .BSI sprite is absent from the install AND from the .ROB archives. A caller
        // must treat those two as names only, never as files it can open.
        var root = RequireDataRoot();
        var registry = RedguardWorldIni.Load(root);

        var declared = registry.Worlds
            .SelectMany(w => new[] { w.MapPath, w.TerrainPath, w.PalettePath, w.SkyPath })
            .Where(path => path is not null)
            .Select(path => path!)
            .ToList();

        // The four typed accessors cover 29 maps + 29 palettes + 7 terrains + 7 skies.
        Assert.Equal(72, declared.Count);

        // The rest of the resolvable references are the 29 world_flash_filename .gxa entries, which
        // have no typed accessor — 101 shipped references in total.
        declared.AddRange(registry.Worlds
            .Select(w => w.Values.TryGetValue("world_flash_filename", out var flash) ? flash : null)
            .Where(path => path is not null)
            .Select(path => path!));
        Assert.Equal(101, declared.Count);

        Assert.All(declared, path => Assert.True(
            File.Exists(Path.Combine(root, path.Replace('\\', Path.DirectorySeparatorChar))),
            $"declared asset '{path}' is not shipped"));

        var nodeMaps = registry.Worlds.SelectMany(w => w.NodeMaps).ToList();
        Assert.Equal(119, nodeMaps.Count);
        Assert.All(nodeMaps, path => Assert.False(
            File.Exists(Path.Combine(root, path.Replace('\\', Path.DirectorySeparatorChar))),
            $"node map '{path}' unexpectedly exists — the absence rule has changed"));
    }

    [Fact]
    public void EveryPaletteIsTheEightBitColTheSharedReaderAlreadyHandles()
    {
        // Measured 2026-09-04: all 18 .COL files are 776 bytes, magic 0xB123, version 0, and every
        // component reaches 255 — so Redguard needs NO palette decoder of its own, and none of them
        // is the 6-bit VGA form that would need promoting. `art_pal.col` even shares Daggerfall's
        // name, which is the XnGine lineage showing through again.
        var root = RequireDataRoot();
        var art = Path.Combine(root, "3dart");
        Assert.SkipWhen(!Directory.Exists(art), RealAssetPaths.SkipMessage("Redguard 3dart"));

        var files = Directory.GetFiles(art, "*.COL");
        Assert.Equal(18, files.Length);

        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            Assert.Equal(Palette.ColFileLength, bytes.Length);
            Assert.Equal(Palette.DaggerfallColMagic, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)));

            // The shared reader accepts it and yields a full 256-entry palette.
            var palette = Palette.LoadArenaCol(bytes);
            Assert.Equal(Palette.EntryCount * 4, palette.Rgba.Length);

            // 8-bit, not 6-bit: a promoted 6-bit palette could never exceed 252.
            Assert.Contains(bytes.AsSpan(8).ToArray(), component => component > 252);
        }
    }

    [Fact]
    public async Task AnalyzerSynthesizesAWorldRecordPerWorld()
    {
        var root = RequireDataRoot();

        var result = await ClassicGameAnalyzer.LoadAsync(root, TestContext.Current.CancellationToken);

        var worlds = result.Records.GenericRecords
            .Where(r => r.RecordType == RedguardRecordSource.WorldRecordType)
            .ToList();

        Assert.Equal(29, worlds.Count);
        Assert.Equal(worlds.Count, worlds.Select(r => r.FormId).Distinct().Count());

        var island = worlds.First(r => r.EditorId == "ISLAND");
        Assert.Equal(@"MAPS\ISLAND.rgm", island.Fields["Map"]);
        Assert.True((bool)island.Fields["MapPresent"]!);
        Assert.Equal(@"MAPS\ISLAND.WLD", island.Fields["Terrain"]);
        Assert.Equal(2, island.Fields["RedbookTrack"]);

        // Unmodelled keys survive: 70 indexed keys exist and only a handful are typed.
        Assert.Equal("-63000, 30000, -10000, 28", island.Fields["world_sun"]);
    }

    [Fact]
    public void EveryRobArchiveTilesAndHoldsNothingButThreeDeeMeshes()
    {
        var root = RequireDataRoot();
        var art = Path.Combine(root, "3dart");
        Assert.SkipWhen(!Directory.Exists(art), RealAssetPaths.SkipMessage("Redguard 3dart"));

        var files = Directory.GetFiles(art, "*.ROB");
        Assert.Equal(41, files.Length);

        var segments = 0;
        var empty = 0;
        var versions = new Dictionary<string, int>(StringComparer.Ordinal);
        var namedGrComp = 0;

        foreach (var file in files)
        {
            // Parsing is itself the tiling proof: the probe only succeeds when the OARD chunk
            // length, every forward pointer and the "END " terminator all agree with the walk.
            using var reader = ArchiveReader.Open(file);
            Assert.Equal("ROB (Redguard)", reader.FormatName);

            var entries = reader.ListFiles();
            segments += entries.Count;

            // Names are unique within an archive, so extraction by name is unambiguous.
            Assert.Equal(entries.Count, entries.Select(e => e.FullPath).Distinct(StringComparer.Ordinal).Count());

            if (entries.Any(e => e.Name == "GR_COMP.3D"))
            {
                namedGrComp++;
            }

            foreach (var entry in entries)
            {
                var bytes = reader.ReadFile(entry.FullPath);
                Assert.NotNull(bytes);
                Assert.Equal(entry.Size, bytes!.Length);

                if (bytes.Length == 0)
                {
                    empty++;
                    continue;
                }

                var version = Encoding.ASCII.GetString(bytes, 0, 4);
                versions.TryGetValue(version, out var count);
                versions[version] = count + 1;
            }
        }

        Assert.Equal(5870, segments);
        Assert.Equal(1203, empty);

        // Nothing but meshes, and the two version tags split 4,450 / 217. A v2.6 tag does NOT mean
        // the unsolved .3DC layout — every one of these reproduces its planes' stored normals from
        // the header's OffsetVertexCoors, which is exactly what a .3DC does not do.
        Assert.Equal(new Dictionary<string, int>(StringComparer.Ordinal) { ["v2.7"] = 4450, ["v2.6"] = 217 }, versions);

        // GR_COMP is an ordinary segment name that happens to lead 39 of the 41 archives — it was
        // once misread here as a compression marker, and nothing in a ROB is compressed at all.
        Assert.Equal(39, namedGrComp);
    }

    [Fact]
    public void EveryRobSegmentParsesAsAMeshWithRedguardsEightBytePlaneHeader()
    {
        var root = RequireDataRoot();
        var art = Path.Combine(root, "3dart");
        Assert.SkipWhen(!Directory.Exists(art), RealAssetPaths.SkipMessage("Redguard 3dart"));

        var meshes = 0;
        var planes = 0;
        var failures = new List<string>();

        foreach (var file in Directory.GetFiles(art, "*.ROB"))
        {
            using var archive = RedguardRobMeshArchive.Open(file);
            for (var i = 0; i < archive.Count; i++)
            {
                if (archive.IsEmpty(i))
                {
                    continue;
                }

                if (!archive.TryParse(i, out var mesh, out var error))
                {
                    failures.Add($"{Path.GetFileName(file)}/{archive.EntryName(i)}: {error}");
                    continue;
                }

                meshes++;
                planes += mesh.Planes.Count;
                Assert.Equal(XnGineMeshLayout.Daggerfall, mesh.Layout);
            }
        }

        Assert.Empty(failures);
        Assert.Equal(4667, meshes);
        Assert.Equal(245535, planes);
    }

    [Fact]
    public void EveryAnimatedMeshTilesExactlyAndSplitsIntoTheTwoMeasuredVariants()
    {
        // ⚑ This is the format the MIT reference could not solve — its own comments read "Some old
        // 3DC files have a different vertex start offset for some unknown reason". The frame table
        // removes the guesswork, and PARSING IS THE PROOF: the header, frame block, plane list and
        // every frame's three blocks must cover the file with no overlap, leaving only the one
        // region the frame block declares.
        var root = RequireDataRoot();
        var art = Path.Combine(root, "3dart");
        Assert.SkipWhen(!Directory.Exists(art), RealAssetPaths.SkipMessage("Redguard 3dart"));

        var files = Directory.GetFiles(art, "*.3DC");
        Assert.Equal(147, files.Length);

        var failures = new List<string>();
        int wide = 0, narrow = 0, threeDword = 0, fourDword = 0, frames = 0;

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (!Redguard3dcFile.TryParse(File.ReadAllBytes(file), name, out var mesh, out var error))
            {
                failures.Add(error);
                continue;
            }

            if (mesh.WideFrames) { wide++; } else { narrow++; }
            if (mesh.FrameRecordDwords == 3) { threeDword++; } else { fourDword++; }
            frames += mesh.FrameCount;

            // ⚑ The frame RECORD width predicts the frame width on every retail file: four dwords
            // always means full int32 poses. The reader does not depend on that — it accepts on the
            // tiling — but the two agreeing 147 times is what makes the rule worth stating.
            Assert.Equal(mesh.FrameRecordDwords == 4, mesh.WideFrames);

            // Every pose holds the mesh's full point count, deltas already resolved.
            Assert.All(mesh.Frames, f => Assert.Equal(mesh.KeyframeMesh.Points.Count, f.Points.Count));
        }

        Assert.Empty(failures);
        Assert.Equal((37, 110), (wide, narrow));
        Assert.Equal((110, 37), (threeDword, fourDword));
        Assert.Equal(9190, frames);
    }

    [Fact]
    public void AnAnimatedMeshIsNotWhatTheThreeDeeReaderMakesOfIt()
    {
        // ⛔ The reason .3DC needs its own route rather than a flag: a .3DC is a VALID .3D in its
        // header and plane list, so the .3D reader returns geometry with no error at all — geometry
        // taken from frame 1's offset. The two readings must disagree, or the bug is invisible.
        var root = RequireDataRoot();
        var path = Path.Combine(root, "3dart", "BMANA001.3DC");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("Redguard BMANA001.3DC"));

        var bytes = File.ReadAllBytes(path);
        var animated = Redguard3dcFile.Parse(bytes, "BMANA001.3DC");
        var asPlain3d = XnGineMesh.Parse(bytes, 0, XnGineMeshLayout.Daggerfall);

        Assert.Equal(asPlain3d.Points.Count, animated.KeyframeMesh.Points.Count);
        Assert.NotEqual(asPlain3d.Points[0], animated.KeyframeMesh.Points[0]);

        // What the .3D reader returns is frame 1 — the header's point offset is that frame's entry.
        Assert.Equal(animated.Frames[1].Points[0], asPlain3d.Points[0]);
    }

    [Fact]
    public void EveryGxaWalksAndTheSixCompressedOnesAreReportedNotMisread()
    {
        var root = RequireDataRoot();
        var files = Directory.EnumerateFiles(root, "*.GXA", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.SkipWhen(files.Count == 0, RealAssetPaths.SkipMessage("Redguard GXA"));
        Assert.Equal(65, files.Count);

        var frames = 0;
        var clean = 0;
        var compressed = new List<string>();

        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var gxa = RedguardGxaFile.Parse(File.ReadAllBytes(path), name);

            // Every palette is 6-bit VGA on disk; the reader promotes, so a full-range entry exists.
            Assert.Equal(Palette.EntryCount * 4, gxa.Palette.Rgba.Length);

            frames += gxa.Frames.Count;
            if (gxa.CompressedFrames == 0)
            {
                clean++;
            }
            else
            {
                compressed.Add(name);
            }

            Assert.All(gxa.Frames, frame => Assert.Equal(frame.Width * frame.Height, frame.Indices.Length));
        }

        // 60 of 65 are entirely raw and give 320 frames, 640x480 location art down to 15x15 icons.
        // gui.gxa is among them: its only defect is the BBMP length written as the terminator's
        // absolute offset, and with that honoured all 25 of its frames tile exactly.
        Assert.Equal(60, clean);
        Assert.Equal(320, frames);

        // The other five carry the undecoded compressed frame form. Pinned by name so a future
        // decoder shows up here as a shrinking list rather than as a silent behaviour change.
        Assert.Equal(
            ["GXICONS.GXA", "INVBACK.GXA", "INVMASK.GXA", "pickblob.gxa", "snuff.gxa"],
            compressed.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void MainSfxTilesAndDeclaresDepthPerRecord()
    {
        var root = RequireDataRoot();
        var path = Path.Combine(root, "sound", "MAIN.SFX");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("Redguard MAIN.SFX"));

        var bank = RedguardSfxFile.Parse(File.ReadAllBytes(path), "MAIN.SFX");

        // Parsing IS the tiling proof: FXHD + FXDT + "END " must account for every byte, and the
        // 118 declared 27-byte-headed records must end exactly where FXDT does.
        Assert.Equal(RedguardSfxFile.RetailBanner, bank.Banner);
        Assert.Equal(118, bank.Sounds.Count);
        Assert.Equal(4511529L, bank.Sounds.Sum(s => (long)s.Samples.Length));

        Assert.All(bank.Sounds, s => Assert.Contains(s.SampleRate, RetailSampleRates));
        Assert.All(bank.Sounds, s => Assert.Equal((byte)100, s.Volume));
        Assert.All(bank.Sounds, s => Assert.Contains(s.Flags, new byte[] { 0, 225, 255 }));

        // Depth is per record: 105 are 16-bit signed and 13 are 8-bit unsigned. The 8-bit ones are
        // the only records with an odd byte count (5 of them), their silence byte is 0x80, and the
        // header's first dword is 0 exactly for them — three independent signs agreeing.
        var eightBit = bank.Sounds.Where(s => s.BitsPerSample == 8).ToList();
        Assert.Equal(13, eightBit.Count);
        Assert.Equal(105, bank.Sounds.Count(s => s.BitsPerSample == 16));
        Assert.All(bank.Sounds.Where(s => s.Samples.Length % 2 == 1), s => Assert.Equal(8, s.BitsPerSample));
        Assert.Equal(5, bank.Sounds.Count(s => s.Samples.Length % 2 == 1));
        Assert.All(bank.Sounds, s => Assert.Equal(s.BitsPerSample == 8, s.Format == 0));
        Assert.All(eightBit, s => Assert.InRange(s.Samples.ToArray().Average(b => (double)b), 120, 140));

        // The discriminator that settled each depth — mean |delta| as a fraction of range — must
        // favour the declared reading by a wide margin on a record of each kind.
        var sixteen = bank.Sounds[0].Samples.Span;
        Assert.Equal(16, bank.Sounds[0].BitsPerSample);
        Assert.True(Smoothness16(sixteen) * 5 < Smoothness8(sixteen), "16-bit record is not smoother read as 16-bit");
        var eight = eightBit[0].Samples.Span;
        Assert.True(Smoothness8(eight) * 3 < Smoothness16(eight), "8-bit record is not smoother read as 8-bit");
    }

    [Fact]
    public void EveryRgmTilesAndItsMeshNamesResolveInTheMapsOwnRob()
    {
        var root = RequireDataRoot();
        var maps = Path.Combine(root, "maps");
        Assert.SkipWhen(!Directory.Exists(maps), RealAssetPaths.SkipMessage("Redguard maps"));

        var files = Directory.GetFiles(maps, "*.RGM");
        Assert.Equal(27, files.Length);

        int objects = 0, placements = 0, meshed = 0, statics = 0, lights = 0, flats = 0, markers = 0, ropes = 0;
        int navMaps = 0, nodes = 0, routes = 0, animationMeshes = 0, spheres = 0;
        var unresolved = new List<string>();
        var badDistance = 0;

        foreach (var file in files)
        {
            var stem = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
            var map = RedguardRgmFile.Parse(File.ReadAllBytes(file), Path.GetFileName(file));

            // The 24 engine objects open every table in the same order.
            Assert.Equal(RedguardRgmFile.FixedObjectLabels, map.Objects.Take(24).Select(o => o.Label));

            objects += map.Objects.Count;
            placements += map.Placements.Count;
            meshed += map.Placements.Count(p => p.HasMesh);
            statics += map.StaticMeshes.Count;
            lights += map.Lights.Count;
            flats += map.Flats.Count;
            markers += map.Markers.Count;
            ropes += map.Ropes.Count;
            navMaps += map.NavigationMaps.Count;
            nodes += map.NavigationMaps.Sum(n => n.Nodes.Count);
            animationMeshes += map.AnimationMeshes.Count;
            spheres += map.CollisionSpheres.Count;

            // Every placement names an object of this map, and every mesh — placed, static or
            // rope link — is a segment of the map's own ROB.
            var labels = map.Objects.Select(o => o.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(map.Placements, p => Assert.Contains(p.ObjectName, labels));

            var rob = RedguardRobParser.Parse(Path.Combine(root, "3dart", stem + ".ROB"));
            var segments = rob.Entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            unresolved.AddRange(map.Placements.Where(p => p.HasMesh && !segments.Contains(p.MeshStem)).Select(p => $"{stem} MPOB {p.MeshName}"));
            unresolved.AddRange(map.StaticMeshes.Where(s => !segments.Contains(s.MeshName)).Select(s => $"{stem} MPSO {s.MeshName}"));
            unresolved.AddRange(map.Ropes.Where(r => !segments.Contains(r.LinkMeshName)).Select(r => $"{stem} MPRP {r.LinkMeshName}"));

            // WDNM's route field is exactly the floor of the 3-D distance between its nodes.
            foreach (var graph in map.NavigationMaps)
            {
                foreach (var node in graph.Nodes)
                {
                    foreach (var route in node.Routes)
                    {
                        routes++;
                        var target = graph.Nodes[route.TargetNode];
                        double dx = target.X - node.X, dy = target.Y - node.Y, dz = target.Z - node.Z;
                        if ((int)Math.Floor(Math.Sqrt(dx * dx + dy * dy + dz * dz)) != route.Distance)
                        {
                            badDistance++;
                        }
                    }
                }
            }
        }

        Assert.Empty(unresolved);
        Assert.Equal(0, badDistance);
        Assert.Equal((1664, 3147, 1552, 4161), (objects, placements, meshed, statics));
        Assert.Equal((1925, 956, 215, 29), (lights, flats, markers, ropes));
        Assert.Equal((108, 4323, 93780), (navMaps, nodes, routes));
        Assert.Equal((1107, 8), (animationMeshes, spheres));
    }

    [Fact]
    public void EnglishRtxTilesAndHoldsTheDialogueWithItsVoice()
    {
        var root = RequireDataRoot();
        var path = Path.Combine(root, RedguardRtxFile.FileName);
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("Redguard ENGLISH.RTX"));

        // Opening IS the tiling proof: every index entry must agree with its record header, the
        // records must tile [0, "END ") with no gap, and the trailer must account for the file.
        using var database = RedguardRtxFile.Open(path);

        Assert.Equal(4866, database.Entries.Count);
        Assert.Equal(3933, database.Entries.Count(e => e.IsVoiced));
        Assert.All(database.Entries.Where(e => e.IsVoiced), e => Assert.Contains(e.Sound!.Value.SampleRate, RetailSampleRates));
        Assert.Equal(database.Entries.Count, database.Entries.Select(e => e.Tag).Distinct(StringComparer.Ordinal).Count());

        // The first record is an effect description; the dialogue lines are the on-screen text.
        var bone = database.Find("#bon")!;
        Assert.Equal(("BOATMAN BONE SOUND", 22050, 96548), (bone.Text, bone.Sound!.Value.SampleRate, bone.Sound.Value.ByteLength));
        Assert.Equal(96548, database.ReadSamples(bone).Length);
        Assert.Equal("GET BACK IN YOUR JAR, YOU FILTHY LITTLE THING.", database.Find("zbza")!.Text);

        var torch = database.Find("xtor")!;
        Assert.Equal(("EXAMINE TORCH", false), (torch.Text, torch.IsVoiced));
    }

    [Fact]
    public void EveryWldIsTheFixedEightLayerShape()
    {
        var root = RequireDataRoot();
        var maps = Path.Combine(root, "maps");
        Assert.SkipWhen(!Directory.Exists(maps), RealAssetPaths.SkipMessage("Redguard maps"));

        var files = Directory.GetFiles(maps, "*.WLD").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(["EXTPALAC", "HIDEOUT", "ISLAND", "NECRISLE"], files.Select(f => Path.GetFileNameWithoutExtension(f).ToUpperInvariant()));

        var trailers = new List<string>();
        foreach (var file in files)
        {
            var wld = RedguardWldFile.Parse(File.ReadAllBytes(file), Path.GetFileName(file));
            Assert.Equal([16u, 2u, 2u, 0u, 160u, 1u, 22u, 263416u], wld.Header);
            trailers.Add(string.Join(",", wld.Trailer));
        }

        // The same 16-byte trailer closes all four files.
        Assert.Single(trailers.Distinct());

        // ISLAND uses every layer; layers 1, 5 and 7 are the smooth (height-like) ones, the rest
        // categorical. NECRISLE populates only the first two — the split that showed the layers
        // are attributes of one map rather than tiles of a larger one.
        var island = RedguardWldFile.Parse(File.ReadAllBytes(files[2]), "ISLAND.WLD");
        Assert.All(island.Layers, layer => Assert.Contains(layer.Indices, b => b != 0));
        var smooth = island.Layers.Select((layer, i) => (i, Step: RedguardWldFile.MeanStep(layer)))
            .Where(t => t.Step < RedguardWldFile.SmoothStepThreshold).Select(t => t.i).ToList();
        Assert.Equal([1, 5, 7], smooth);

        var necrisle = RedguardWldFile.Parse(File.ReadAllBytes(files[3]), "NECRISLE.WLD");
        Assert.All(necrisle.Layers.Skip(2), layer => Assert.True(layer.Indices.Count(b => b != 0) < 400));
    }

    [Fact]
    public void EveryThreeDfxTextureSetWalksAndItsFramesAreExact()
    {
        // fxart lives only on the original Disc 1, inside its InstallShield cabinet; this runs
        // against the extracted tree.
        BucketBTestGuard.SkipUnlessEnabled();
        var fxart = Path.Combine(RepositoryRoot(), "Sample", "Full_Builds", "Redguard_Disc1_extracted", "fxart");
        Assert.SkipWhen(!Directory.Exists(fxart), RealAssetPaths.SkipMessage("Redguard Disc 1 fxart"));

        var files = Directory.GetFiles(fxart, "TEXBSI.*");
        Assert.Equal(415, files.Length);

        var images = 0;
        var animated = 0;
        var withPalette = 0;
        var frames = 0;
        foreach (var file in files)
        {
            // Parsing IS the tiling proof: images must run to a nine-NUL terminator with every
            // subrecord accounted for, and each still image's DATA must be exactly width x height.
            var set = RedguardTexBsiFile.Parse(File.ReadAllBytes(file), Path.GetFileName(file));
            images += set.Images.Count;
            animated += set.Images.Count(i => i.IsAnimated);
            withPalette += set.Images.Count(i => i.Palette is not null);
            frames += set.Images.Sum(i => i.Frames.Count);
            Assert.All(set.Images, image => Assert.All(image.Frames, frame =>
                Assert.Equal(image.Width * image.Height, frame.Indices.Length)));
        }

        Assert.Equal(5602, images);
        Assert.Equal(55, animated);

        // Exactly the animated form (IFHD) carries a CMAP; the 5,546 BSIF stills share an
        // external palette. 56 rather than 55 because one CMAP image declares a single frame.
        Assert.Equal(56, withPalette);
        Assert.True(frames > images, $"{frames} frames across {images} images");
    }

    /// <summary>The checkout root: the nearest ancestor of the test binary holding the solution file.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BethesdaMultitool.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("BethesdaMultitool.slnx not found above the test binary.");
    }

    [Fact]
    public void EverySoftwareTextureSetIsDaggerfallsTextureFormat()
    {
        // 3dart\TEXTURE.### was written up as "original RE required"; it is Daggerfall's TEXTURE
        // container (26-byte header + 20-byte record directory + image data), which the existing
        // reader parses outright — and MPSF's archive*128+record packing is that convention too.
        var root = RequireDataRoot();
        var art = Path.Combine(root, "3dart");
        Assert.SkipWhen(!Directory.Exists(art), RealAssetPaths.SkipMessage("Redguard 3dart"));

        var files = Directory.GetFiles(art, "TEXTURE.*").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(418, files.Count);

        var failures = new List<string>();
        var refused = new List<string>();
        var records = 0;
        var frames = 0;
        foreach (var file in files)
        {
            try
            {
                var texture = DaggerfallTextureFile.Parse(File.ReadAllBytes(file), Path.GetFileName(file));
                records += texture.Records.Count;
                frames += texture.Records.Sum(r => r.Frames.Count);
            }
            catch (InvalidDataException e)
            {
                failures.Add($"{Path.GetFileName(file)}: {e.Message}");
            }
            catch (NotSupportedException)
            {
                refused.Add(Path.GetFileName(file).ToUpperInvariant());
            }
        }

        Assert.Empty(failures);

        // ⚑ Redguard ships the SAME three malformed TEXTURE archives Daggerfall does — the ones
        // the decoder refuses by design, and the reference refuses too. Engine lineage, not a
        // reader defect: 415 of the 418 sets decode.
        Assert.Equal(["TEXTURE.215", "TEXTURE.217", "TEXTURE.436"], refused.OrderBy(n => n, StringComparer.Ordinal));
        Assert.True(records > 0 && frames >= records, $"{records} records, {frames} frames");
    }

    [Fact]
    public async Task AnalyzerSynthesizesMapAndObjectRecordsWithUniqueIds()
    {
        var root = RequireDataRoot();

        var result = await ClassicGameAnalyzer.LoadAsync(root, TestContext.Current.CancellationToken);

        var maps = result.Records.GenericRecords.Where(r => r.RecordType == RedguardMapRecordSource.MapRecordType).ToList();
        var objects = result.Records.GenericRecords.Where(r => r.RecordType == RedguardMapRecordSource.ObjectRecordType).ToList();

        Assert.Equal(27, maps.Count);
        Assert.Equal(1664, objects.Count);
        Assert.Equal(4866, result.Records.GenericRecords.Count(r => r.RecordType == RedguardTextRecordSource.TextRecordType));

        // Name-hashed identities must not collide anywhere in the retail set.
        var all = result.Records.GenericRecords.Select(r => r.FormId).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());

        var island = maps.First(r => r.EditorId == "ISLAND");
        Assert.Equal("1, 27, 28", island.Fields["Worlds"]);
        Assert.Equal(268, island.Fields["Objects"]);
        Assert.Equal(972, island.Fields["Placements"]);

        var hideout = maps.First(r => r.EditorId == "HIDEOUT");
        Assert.Equal("(unregistered)", hideout.Fields["Worlds"]);

        // BELL owns a mesh but no script alias (BELLTOWR's RANM holds only gremlin and favis);
        // FAVIS is the NPC whose lowercase alias the RANM span supplies.
        var bell = objects.First(r => r.EditorId == "BELLTOWR_BELL");
        Assert.Null(bell.FullName);
        Assert.Equal(1, bell.Fields["Placements"]);
        Assert.Equal("BELL", bell.Fields["Mesh"]);
        Assert.True((bool)bell.Fields["MeshInRob"]!);

        var favis = objects.First(r => r.EditorId == "BELLTOWR_FAVIS");
        Assert.Equal("favis", favis.FullName);
        Assert.True((bool)favis.Fields["IsActor"]!);
    }

    private static double Smoothness8(ReadOnlySpan<byte> pcm)
    {
        var n = Math.Min(pcm.Length, 20000);
        double sum = 0;
        for (var i = 1; i < n; i++)
        {
            sum += Math.Abs(pcm[i] - pcm[i - 1]);
        }

        return sum / (n - 1) / 255.0;
    }

    private static double Smoothness16(ReadOnlySpan<byte> pcm)
    {
        var n = Math.Min(pcm.Length / 2, 20000);
        double sum = 0;
        for (var i = 1; i < n; i++)
        {
            sum += Math.Abs(
                BinaryPrimitives.ReadInt16LittleEndian(pcm[(i * 2)..]) -
                BinaryPrimitives.ReadInt16LittleEndian(pcm[((i - 1) * 2)..]));
        }

        return sum / (n - 1) / 65535.0;
    }
}
