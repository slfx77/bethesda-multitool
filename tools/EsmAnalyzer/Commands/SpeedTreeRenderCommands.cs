using System.CommandLine;
using System.Globalization;
using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Bsa.Parsing;
using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Rasterization;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.SpeedTree;
using BethesdaMultitool.Core.Utils;

namespace EsmAnalyzer.Commands;

/// <summary>
///     SpeedTree <c>.spt</c> rendering sub-commands: <c>render</c> (single tree → PNG) and
///     <c>render-all</c> (every tree in a BSA/directory → individual PNGs).
/// </summary>
internal static class SpeedTreeRenderCommands
{
    public static Command CreateRenderAllCommand()
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

    /// <summary>
    ///     Build (no render, no textures) every <c>.spt</c> in a BSA/directory and print its LOD section +
    ///     resulting bark/leaf counts — a fast breadth check that the LOD parse + branch decimation + global
    ///     leaf rejection behave across a whole game's trees (e.g. Oblivion's ~113), not just one sample.
    /// </summary>
    public static Command CreateSurveyCommand()
    {
        var command = new Command("survey",
            "Build every .spt in a BSA/dir (no render) and print LOD section + bark/leaf counts");
        var bsaOption = new Option<string?>("--bsa") { Description = "Meshes BSA to enumerate .spt from" };
        var dirOption = new Option<string?>("--dir") { Description = "Directory to enumerate .spt from" };
        var esmOption = new Option<string?>("--esm")
        { Description = "ESM to source the per-tree TREE.SNAM seed (matches the viewer build)" };
        command.Options.Add(bsaOption);
        command.Options.Add(dirOption);
        command.Options.Add(esmOption);
        command.SetAction(parseResult => Survey(
            parseResult.GetValue(bsaOption), parseResult.GetValue(dirOption), parseResult.GetValue(esmOption)));
        return command;
    }

    private static int Survey(string? bsa, string? dir, string? esmPath)
    {
        var items = EnumerateSptItems(bsa, dir);
        if (items is null)
        {
            return 1;
        }

        var treeByPath = string.IsNullOrEmpty(esmPath)
            ? new Dictionary<string, TreeMetadata>(StringComparer.OrdinalIgnoreCase)
            : SpeedTreeMetadata.BuildTreeMetadataMap(esmPath);

        var ci = CultureInfo.InvariantCulture;
        Console.WriteLine($"Surveying {items.Count} .spt ...");
        int withLod = 0, blobs = 0, failed = 0;
        foreach (var (archivePath, name, bytes) in items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var model = SptFile.Parse(bytes);
                treeByPath.TryGetValue(archivePath, out var treeMeta);
                var opt = SptGeometryOptions.FromEnvironment();
                var seed = treeMeta?.Seed ?? model.General.Token2005;
                var renderable = SptGeometryBuilder.Build(model, seed, opt);
                var bark = renderable.Submeshes.FirstOrDefault(s => s.ShapeName == "spt:bark");
                var barkTris = bark is null ? 0 : bark.Triangles.Length / 3;
                var leafCards = renderable.Submeshes.Where(s => s.ShapeName == "spt:leaves")
                    .Sum(s => s.Triangles.Length / 6); // 2 tris per quad card
                var lod = model.Lod;
                if (lod is { NumBranchLods: >= 2 } && lod.BranchNearFraction < 1f)
                {
                    withLod++;
                }
                else if (barkTris > 2000)
                {
                    blobs++; // no usable LOD section AND dense → would render as an over-dense blob
                }

                Console.WriteLine(string.Create(ci,
                    $"  {name,-32} LOD={(lod is null ? "(none)" : $"n={lod.NumBranchLods} near={lod.BranchNearFraction:F2}")}" +
                    $"  barkTris={barkTris,-6} leafCards={leafCards}"));
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"  FAIL {name}: {ex.Message}");
            }
        }

        Console.WriteLine($"Done: {items.Count} trees, {withLod} with branch-LOD decimation, " +
                          $"{blobs} dense-without-LOD, {failed} failed.");
        return 0;
    }

    /// <summary>Enumerate <c>(archivePath, name, bytes)</c> for every <c>.spt</c> in a BSA or directory.</summary>

    private static List<(string ArchivePath, string Name, byte[] Bytes)>? EnumerateSptItems(string? bsa, string? dir)
    {
        var items = new List<(string ArchivePath, string Name, byte[] Bytes)>();
        if (!string.IsNullOrEmpty(bsa))
        {
            if (!File.Exists(bsa))
            {
                Console.Error.WriteLine($"BSA not found: {bsa}");
                return null;
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
            return null;
        }

        return items;
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
            : SpeedTreeMetadata.BuildTreeMetadataMap(esmPath);
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
        var baseOpt = SptGeometryOptions.FromEnvironment() with
        {
            LeafFaceDirection = camDir,
        };

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
    private static void ExpandLeafBillboards(NifRenderableModel model, System.Numerics.Vector3 camDir,
        float windStrength = 0f, float windTime = 0f)
    {
        var dir = System.Numerics.Vector3.Normalize(camDir);
        var reference = MathF.Abs(dir.Z) > 0.99f ? System.Numerics.Vector3.UnitX : System.Numerics.Vector3.UnitZ;
        var right = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(reference, dir));
        var up = System.Numerics.Vector3.Cross(dir, right);
        // Mirror reference_instanced.vert.hlsl's leaf wind EXACTLY so this still validates the shader math.
        var windDir = windStrength > 0f
            ? System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(0.82f, 0.57f, 0f))
            : System.Numerics.Vector3.Zero;
        var phaseScale = new System.Numerics.Vector3(0.03f, 0.027f, 0.05f);
        foreach (var sub in model.Submeshes)
        {
            if (!sub.IsLeafBillboard || sub.Tangents is not { } t || sub.Bitangents is not { } b)
            {
                continue;
            }

            var p = sub.Positions;
            for (var i = 0; i < p.Length; i += 3)
            {
                var center = new System.Numerics.Vector3(t[i], t[i + 1], t[i + 2]);
                if (windStrength > 0f)
                {
                    var windWeight = b[i + 2];
                    var sizeProxy = MathF.Max(MathF.Abs(b[i]), MathF.Abs(b[i + 1]));
                    var phase = windTime * 0.7f + System.Numerics.Vector3.Dot(center, phaseScale);
                    var gust = MathF.Sin(phase) + 0.25f * MathF.Sin(phase * 2.9f + 1.7f);
                    center += windDir * (gust * windStrength * windWeight * sizeProxy);
                }

                var world = center + right * b[i] + up * b[i + 1];
                p[i] = world.X;
                p[i + 1] = world.Y;
                p[i + 2] = world.Z;
            }
        }
    }

    private static float ParseFloatEnv(string name, float fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(raw) &&
               float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;
    }

    public static Command CreateRenderCommand()
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
            Description = "ESM to source TREE.ICON and the TREE.SNAM seed for this .spt.",
        };
        var seedOption = new Option<uint?>("--seed")
        {
            Description = "Override the SpeedTree seed (TREE.SNAM / GECK SpeedTree Seed).",
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
            parseResult.GetValue(seedOption)));
        return command;
    }

    private static int RenderSpt(string sptPath, string outPng, string dataSource, float azimuth, float elevation,
        int size, bool dumpTextures, string? bsa, string? leafTexture, string? esmPath, uint? seedOverride)
    {
        var bytes = SpeedTreeSptIo.LoadSptBytes(sptPath, bsa);
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
            var treeByPath = SpeedTreeMetadata.BuildTreeMetadataMap(esmPath);
            treeMeta = SpeedTreeMetadata.ResolveTreeMetadata(treeByPath, sptPath);
            if (treeMeta is null)
            {
                Console.Error.WriteLine(
                    $"No TREE metadata in {esmPath} matched {string.Join(", ", SpeedTreeMetadata.BuildArchivePathCandidates(sptPath))}.");
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
        };

        var seed = seedOverride ?? treeMeta?.Seed ?? model.General.Token2005;
        var renderable = SptGeometryBuilder.Build(model, seed, opt);
        if (billboard)
        {
            // FALLOUT_SPT_WIND=<strength> (+ optional FALLOUT_SPT_WIND_TIME=<seconds>) bakes the viewer's
            // leaf-wind sway into this still at a fixed phase, so the headless render verifies the exact
            // shader math (reference_instanced.vert.hlsl) — leaves should be displaced along the wind dir.
            var windStrength = ParseFloatEnv("FALLOUT_SPT_WIND", 0f);
            var windTime = ParseFloatEnv("FALLOUT_SPT_WIND_TIME", 2f);
            ExpandLeafBillboards(renderable, camDir, windStrength, windTime);
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Built geometry: seed={seed} {renderable.Submeshes.Count} submeshes, bounds W={renderable.Width:F1} H={renderable.Height:F1} D={renderable.Depth:F1}"));
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
}

