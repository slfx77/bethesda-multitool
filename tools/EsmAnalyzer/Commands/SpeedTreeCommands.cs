using System.CommandLine;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
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
        return command;
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
        command.Arguments.Add(fileArg);
        command.Options.Add(outOption);
        command.Options.Add(dataOption);
        command.Options.Add(azimuthOption);
        command.Options.Add(elevationOption);
        command.Options.Add(sizeOption);
        command.Options.Add(dumpTexOption);
        command.SetAction(parseResult => RenderSpt(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(outOption)!,
            parseResult.GetValue(dataOption)!,
            parseResult.GetValue(azimuthOption),
            parseResult.GetValue(elevationOption),
            parseResult.GetValue(sizeOption),
            parseResult.GetValue(dumpTexOption)));
        return command;
    }

    private static int RenderSpt(string sptPath, string outPng, string dataSource, float azimuth, float elevation,
        int size, bool dumpTextures)
    {
        if (!File.Exists(sptPath))
        {
            Console.Error.WriteLine($"File not found: {sptPath}");
            return 1;
        }

        SptModel model;
        try
        {
            model = SptFile.Parse(File.ReadAllBytes(sptPath));
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
        var opt = SptGeometryOptions.FromEnvironment() with { LeafFaceDirection = camDir };

        var renderable = SptGeometryBuilder.Build(model, model.General.Token2005, opt);
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
                Console.WriteLine($"  TEX OK  : {path} -> {tex.Width}x{tex.Height} {Path.GetFileName(texOut)}");
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
        var fileArg = new Argument<string>("file") { Description = "Path to the .spt file" };
        var splinesOption = new Option<bool>("--splines")
        {
            Description = "Print every branch spline's control points (verbose)",
        };
        command.Arguments.Add(fileArg);
        command.Options.Add(splinesOption);
        command.SetAction(parseResult => Dump(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(splinesOption)));
        return command;
    }

    private static int Dump(string path, bool printSplines)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"File not found: {path}");
            return 1;
        }

        SptModel model;
        try
        {
            model = SptFile.Parse(File.ReadAllBytes(path));
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
