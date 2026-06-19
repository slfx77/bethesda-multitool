using System.CommandLine;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Scanning;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.SpeedTree;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Diagnostic commands for SpeedTree <c>.spt</c> ("__IdvSpt_02_") tree files.
/// </summary>
public static class SpeedTreeCommands
{
    public static Command CreateSptCommand()
    {
        var command = new Command("spt", "Inspect SpeedTree .spt tree files");
        command.Subcommands.Add(CreateDumpCommand());
        command.Subcommands.Add(CreateExtractDumpCommand());
        command.Subcommands.Add(CreateRenderCommand());
        command.Subcommands.Add(CreateRenderAllCommand());
        return command;
    }

    private static Command CreateRenderAllCommand()
    {
        var command = new Command("render-all",
            "Render EVERY .spt in a BSA (or directory) to individual PNGs, sharing one texture source");
        var bsaOption = new Option<string?>("--bsa") { Description = "Meshes BSA to enumerate .spt from" };
        var dirOption = new Option<string?>("--dir") { Description = "Directory to enumerate .spt from (instead of --bsa)" };
        var dataOption = new Option<string>("--data")
        { Description = "Texture source(s) — BSA or dir, semicolon-separated", DefaultValueFactory = _ => "" };
        var outOption = new Option<string>("-o", "--output") { Description = "Output directory", Required = true };
        var azimuthOption = new Option<float>("--azimuth") { DefaultValueFactory = _ => 45f };
        var elevationOption = new Option<float>("--elevation") { DefaultValueFactory = _ => 18f };
        var sizeOption = new Option<int>("--size") { DefaultValueFactory = _ => 256 };
        var esmOption = new Option<string?>("--esm")
        {
            Description = "ESM to source each tree's leaf atlas from its TREE.ICON (the authoritative " +
                          "leaf texture; the .spt's own material is a dev-era path that often never shipped).",
        };
        var billboardsOption = new Option<bool>("--billboards")
        { Description = "Also dump each tree's engine billboard (textures\\trees\\billboards\\<name>.dds) for comparison" };
        command.Options.Add(bsaOption);
        command.Options.Add(dirOption);
        command.Options.Add(dataOption);
        command.Options.Add(outOption);
        command.Options.Add(azimuthOption);
        command.Options.Add(elevationOption);
        command.Options.Add(sizeOption);
        command.Options.Add(esmOption);
        command.Options.Add(billboardsOption);
        command.SetAction(parseResult => RenderAll(
            parseResult.GetValue(bsaOption),
            parseResult.GetValue(dirOption),
            parseResult.GetValue(dataOption)!,
            parseResult.GetValue(outOption)!,
            parseResult.GetValue(azimuthOption),
            parseResult.GetValue(elevationOption),
            parseResult.GetValue(sizeOption),
            parseResult.GetValue(esmOption),
            parseResult.GetValue(billboardsOption)));
        return command;
    }

    private static int RenderAll(string? bsa, string? dir, string dataSources, string outDir, float azimuth,
        float elevation, int size, string? esmPath, bool billboards)
    {
        // Enumerate (archivePath, name, bytes) for every .spt in the BSA or directory.
        var items = new List<(string ArchivePath, string Name, byte[] Bytes)>();
        if (!string.IsNullOrEmpty(bsa))
        {
            if (!File.Exists(bsa))
            {
                Console.Error.WriteLine($"BSA not found: {bsa}");
                return 1;
            }

            var archive = BsaParser.Parse(bsa);
            using var extractor = new BsaExtractor(bsa);
            foreach (var rec in archive.AllFiles.Where(f =>
                         f.FullPath?.EndsWith(".spt", StringComparison.OrdinalIgnoreCase) == true))
            {
                try
                {
                    items.Add((SpeedTreeModelPath.ToArchivePath(rec.FullPath!),
                        Path.GetFileNameWithoutExtension(rec.FullPath!), extractor.ExtractFile(rec)));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  extract failed {rec.FullPath}: {ex.Message}");
                }
            }
        }
        else if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.spt", SearchOption.AllDirectories))
            {
                items.Add((SpeedTreeModelPath.ToArchivePath(Path.GetFileName(f)),
                    Path.GetFileNameWithoutExtension(f), File.ReadAllBytes(f)));
            }
        }
        else
        {
            Console.Error.WriteLine("Provide --bsa <archive> or --dir <directory>.");
            return 1;
        }

        // Optional: map each .spt → its TREE metadata from the ESM. ICON is the engine's real leaf atlas;
        // SNAM is the seed the CS/game uses when building the tree/billboard for that TREE record.
        var treeByPath = string.IsNullOrEmpty(esmPath)
            ? new Dictionary<string, TreeMetadata>(StringComparer.OrdinalIgnoreCase)
            : BuildTreeMetadataMap(esmPath);
        if (!string.IsNullOrEmpty(esmPath))
        {
            var seedCount = treeByPath.Values.Count(t => t.Seed.HasValue);
            var leafCount = treeByPath.Values.Count(t => t.LeafTexture is not null);
            Console.WriteLine($"ESM TREE metadata resolved for {treeByPath.Count} tree(s) " +
                              $"({leafCount} ICON leaf atlases, {seedCount} SNAM seeds).");
        }

        Console.WriteLine($"Found {items.Count} .spt files. Rendering to {outDir} ...");
        Directory.CreateDirectory(outDir);
        var bbDir = Path.Combine(outDir, "_billboards");
        if (billboards)
        {
            Directory.CreateDirectory(bbDir);
        }

        var sources = dataSources.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        using var resolver = new NifTextureResolver(sources);

        var azR = azimuth * (float)Math.PI / 180f;
        var elR = elevation * (float)Math.PI / 180f;
        var camDir = new System.Numerics.Vector3(
            (float)(Math.Cos(azR) * Math.Cos(elR)), (float)(Math.Sin(azR) * Math.Cos(elR)), (float)Math.Sin(elR));
        var baseOpt = SptGeometryOptions.FromEnvironment() with { LeafFaceDirection = camDir };

        int ok = 0, fail = 0, textured = 0, bbDumped = 0;
        foreach (var (archivePath, name, bytes) in items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var model = SptFile.Parse(bytes);
                treeByPath.TryGetValue(archivePath, out var treeMeta);
                var opt = baseOpt with { LeafTextureOverride = treeMeta?.LeafTexture };
                var seed = treeMeta?.Seed ?? model.General.Token2005;
                var renderable = SptGeometryBuilder.Build(model, seed, opt);
                var sprite = NifSpriteRenderer.Render(renderable, resolver, 1f, size, size, azimuth, elevation, size);
                if (sprite is null)
                {
                    fail++;
                    continue;
                }

                PngWriter.SaveRgba(sprite.Pixels, sprite.Width, sprite.Height, Path.Combine(outDir, name + ".png"));
                ok++;
                if (sprite.HasTexture)
                {
                    textured++;
                }

                if (billboards &&
                    resolver.GetTexture($@"textures\trees\billboards\{name.ToLowerInvariant()}.dds") is { } bb)
                {
                    PngWriter.SaveRgba(bb.Pixels, bb.Width, bb.Height, Path.Combine(bbDir, name + ".png"));
                    bbDumped++;
                }
            }
            catch (Exception ex)
            {
                fail++;
                Console.Error.WriteLine($"  render failed {name}: {ex.Message}");
            }
        }

        Console.WriteLine($"Done: {ok} rendered ({textured} textured), {fail} failed, {bbDumped} billboards → {outDir}");
        return 0;
    }

    /// <summary>
    ///     CPU-expands the GPU leaf-billboard encoding for verification: each leaf card stores its center
    ///     in the tangent slot and a signed 2D card-space offset in the bitangent slot; this rebuilds the
    ///     positions as <c>center + camRight·off.x + camUp·off.y</c> — exactly what the viewer's
    ///     leaf-billboard vertex shader does — using a camera-facing frame about <paramref name="camDir" />.
    ///     A correct result is identical to the camera-facing still, proving the encoding + billboard math.
    /// </summary>
    private static void ExpandLeafBillboards(NifRenderableModel model, System.Numerics.Vector3 camDir)
    {
        var dir = System.Numerics.Vector3.Normalize(camDir);
        var reference = MathF.Abs(dir.Z) > 0.99f ? System.Numerics.Vector3.UnitX : System.Numerics.Vector3.UnitZ;
        var right = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(reference, dir));
        var up = System.Numerics.Vector3.Cross(dir, right);
        foreach (var sub in model.Submeshes)
        {
            if (!sub.IsLeafBillboard || sub.Tangents is not { } t || sub.Bitangents is not { } b)
            {
                continue;
            }

            var p = sub.Positions;
            for (var i = 0; i < p.Length; i += 3)
            {
                var world = new System.Numerics.Vector3(t[i], t[i + 1], t[i + 2]) + right * b[i] + up * b[i + 1];
                p[i] = world.X;
                p[i + 1] = world.Y;
                p[i + 2] = world.Z;
            }
        }
    }

    private sealed record TreeMetadata(string? LeafTexture, uint? Seed);

    /// <summary>Load an ESM and map each SpeedTree <c>.spt</c> archive path → its TREE metadata.</summary>
    private static Dictionary<string, TreeMetadata> BuildTreeMetadataMap(string esmPath)
    {
        var map = new Dictionary<string, TreeMetadata>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(esmPath))
        {
            Console.Error.WriteLine($"ESM not found: {esmPath}");
            return map;
        }

        var result = EsmFileAnalyzer.AnalyzeAsync(esmPath).GetAwaiter().GetResult();
        if (result.EsmRecords is null)
        {
            return map;
        }

        using var mmf = MemoryMappedFile.CreateFromFile(esmPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        var records = new RecordParser(result.EsmRecords, result.FormIdMap, accessor, result.FileSize).ParseAll();
        foreach (var rec in records.GenericRecords)
        {
            if (rec.ModelPath is not { } mp || !SpeedTreeModelPath.IsSpt(mp))
            {
                continue;
            }

            string? leaf = null;
            if (rec.Fields.TryGetValue("ICON", out var ic) && ic is string icon)
            {
                leaf = SpeedTreeTexturePath.IconToLeafPath(icon);
            }

            map[SpeedTreeModelPath.ToArchivePath(mp)] = new TreeMetadata(leaf, ExtractTreeSeed(rec.Fields));
        }

        return map;
    }

    private static uint? ExtractTreeSeed(Dictionary<string, object?> fields)
    {
        if (!fields.TryGetValue("SNAM", out var snam))
        {
            return null;
        }

        if (snam is uint direct)
        {
            return direct;
        }

        if (snam is Dictionary<string, object?> dict)
        {
            if (dict.TryGetValue("Seed", out var seed) && seed is uint seedValue)
            {
                return seedValue;
            }

            // TREE/SNAM with a single 4-byte payload currently resolves through the generic 4-byte schema.
            if (dict.TryGetValue("Sound FormID", out var legacy) && legacy is uint legacyValue)
            {
                return legacyValue;
            }
        }

        return null;
    }

    private static Command CreateRenderCommand()
    {
        var command = new Command("render",
            "Build procedural geometry from a .spt and CPU-render it to a PNG (textured) for visual verification");
        var fileArg = new Argument<string>("file") { Description = "Path to the .spt file" };
        var outOption = new Option<string>("-o", "--output") { Description = "Output PNG path", Required = true };
        var dataOption = new Option<string>("--data")
        {
            Description = "Texture source dir or BSA (resolves textures\\trees\\...). " +
                          "Default: Sample/Unpacked_Builds/PC_Final_Unpacked/Data",
            DefaultValueFactory = _ => "Sample/Unpacked_Builds/PC_Final_Unpacked/Data",
        };
        var azimuthOption = new Option<float>("--azimuth")
        { Description = "Camera azimuth degrees (0=S,45=NE,90=E)", DefaultValueFactory = _ => 45f };
        var elevationOption = new Option<float>("--elevation")
        { Description = "Camera elevation degrees above horizontal", DefaultValueFactory = _ => 18f };
        var sizeOption = new Option<int>("--size")
        { Description = "Output longest-edge size in px", DefaultValueFactory = _ => 512 };
        var dumpTexOption = new Option<bool>("--dump-textures")
        { Description = "Also save each resolved texture's mip-0 as a PNG next to the output" };
        var bsaOption = new Option<string?>("--bsa")
        { Description = "Read <file> from this BSA archive instead of disk (e.g. an Oblivion Meshes BSA)" };
        var leafTexOption = new Option<string?>("--leaf-texture")
        {
            Description = "Override the leaf atlas (the engine uses TREE.ICON, not the .spt material). " +
                          "Bare name like 'WhiteOakLeaves01.dds' → textures\\trees\\leaves\\..., or a full path.",
        };
        command.Arguments.Add(fileArg);
        command.Options.Add(outOption);
        command.Options.Add(dataOption);
        command.Options.Add(azimuthOption);
        command.Options.Add(elevationOption);
        command.Options.Add(sizeOption);
        command.Options.Add(dumpTexOption);
        command.Options.Add(bsaOption);
        command.Options.Add(leafTexOption);
        command.SetAction(parseResult => RenderSpt(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(outOption)!,
            parseResult.GetValue(dataOption)!,
            parseResult.GetValue(azimuthOption),
            parseResult.GetValue(elevationOption),
            parseResult.GetValue(sizeOption),
            parseResult.GetValue(dumpTexOption),
            parseResult.GetValue(bsaOption),
            parseResult.GetValue(leafTexOption)));
        return command;
    }

    private static int RenderSpt(string sptPath, string outPng, string dataSource, float azimuth, float elevation,
        int size, bool dumpTextures, string? bsa, string? leafTexture)
    {
        var bytes = LoadSptBytes(sptPath, bsa);
        if (bytes is null)
        {
            Console.Error.WriteLine($"File not found: {sptPath}");
            return 1;
        }

        SptModel model;
        try
        {
            model = SptFile.Parse(bytes);
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"Not a valid .spt file: {ex.Message}");
            return 1;
        }

        // Orient leaf cards to face the render camera (a still stand-in for the engine's per-card leaf
        // billboards). Camera direction matches NifSpriteRenderer's az/el basis.
        var azR = azimuth * (float)Math.PI / 180f;
        var elR = elevation * (float)Math.PI / 180f;
        var camDir = new System.Numerics.Vector3(
            (float)(Math.Cos(azR) * Math.Cos(elR)),
            (float)(Math.Sin(azR) * Math.Cos(elR)),
            (float)Math.Sin(elR));
        // FALLOUT_SPT_CROSSED=1 renders the crossed-card path the GUI viewer uses (no per-card
        // camera-facing billboard) instead of the still-friendly camera-facing cards — for diagnosing
        // what the live viewer actually shows.
        var crossed = Environment.GetEnvironmentVariable("FALLOUT_SPT_CROSSED") is "1";
        // FALLOUT_SPT_BILLBOARD=1 exercises the GPU leaf-billboard ENCODING (center in tangent + signed
        // 2D offset in bitangent) and the billboard math the viewer's leaf VS runs, by CPU-expanding each
        // card here with the render camera. A correct result should match the camera-facing still.
        var billboard = Environment.GetEnvironmentVariable("FALLOUT_SPT_BILLBOARD") is "1";
        var opt = SptGeometryOptions.FromEnvironment() with
        {
            LeafFaceDirection = crossed || billboard ? null : camDir,
            LeafBillboard = billboard,
            LeafTextureOverride = SpeedTreeTexturePath.IconToLeafPath(leafTexture),
        };

        var renderable = SptGeometryBuilder.Build(model, model.General.Token2005, opt);
        if (billboard)
        {
            ExpandLeafBillboards(renderable, camDir);
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Built geometry: {renderable.Submeshes.Count} submeshes, bounds W={renderable.Width:F1} H={renderable.Height:F1} D={renderable.Depth:F1}"));
        foreach (var sub in renderable.Submeshes)
        {
            Console.WriteLine(
                $"  [{sub.ShapeName}] verts={sub.Positions.Length / 3} tris={sub.Triangles.Length / 3} tex={sub.DiffuseTexturePath ?? "(none)"}");
        }

        using var resolver = new NifTextureResolver(dataSource);

        if (dumpTextures)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(outPng))!;
            var stem = Path.GetFileNameWithoutExtension(outPng);
            // Also dump the engine's own billboard LOD of this tree (the ground-truth silhouette).
            var bbName = Path.GetFileNameWithoutExtension(sptPath).ToLowerInvariant();
            var paths = renderable.Submeshes.Select(s => s.DiffuseTexturePath)
                .Append($@"textures\trees\billboards\{bbName}.dds");
            foreach (var path in paths.Where(p => p is not null).Distinct())
            {
                var tex = resolver.GetTexture(path!);
                if (tex is null)
                {
                    Console.WriteLine($"  TEX MISS: {path}");
                    continue;
                }

                var safe = path!.Replace('\\', '_').Replace('/', '_');
                var texOut = Path.Combine(dir, $"{stem}__{safe}.png");
                PngWriter.SaveRgba(tex.Pixels, tex.Width, tex.Height, texOut);
                // Alpha stats: % of pixels with alpha < 128 (transparent). A proper cutout atlas is
                // mostly transparent; ~0% means an opaque background → leaf cards render as white blobs.
                var px = tex.Pixels;
                long total = px.Length / 4;
                long b0 = 0, b1 = 0, b2 = 0, b3 = 0; // <32, 32-96, 96-160, >160
                for (var a = 3; a < px.Length; a += 4)
                {
                    var al = px[a];
                    if (al < 32) b0++;
                    else if (al < 96) b1++;
                    else if (al < 160) b2++;
                    else b3++;
                }

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  TEX  {Path.GetFileName(path)} {tex.Width}x{tex.Height}  alpha<32={100.0 * b0 / total:F0}% 32-96={100.0 * b1 / total:F0}% 96-160={100.0 * b2 / total:F0}% >160={100.0 * b3 / total:F0}%"));
            }
        }

        var sprite = NifSpriteRenderer.Render(
            renderable, resolver,
            pixelsPerUnit: 1f, minSize: size, maxSize: size,
            azimuthDeg: azimuth, elevationDeg: elevation, fixedSize: size);
        if (sprite is null)
        {
            Console.Error.WriteLine("Render produced no output (no geometry?).");
            return 1;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPng))!);
        PngWriter.SaveRgba(sprite.Pixels, sprite.Width, sprite.Height, outPng);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Saved {outPng} ({sprite.Width}x{sprite.Height}) hasTexture={sprite.HasTexture} az={azimuth} el={elevation}"));
        return 0;
    }

    private static Command CreateExtractDumpCommand()
    {
        var command = new Command("extract-dump",
            "Extract real engine-generated SpeedTree geometry (BSTreeModel/NiTriShape) from a memory dump");
        var fileArg = new Argument<string>("dump") { Description = "Path to the .dmp memory dump" };
        command.Arguments.Add(fileArg);
        command.SetAction(parseResult => ExtractFromDump(parseResult.GetValue(fileArg)!));
        return command;
    }

    private static int ExtractFromDump(string dumpPath)
    {
        if (!File.Exists(dumpPath))
        {
            Console.Error.WriteLine($"File not found: {dumpPath}");
            return 1;
        }

        Console.WriteLine($"Parsing dump: {Path.GetFileName(dumpPath)} ...");
        var info = MinidumpParser.Parse(dumpPath);
        if (!info.IsValid)
        {
            Console.Error.WriteLine("Not a valid minidump.");
            return 1;
        }

        using var mmf = MemoryMappedFile.CreateFromFile(dumpPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        var accessor = new MmfMemoryAccessor(view);
        var context = new RuntimeMemoryContext(accessor, new FileInfo(dumpPath).Length, info);

        // Find the BSTreeModel vtable via an RTTI census over ALL regions (the default heap window
        // misses the Xbox object pools where BSTreeModel lives).
        Console.WriteLine("Running RTTI census over all regions (this scans the whole dump) ...");
        using var stream = File.OpenRead(dumpPath);
        var census = new RttiReader(info, stream).RunCensus(includeAllRegions: true);
        Console.WriteLine($"Census resolved {census.Count} distinct classes.");

        PrintTreeClassDiagnostic(census);

        var bstm = census.FirstOrDefault(e =>
            e.Rtti.ClassName.Contains("BSTreeModel", StringComparison.OrdinalIgnoreCase));
        if (bstm is null)
        {
            return ScanAllMeshesFallback(context, CultureInfo.InvariantCulture);
        }

        var vtableVa = bstm.Rtti.VtableVA;
        Console.WriteLine($"BSTreeModel vtable @ 0x{vtableVa:X8}  (census instance count: {bstm.InstanceCount})");

        // Scan the heap for objects whose vtable pointer (object+0) == the BSTreeModel vtable.
        var instanceVas = new List<uint>();
        var scanner = new RuntimeObjectScanner(context);
        scanner.ScanAligned(
            (chunk, offset) => BinaryUtils.ReadUInt32BE(chunk, offset) == vtableVa,
            (_, _, fileOffset) =>
            {
                var va = info.FileOffsetToVirtualAddress(fileOffset);
                if (va.HasValue)
                {
                    lock (instanceVas)
                    {
                        instanceVas.Add((uint)va.Value);
                    }
                }
            },
            4);

        Console.WriteLine($"Located {instanceVas.Count} BSTreeModel instance(s).");

        var trees = new RuntimeTreeGeometryExtractor(context).Extract(instanceVas);
        Console.WriteLine($"Extracted geometry from {trees.Count} tree(s):");
        var ci = CultureInfo.InvariantCulture;
        foreach (var tree in trees.OrderByDescending(t => t.TotalVertices))
        {
            var branch = tree.Submeshes.Count(s => s.Kind == TreeGeometryKind.Branch);
            var leaf = tree.Submeshes.Count(s => s.Kind == TreeGeometryKind.Leaf);
            var bb = tree.Submeshes.Count(s => s.Kind == TreeGeometryKind.Billboard);
            Console.WriteLine(string.Create(ci,
                $"  BSTreeModel @ 0x{tree.BSTreeModelVa:X8}: {tree.Submeshes.Count} submeshes (branch {branch}, leaf {leaf}, billboard {bb}), {tree.TotalVertices} verts, {tree.TotalTriangles} tris"));
        }

        return 0;
    }

    private static void PrintTreeClassDiagnostic(IEnumerable<FalloutXbox360Utils.Core.Minidump.CensusEntry> census)
    {
        string[] keys = ["Tree", "Speed", "Billboard", "NiTriShape", "NiNode"];
        foreach (var e in census
                     .Where(e => keys.Any(k => e.Rtti.ClassName.Contains(k, StringComparison.OrdinalIgnoreCase)))
                     .Take(20))
        {
            Console.WriteLine($"  [class] {e.Rtti.ClassName}  @vtable 0x{e.Rtti.VtableVA:X8}  x{e.InstanceCount}");
        }
    }

    // Proto/beta Xbox builds use QueuedTreeModel (a load-queue wrapper), not the retail BSTreeModel.
    // The generated geometry is still standard NiTriShape; prove it's reachable by scanning all meshes
    // and reporting the largest (tree branch/leaf meshes are among the bigger ones).
    private static int ScanAllMeshesFallback(RuntimeMemoryContext context, CultureInfo ci)
    {
        Console.WriteLine("No BSTreeModel (proto build). Scanning all NiTriShape geometry as a fallback ...");
        var meshes = new RuntimeGeometryScanner(context).ScanForMeshes();
        Console.WriteLine($"Geometry scan found {meshes.Count} meshes. Largest 12 by vertex count:");
        foreach (var m in meshes.OrderByDescending(m => m.VertexCount).Take(12))
        {
            Console.WriteLine(string.Create(ci,
                $"  @0x{m.SourceOffset:X}: {m.VertexCount} verts, {m.TriangleCount} tris, bound r={m.BoundRadius:F1} uv={m.UVs != null}"));
        }

        return 0;
    }

    private static Command CreateDumpCommand()
    {
        var command = new Command("dump", "Parse a .spt file and dump its general params, branch splines, and leaf cards");
        var fileArg = new Argument<string>("file") { Description = "Path to the .spt file (or BSA-internal path with --bsa)" };
        var splinesOption = new Option<bool>("--splines")
        {
            Description = "Print every branch spline's control points (verbose)",
        };
        var bsaOption = new Option<string?>("--bsa")
        {
            Description = "Read <file> from this BSA archive instead of disk (e.g. an Oblivion Meshes BSA)",
        };
        command.Arguments.Add(fileArg);
        command.Options.Add(splinesOption);
        command.Options.Add(bsaOption);
        command.SetAction(parseResult => Dump(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(splinesOption),
            parseResult.GetValue(bsaOption)));
        return command;
    }

    /// <summary>Load a <c>.spt</c> from disk, or from a BSA archive when <paramref name="bsa" /> is set.</summary>
    private static byte[]? LoadSptBytes(string path, string? bsa)
    {
        if (string.IsNullOrEmpty(bsa))
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        if (!File.Exists(bsa))
        {
            Console.Error.WriteLine($"BSA not found: {bsa}");
            return null;
        }

        var archive = BsaParser.Parse(bsa);
        using var extractor = new BsaExtractor(bsa);
        var norm = path.Replace('/', '\\');
        var rec = archive.AllFiles.FirstOrDefault(f =>
            string.Equals(f.FullPath?.Replace('/', '\\'), norm, StringComparison.OrdinalIgnoreCase));
        if (rec is null)
        {
            Console.Error.WriteLine($"Entry not found in BSA: {path}");
            return null;
        }

        return extractor.ExtractFile(rec);
    }

    private static int Dump(string path, bool printSplines, string? bsa)
    {
        var bytes = LoadSptBytes(path, bsa);
        if (bytes is null)
        {
            Console.Error.WriteLine($"File not found: {path}");
            return 1;
        }

        SptModel model;
        try
        {
            model = SptFile.Parse(bytes);
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"Not a valid .spt file: {ex.Message}");
            return 1;
        }

        var ci = CultureInfo.InvariantCulture;
        Console.WriteLine($"=== {Path.GetFileName(path)} ===");
        Console.WriteLine("[General]");
        Console.WriteLine($"  BarkTexture : {model.General.BarkTexturePath ?? "(none)"}");
        Console.WriteLine(string.Create(ci,
            $"  Floats      : 2001={model.General.Float2001} 2003={model.General.Float2003} 2006={model.General.Float2006} 2007={model.General.Float2007}"));
        Console.WriteLine(string.Create(ci, $"  Byte2002    : {model.General.Byte2002}   Token2005: {model.General.Token2005}"));
        Console.WriteLine(string.Create(ci, $"  LeafSize    : {model.LeafSize}"));
        if (model.LeafTable is { } lt)
        {
            Console.WriteLine(string.Create(ci,
                $"  LeafTable   : 3007(spacing)={lt.Float3007} 3008(mode)={lt.UInt3008} 3002={lt.Float3002} 3001={lt.UInt3001}"));
        }

        Console.WriteLine($"[Branches] count={model.Branches.Count}");
        for (var i = 0; i < model.Branches.Count; i++)
        {
            var b = model.Branches[i];
            Console.WriteLine(string.Create(ci,
                $"  Branch {i}: u6008={b.UInt6008} u6009={b.UInt6009} f={b.Float6010},{b.Float6011},{b.Float6012},{b.Float6013},{b.Float6014} bools={b.Bool6015},{b.Bool6016}"));
            for (var s = 0; s < b.Splines.Count; s++)
            {
                var sp = b.Splines[s];
                if (sp is null)
                {
                    Console.WriteLine($"    spline[{s}] = (none)");
                    continue;
                }

                Console.WriteLine(string.Create(ci,
                    $"    spline[{s}] header=({sp.Header.X},{sp.Header.Y},{sp.Header.Z}) points={sp.ControlPoints.Count}"));
                if (printSplines)
                {
                    foreach (var cp in sp.ControlPoints)
                    {
                        Console.WriteLine(string.Create(ci,
                            $"        p={cp.Param}  [{cp.A}, {cp.B}, {cp.C}, {cp.D}]"));
                    }
                }
            }
        }

        Console.WriteLine($"[Leaves] count={model.Leaves.Count}");
        for (var i = 0; i < model.Leaves.Count; i++)
        {
            var l = model.Leaves[i];
            Console.WriteLine(string.Create(ci,
                $"  Leaf {i}: type={l.Type} size={l.Size} pos=({l.Position.X},{l.Position.Y},{l.Position.Z}) mat={l.Material ?? "(none)"}"));
            Console.WriteLine(string.Create(ci,
                $"        c0=({l.Corner0.X},{l.Corner0.Y},{l.Corner0.Z}) c1=({l.Corner1.X},{l.Corner1.Y},{l.Corner1.Z}) c2=({l.Corner2.X},{l.Corner2.Y},{l.Corner2.Z}) f4007={l.Float4007}"));
        }

        if (model.Wind is { } wind)
        {
            Console.WriteLine(string.Create(ci, $"[Wind] 5005={wind.Float5005} 5006={wind.Byte5006}"));
        }

        return 0;
    }
}
