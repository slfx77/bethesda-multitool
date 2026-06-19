using System.CommandLine;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Scanning;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.SpeedTree;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Utils;

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
            var root = Path.GetFullPath(dir);
            foreach (var f in Directory.EnumerateFiles(dir, "*.spt", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(root, f);
                items.Add((SpeedTreeModelPath.ToArchivePath(relativePath),
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
            var heightCount = treeByPath.Values.Count(t => t.TargetHeight.HasValue);
            Console.WriteLine($"ESM TREE metadata resolved for {treeByPath.Count} tree(s) " +
                              $"({leafCount} ICON leaf atlases, {seedCount} SNAM seeds, " +
                              $"{heightCount} OBND/BNAM heights).");
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
                var opt = baseOpt with
                {
                    LeafTextureOverride = treeMeta?.LeafTexture,
                    TargetHeight = treeMeta?.TargetHeight,
                };
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

    private sealed record TreeMetadata(
        string ArchivePath,
        string? EditorId,
        string? LeafTexture,
        uint? Seed,
        float? ObndHeight,
        float? BillboardWidth,
        float? BillboardHeight)
    {
        public float? TargetHeight => Positive(ObndHeight) ?? Positive(BillboardHeight);

        public string DisplayName => string.IsNullOrWhiteSpace(EditorId) ? ArchivePath : EditorId!;

        private static float? Positive(float? value) => value is > 0f ? value : null;
    }

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

            var archivePath = SpeedTreeModelPath.ToArchivePath(mp);
            var (billboardWidth, billboardHeight) = ExtractTreeBillboardSize(rec.Fields, rec.IsBigEndian);
            map[archivePath] = new TreeMetadata(
                archivePath,
                rec.EditorId,
                leaf,
                ExtractTreeSeed(rec.Fields, rec.IsBigEndian),
                ExtractObjectBoundsHeight(rec.Bounds),
                billboardWidth,
                billboardHeight);
        }

        return map;
    }

    private static float? ExtractObjectBoundsHeight(BethesdaMultitool.Core.Formats.Esm.Models.ObjectBounds? bounds)
    {
        if (bounds is null)
        {
            return null;
        }

        var height = bounds.Z2 - bounds.Z1;
        return height > 0 ? height : null;
    }

    private static uint? ExtractTreeSeed(Dictionary<string, object?> fields, bool bigEndian)
    {
        if (!fields.TryGetValue("SNAM", out var snam))
        {
            return null;
        }

        if (TryGetUInt32(snam, out var direct))
        {
            return direct;
        }

        if (snam is Dictionary<string, object?> dict)
        {
            if (dict.TryGetValue("Seed", out var seed) && TryGetUInt32(seed, out var seedValue))
            {
                return seedValue;
            }

            // TREE/SNAM with a single 4-byte payload currently resolves through the generic 4-byte schema.
            if (dict.TryGetValue("Sound FormID", out var legacy) && TryGetUInt32(legacy, out var legacyValue))
            {
                return legacyValue;
            }
        }

        if (snam is byte[] { Length: >= 4 } raw)
        {
            return BinaryUtils.ReadUInt32(raw, 0, bigEndian);
        }

        return null;
    }

    private static (float? Width, float? Height) ExtractTreeBillboardSize(
        Dictionary<string, object?> fields,
        bool bigEndian)
    {
        if (!fields.TryGetValue("BNAM", out var bnam))
        {
            return (null, null);
        }

        if (TryGetNamedFloat(bnam, "Width", out var width) &&
            TryGetNamedFloat(bnam, "Height", out var height))
        {
            return (width, height);
        }

        if (bnam is byte[] { Length: >= 8 } raw)
        {
            return (BinaryUtils.ReadFloat(raw, 0, bigEndian), BinaryUtils.ReadFloat(raw, 4, bigEndian));
        }

        return (null, null);
    }

    private static bool TryGetNamedFloat(object? container, string name, out float value)
    {
        if (container is Dictionary<string, object?> dict && dict.TryGetValue(name, out var raw))
        {
            return TryGetFloat(raw, out value);
        }

        if (container is System.Collections.IDictionary idict && idict.Contains(name))
        {
            return TryGetFloat(idict[name], out value);
        }

        value = 0f;
        return false;
    }

    private static bool TryGetFloat(object? raw, out float value)
    {
        switch (raw)
        {
            case float f:
                value = f;
                return true;
            case double d:
                value = (float)d;
                return true;
            case int i:
                value = i;
                return true;
            case uint u:
                value = u;
                return true;
            default:
                value = 0f;
                return false;
        }
    }

    private static bool TryGetUInt32(object? raw, out uint value)
    {
        switch (raw)
        {
            case uint u:
                value = u;
                return true;
            case int i when i >= 0:
                value = (uint)i;
                return true;
            case ushort us:
                value = us;
                return true;
            case byte b:
                value = b;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private static TreeMetadata? ResolveTreeMetadata(
        IReadOnlyDictionary<string, TreeMetadata> treeByPath,
        string sptPath)
    {
        foreach (var candidate in BuildArchivePathCandidates(sptPath))
        {
            if (treeByPath.TryGetValue(candidate, out var metadata))
            {
                return metadata;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> BuildArchivePathCandidates(string sptPath)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !SpeedTreeModelPath.IsSpt(candidate))
            {
                return;
            }

            var archivePath = SpeedTreeModelPath.ToArchivePath(candidate);
            if (seen.Add(archivePath))
            {
                candidates.Add(archivePath);
            }
        }

        var normalized = sptPath.Replace('/', '\\').Trim();
        Add(normalized);

        const string treeMarker = "\\trees\\";
        var treeIndex = normalized.LastIndexOf(treeMarker, StringComparison.OrdinalIgnoreCase);
        if (treeIndex >= 0)
        {
            Add(normalized[(treeIndex + 1)..]);
        }

        Add(Path.GetFileName(normalized));
        return candidates;
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
        var esmOption = new Option<string?>("--esm")
        {
            Description = "ESM to source TREE.ICON, TREE.SNAM seed, and TREE OBND/BNAM height for this .spt.",
        };
        var seedOption = new Option<uint?>("--seed")
        {
            Description = "Override the SpeedTree seed (TREE.SNAM / GECK SpeedTree Seed).",
        };
        var targetHeightOption = new Option<float?>("--target-height")
        {
            Description = "Override final tree height in model units (TREE billboard/BNAM/OBND-derived height).",
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
        command.Options.Add(esmOption);
        command.Options.Add(seedOption);
        command.Options.Add(targetHeightOption);
        command.SetAction(parseResult => RenderSpt(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(outOption)!,
            parseResult.GetValue(dataOption)!,
            parseResult.GetValue(azimuthOption),
            parseResult.GetValue(elevationOption),
            parseResult.GetValue(sizeOption),
            parseResult.GetValue(dumpTexOption),
            parseResult.GetValue(bsaOption),
            parseResult.GetValue(leafTexOption),
            parseResult.GetValue(esmOption),
            parseResult.GetValue(seedOption),
            parseResult.GetValue(targetHeightOption)));
        return command;
    }

    private static int RenderSpt(string sptPath, string outPng, string dataSource, float azimuth, float elevation,
        int size, bool dumpTextures, string? bsa, string? leafTexture, string? esmPath, uint? seedOverride,
        float? targetHeight)
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
        TreeMetadata? treeMeta = null;
        if (!string.IsNullOrWhiteSpace(esmPath))
        {
            var treeByPath = BuildTreeMetadataMap(esmPath);
            treeMeta = ResolveTreeMetadata(treeByPath, sptPath);
            if (treeMeta is null)
            {
                Console.Error.WriteLine(
                    $"No TREE metadata in {esmPath} matched {string.Join(", ", BuildArchivePathCandidates(sptPath))}.");
            }
            else
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"ESM TREE metadata: {treeMeta.DisplayName} path={treeMeta.ArchivePath} " +
                    $"seed={(treeMeta.Seed?.ToString(CultureInfo.InvariantCulture) ?? "(none)")} " +
                    $"OBNDh={(treeMeta.ObndHeight?.ToString(CultureInfo.InvariantCulture) ?? "(none)")} " +
                    $"BNAM={(treeMeta.BillboardWidth?.ToString(CultureInfo.InvariantCulture) ?? "?")}x{(treeMeta.BillboardHeight?.ToString(CultureInfo.InvariantCulture) ?? "?")} " +
                    $"leaf={treeMeta.LeafTexture ?? "(none)"}"));
            }
        }

        var resolvedLeafTexture = leafTexture is not null
            ? SpeedTreeTexturePath.IconToLeafPath(leafTexture)
            : treeMeta?.LeafTexture;
        var resolvedTargetHeight = targetHeight ?? treeMeta?.TargetHeight;
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
            LeafTextureOverride = resolvedLeafTexture,
            TargetHeight = resolvedTargetHeight,
        };

        var seed = seedOverride ?? treeMeta?.Seed ?? model.General.Token2005;
        var renderable = SptGeometryBuilder.Build(model, seed, opt);
        if (billboard)
        {
            ExpandLeafBillboards(renderable, camDir);
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Built geometry: seed={seed} targetH={(resolvedTargetHeight?.ToString(CultureInfo.InvariantCulture) ?? "(raw)")} {renderable.Submeshes.Count} submeshes, bounds W={renderable.Width:F1} H={renderable.Height:F1} D={renderable.Depth:F1}"));
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
        if (trees.Count == 0)
        {
            PrintBstreeModelFieldDiagnostic(context, instanceVas.Take(8), ci);
        }

        var groups = trees
            .GroupBy(t => new
            {
                Seed = ReadUInt32(context, t.BSTreeModelVa + 0x40),
                RuntimeHeight = ReadFloat(context, t.BSTreeModelVa + 0x4C),
                Branches = t.Submeshes.Count(s => s.Kind == TreeGeometryKind.Branch),
                Leaves = t.Submeshes.Count(s => s.Kind == TreeGeometryKind.Leaf),
                Billboards = t.Submeshes.Count(s => s.Kind == TreeGeometryKind.Billboard),
                Vertices = t.TotalVertices,
                Triangles = t.TotalTriangles,
            })
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key.Vertices)
            .ToList();

        Console.WriteLine($"Unique extracted geometry groups: {groups.Count}");
        foreach (var group in groups.Take(24))
        {
            var tree = group.First();
            var bounds = ComputeBounds(tree.Submeshes);
            Console.WriteLine(string.Create(ci,
                $"  x{group.Count(),3} seed={group.Key.Seed} runtimeH={group.Key.RuntimeHeight:F2} @0x{tree.BSTreeModelVa:X8}: {tree.Submeshes.Count} submeshes (branch {group.Key.Branches}, leaf {group.Key.Leaves}, billboard {group.Key.Billboards}), {group.Key.Vertices} verts, {group.Key.Triangles} tris, bounds size=({bounds.SizeX:F2},{bounds.SizeY:F2},{bounds.SizeZ:F2}) min=({bounds.MinX:F2},{bounds.MinY:F2},{bounds.MinZ:F2}) max=({bounds.MaxX:F2},{bounds.MaxY:F2},{bounds.MaxZ:F2})"));
        }

        if (groups.Count > 24)
        {
            Console.WriteLine($"  ... {groups.Count - 24} more group(s) omitted");
        }

        if (trees.Count > 0 && trees.All(t => t.Submeshes.All(s => s.Kind != TreeGeometryKind.Branch)))
        {
            Console.WriteLine("No branch meshes extracted; branch field diagnostic for first 3 instances:");
            PrintBstreeModelFieldDiagnostic(context, instanceVas.Take(3), ci);
        }

        return 0;
    }

    private static RuntimeBounds ComputeBounds(IEnumerable<ExtractedTreeSubmesh> submeshes)
    {
        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var minZ = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;
        var maxZ = float.MinValue;

        foreach (var submesh in submeshes)
        {
            var vertices = submesh.Mesh.Vertices;
            for (var i = 0; i + 2 < vertices.Length; i += 3)
            {
                var x = vertices[i];
                var y = vertices[i + 1];
                var z = vertices[i + 2];
                if (!RuntimeMemoryContext.IsNormalFloat(x) || !RuntimeMemoryContext.IsNormalFloat(y) ||
                    !RuntimeMemoryContext.IsNormalFloat(z))
                {
                    continue;
                }

                minX = MathF.Min(minX, x);
                minY = MathF.Min(minY, y);
                minZ = MathF.Min(minZ, z);
                maxX = MathF.Max(maxX, x);
                maxY = MathF.Max(maxY, y);
                maxZ = MathF.Max(maxZ, z);
            }
        }

        return minX == float.MaxValue
            ? new RuntimeBounds()
            : new RuntimeBounds(minX, minY, minZ, maxX, maxY, maxZ);
    }

    private static void PrintBstreeModelFieldDiagnostic(RuntimeMemoryContext context, IEnumerable<uint> modelVas,
        CultureInfo ci)
    {
        Console.WriteLine("BSTreeModel field diagnostic (first 8 instances; offsets from PDB/decompile):");
        foreach (var va in modelVas)
        {
            var speedTree = ReadPointer(context, va + 0x0C);
            var branchData = ReadPointer(context, va + 0x14);
            var leafData = ReadPointer(context, va + 0x18);
            var billboard = ReadPointer(context, va + 0x1C);
            var branchProps = ReadPointer(context, va + 0x24);
            var leafProps = ReadPointer(context, va + 0x28);
            var branchCount = ReadArrayCount(context, branchData);
            var leafCount = ReadArrayCount(context, leafData);
            var firstBranch = ReadFirstArrayPointer(context, branchData);
            var firstLeaf = ReadFirstArrayPointer(context, leafData);
            var seed = ReadUInt32(context, va + 0x40);
            var initialized = ReadByte(context, va + 0x44);
            var width = ReadFloat(context, va + 0x48);
            var height = ReadFloat(context, va + 0x4C);

            Console.WriteLine(string.Create(ci,
                $"  @0x{va:X8}: pSpeedTree=0x{speedTree:X8} branchData=0x{branchData:X8}[{branchCount}] first=0x{firstBranch:X8} leafData=0x{leafData:X8}[{leafCount}] first=0x{firstLeaf:X8} billboard=0x{billboard:X8} branchProps=0x{branchProps:X8} leafProps=0x{leafProps:X8} seed={seed} init={initialized} width={width:F2} height={height:F2}"));
            PrintGeometryDataDiagnostic(context, "branch0", firstBranch, ci);
            PrintGeometryDataDiagnostic(context, "leaf0", firstLeaf, ci);
        }
    }

    private static void PrintGeometryDataDiagnostic(RuntimeMemoryContext context, string label, uint dataVa,
        CultureInfo ci)
    {
        if (dataVa == 0 || !context.IsValidPointer(dataVa))
        {
            return;
        }

        var refCount = ReadUInt32(context, dataVa + 0x04);
        var vertices = ReadUInt16(context, dataVa + 0x08);
        var triangles = ReadUInt16(context, dataVa + 0x40);
        var radius = ReadFloat(context, dataVa + 0x1C);
        var vertexPtr = ReadPointer(context, dataVa + 0x20);
        var normalPtr = ReadPointer(context, dataVa + 0x24);
        var uvPtr = ReadPointer(context, dataVa + 0x2C);
        var buffDataPtr = ReadPointer(context, dataVa + 0x34);
        var indexCount = ReadUInt32(context, dataVa + 0x44);
        var indexPtr = ReadPointer(context, dataVa + 0x48);
        var stripCount = ReadUInt16(context, dataVa + 0x44);
        var stripLengthsPtr = ReadPointer(context, dataVa + 0x48);
        var stripListsPtr = ReadPointer(context, dataVa + 0x4C);
        var derivedStripListsPtr = ResolvePackedStripListsPointer(context, buffDataPtr);
        var firstStripLength = ReadUInt16(context, stripLengthsPtr);
        var validPointers =
            $"{context.IsValidPointer(vertexPtr)}/{context.IsValidPointer(normalPtr)}/{context.IsValidPointer(uvPtr)}/{context.IsValidPointer(indexPtr)}";
        var validStripPointers =
            $"{context.IsValidPointer(stripLengthsPtr)}/{context.IsValidPointer(stripListsPtr)}/{context.IsValidPointer(derivedStripListsPtr)}";
        var vertexBounds = ReadPointArrayBounds(context, vertexPtr, vertices);

        Console.WriteLine(string.Create(ci,
            $"      {label} @0x{dataVa:X8}: ref={refCount} verts={vertices} tris={triangles} radius={radius:F2} v/n/uv/i=0x{vertexPtr:X8}/0x{normalPtr:X8}/0x{uvPtr:X8}/0x{indexPtr:X8} valid={validPointers} idxCount={indexCount} buff=0x{buffDataPtr:X8} stripCount={stripCount} stripLen/list/derived=0x{stripLengthsPtr:X8}/0x{stripListsPtr:X8}/0x{derivedStripListsPtr:X8} validStrip={validStripPointers} firstStripLen={firstStripLength} vertexSize=({vertexBounds.SizeX:F2},{vertexBounds.SizeY:F2},{vertexBounds.SizeZ:F2})"));
    }

    private static uint ResolvePackedStripListsPointer(RuntimeMemoryContext context, uint buffDataPtr)
    {
        if (buffDataPtr == 0 || !context.IsValidPointer(buffDataPtr))
        {
            return 0;
        }

        var nestedPtr = ReadPointer(context, buffDataPtr + 0x34);
        if (nestedPtr == 0 || !context.IsValidPointer(nestedPtr))
        {
            return 0;
        }

        var packedAddress = ReadUInt32(context, nestedPtr + 0x18);
        return unchecked((packedAddress & 0x1FFF_FFFFu) +
                         (((packedAddress >> 20) + 0x200u) & 0x1000u) -
                         0x4000_0000u);
    }

    private static RuntimeBounds ReadPointArrayBounds(RuntimeMemoryContext context, uint pointPtr, int pointCount)
    {
        if (pointPtr == 0 || pointCount <= 0 || !context.IsValidPointer(pointPtr))
        {
            return new RuntimeBounds();
        }

        var data = context.ReadBytesAtVa(pointPtr, pointCount * 12);
        if (data is null || data.Length < pointCount * 12)
        {
            return new RuntimeBounds();
        }

        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var minZ = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;
        var maxZ = float.MinValue;
        for (var i = 0; i < pointCount; i++)
        {
            var offset = i * 12;
            var x = BinaryUtils.ReadFloatBE(data, offset);
            var y = BinaryUtils.ReadFloatBE(data, offset + 4);
            var z = BinaryUtils.ReadFloatBE(data, offset + 8);
            if (!RuntimeMemoryContext.IsNormalFloat(x) || !RuntimeMemoryContext.IsNormalFloat(y) ||
                !RuntimeMemoryContext.IsNormalFloat(z))
            {
                continue;
            }

            minX = MathF.Min(minX, x);
            minY = MathF.Min(minY, y);
            minZ = MathF.Min(minZ, z);
            maxX = MathF.Max(maxX, x);
            maxY = MathF.Max(maxY, y);
            maxZ = MathF.Max(maxZ, z);
        }

        return minX == float.MaxValue
            ? new RuntimeBounds()
            : new RuntimeBounds(minX, minY, minZ, maxX, maxY, maxZ);
    }

    private static uint ReadArrayCount(RuntimeMemoryContext context, uint arrayVa)
    {
        return arrayVa != 0 && context.IsValidPointer(arrayVa) && arrayVa >= 4
            ? ReadUInt32(context, arrayVa - 4)
            : 0;
    }

    private static uint ReadFirstArrayPointer(RuntimeMemoryContext context, uint arrayVa)
    {
        return arrayVa != 0 && context.IsValidPointer(arrayVa) ? ReadPointer(context, arrayVa) : 0;
    }

    private static byte ReadByte(RuntimeMemoryContext context, long va)
    {
        var bytes = context.ReadBytesAtVa(va, 1);
        return bytes is null || bytes.Length == 0 ? (byte)0 : bytes[0];
    }

    private static float ReadFloat(RuntimeMemoryContext context, long va)
    {
        var bytes = context.ReadBytesAtVa(va, 4);
        return bytes is null || bytes.Length < 4 ? 0 : BinaryUtils.ReadFloatBE(bytes, 0);
    }

    private static uint ReadPointer(RuntimeMemoryContext context, long va)
    {
        return ReadUInt32(context, va);
    }

    private static uint ReadUInt32(RuntimeMemoryContext context, long va)
    {
        var bytes = context.ReadBytesAtVa(va, 4);
        return bytes is null || bytes.Length < 4 ? 0 : BinaryUtils.ReadUInt32BE(bytes, 0);
    }

    private static ushort ReadUInt16(RuntimeMemoryContext context, long va)
    {
        var bytes = context.ReadBytesAtVa(va, 2);
        return bytes is null || bytes.Length < 2 ? (ushort)0 : BinaryUtils.ReadUInt16BE(bytes, 0);
    }

    private static void PrintTreeClassDiagnostic(IEnumerable<BethesdaMultitool.Core.Minidump.CensusEntry> census)
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

    private readonly record struct RuntimeBounds(
        float MinX,
        float MinY,
        float MinZ,
        float MaxX,
        float MaxY,
        float MaxZ)
    {
        public float SizeX => MaxX - MinX;
        public float SizeY => MaxY - MinY;
        public float SizeZ => MaxZ - MinZ;
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
