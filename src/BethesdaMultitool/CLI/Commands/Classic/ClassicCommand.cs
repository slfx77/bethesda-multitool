using System.CommandLine;
using System.Globalization;
using BethesdaMultitool.CLI.Rendering.Map;
using BethesdaMultitool.Core.Formats.Arena;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Battlespire;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Core.Games;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Classic;

/// <summary>
///     <c>classic</c> command group — reads the pre-plugin-era games (Arena, Daggerfall,
///     Battlespire, Redguard, Fallout 1/2, Fallout Tactics), whose content lives in bespoke
///     containers rather than a plugin record stream. <c>classic text</c> today; the map and
///     audio arms join as their game verticals land.
/// </summary>
public static class ClassicCommand
{
    public static Command Create()
    {
        var command = new Command("classic", "Read classic (pre-Morrowind) game data");
        command.Subcommands.Add(CreateTextCommand());
        command.Subcommands.Add(CreateMapCommand());
        command.Subcommands.Add(CreateMeshCommand());
        command.Subcommands.Add(CreateBlockCommand());
        command.Subcommands.Add(CreateLevelCommand());
        command.Subcommands.Add(CreateExeCommand());
        return command;
    }

    private static Command CreateLevelCommand()
    {
        var command = new Command("level", "Inspect or assemble classic level files (Battlespire BS6.BSA)");
        command.Subcommands.Add(CreateLevelInfoCommand());
        command.Subcommands.Add(CreateLevelExportCommand());
        return command;
    }

    private static Command CreateLevelInfoCommand()
    {
        var command = new Command("info", "Summarize the level archive, or one level with --entry");
        var inputArg = new Argument<string>("input") { Description = "BS6.BSA, a .BS6 file, or a Battlespire install/GAMEDATA directory" };
        var entryOption = new Option<string?>("--entry", "-e") { Description = "Level name inside the archive (e.g. L8.BS6)" };
        command.Arguments.Add(inputArg);
        command.Options.Add(entryOption);
        command.SetAction((parseResult, _) => Guarded(() => RunLevelInfo(
            parseResult.GetValue(inputArg)!,
            parseResult.GetValue(entryOption))));
        return command;
    }

    private static Command CreateLevelExportCommand()
    {
        var command = new Command("export", "Assemble one level's placed meshes into a single GLB");
        var inputArg = new Argument<string>("input") { Description = "BS6.BSA, a .BS6 file, or a Battlespire install/GAMEDATA directory" };
        var entryOption = new Option<string?>("--entry", "-e") { Description = "Level name inside the archive (e.g. L8.BS6)" };
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output directory for the GLB",
            DefaultValueFactory = _ => "TestOutput/classic-levels"
        };
        command.Arguments.Add(inputArg);
        command.Options.Add(entryOption);
        command.Options.Add(outputOption);
        command.SetAction((parseResult, _) => Guarded(() => RunLevelExport(
            parseResult.GetValue(inputArg)!,
            parseResult.GetValue(entryOption),
            parseResult.GetValue(outputOption)!)));
        return command;
    }

    private static void RunLevelExport(string input, string? entryName, string outputDir)
    {
        var path = ResolveLevelPath(input);
        Bs6File level;
        string meshDirectory;
        if (Bs6File.IsBs6FileName(Path.GetFileName(path)))
        {
            level = Bs6File.Parse(File.ReadAllBytes(path), Path.GetFileName(path));
            meshDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        }
        else
        {
            if (entryName is null)
            {
                throw new InvalidOperationException($"{Path.GetFileName(path)} holds many levels — pass --entry <name>.");
            }

            using var archive = ArchiveReader.Open(path);
            var entry = archive.ListFiles().FirstOrDefault(e => e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"No level named '{entryName}' in {Path.GetFileName(path)}.");
            level = Bs6File.Parse(archive.ReadFile(entry.FullPath)!, entry.Name);
            meshDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        }

        using var meshes = BattlespireMeshLibrary.Open(meshDirectory);
        var assembly = Bs6SceneAssembler.Assemble(level, meshes.Resolve);
        if (assembly.Instances.Count == 0)
        {
            throw new InvalidOperationException(
                $"{level.Name} resolved none of its {assembly.Placed} placements against {meshDirectory}.");
        }

        var stem = Path.GetFileNameWithoutExtension(level.Name).ToUpperInvariant();
        var outputPath = Path.Combine(outputDir, stem + ".glb");
        XnGineMeshGlbExporter.WriteScene(stem, assembly.Instances, outputPath);

        PrintLevel(level);
        AnsiConsole.MarkupLine("[green]Wrote[/] {0} [grey]({1} of {2} placements assembled from {3} archived + {4} loose meshes)[/]",
            Markup.Escape(outputPath), assembly.Resolved, assembly.Placed, meshes.ArchivedCount, meshes.LooseCount);
        if (assembly.MissingNames.Count > 0)
        {
            AnsiConsole.MarkupLine("[yellow]{0} mesh name(s) unresolved:[/] {1}",
                assembly.MissingNames.Count,
                Markup.Escape(string.Join(", ", assembly.MissingNames.Take(12))));
        }

        AnsiConsole.MarkupLine(
            "[grey]Untextured: Battlespire's textures live in BSI.BSA, whose 15-bit palette tables are not decoded yet.[/]");
    }

    private static string ResolveLevelPath(string input)
    {
        if (File.Exists(input))
        {
            return input;
        }

        if (!Directory.Exists(input))
        {
            throw new FileNotFoundException($"Input not found: {input}", input);
        }

        foreach (var candidate in new[] { Path.Combine(input, "BS6.BSA"), Path.Combine(input, "GAMEDATA", "BS6.BSA") })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"No BS6.BSA under '{input}' (or its GAMEDATA).", input);
    }

    private static void RunLevelInfo(string input, string? entryName)
    {
        var path = ResolveLevelPath(input);
        if (Bs6File.IsBs6FileName(Path.GetFileName(path)))
        {
            PrintLevel(Bs6File.Parse(File.ReadAllBytes(path), Path.GetFileName(path)));
            return;
        }

        using var archive = ArchiveReader.Open(path);
        var entries = archive.ListFiles();
        if (entryName is not null)
        {
            var entry = entries.FirstOrDefault(e => e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"No level named '{entryName}' in {Path.GetFileName(path)}.");
            PrintLevel(Bs6File.Parse(archive.ReadFile(entry.FullPath)!, entry.Name));
            return;
        }

        var levels = 0;
        var meshNames = 0;
        var objects = 0;
        var lights = 0;
        var flats = 0;
        var failures = new List<string>();
        foreach (var entry in entries)
        {
            var bytes = archive.ReadFile(entry.FullPath);
            if (bytes is null)
            {
                continue;
            }

            try
            {
                var level = Bs6File.Parse(bytes, entry.Name);
                levels++;
                meshNames += level.MeshNames.Count;
                objects += level.Objects.Count;
                lights += level.Lights.Count;
                flats += level.Flats.Count;
            }
            catch (InvalidDataException e)
            {
                failures.Add($"{entry.Name}: {e.Message}");
            }
        }

        AnsiConsole.MarkupLine("[bold cyan]{0}[/] — [grey]{1} entries, {2} levels[/]",
            Markup.Escape(Path.GetFileName(path)), entries.Count, levels);
        AnsiConsole.MarkupLine("[grey]{0:N0} mesh-list names, {1:N0} placed meshes, {2:N0} lights, {3:N0} flats.[/]",
            meshNames, objects, lights, flats);
        foreach (var failure in failures)
        {
            AnsiConsole.MarkupLine("[red]unparsed[/] {0}", Markup.Escape(failure));
        }
    }

    private static void PrintLevel(Bs6File level)
    {
        AnsiConsole.MarkupLine("[bold]{0}[/]", Markup.Escape(level.Name));
        AnsiConsole.MarkupLine("  {0} meshes listed, {1} placed; {2} lights, {3} flats; {4} views, {5} snap grids",
            level.MeshNames.Count, level.Objects.Count, level.Lights.Count, level.Flats.Count, level.ViewCount, level.SnapCount);

        if (level.BoundingBox is { } box)
        {
            AnsiConsole.MarkupLine("  bounds ({0}, {1}, {2}) to ({3}, {4}, {5}); radius {6}; centre ({7}, {8}, {9})",
                box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z,
                level.Radius, level.Center.X, level.Center.Y, level.Center.Z);
        }

        AnsiConsole.MarkupLine("  water {0}, bits 0x{1:X}{2}",
            level.Water, level.Bits,
            level.TextureDirectory is null ? string.Empty : $", authored in {Markup.Escape(level.TextureDirectory)}");

        var placed = level.Objects
            .Where(o => o.MeshIndex >= 0 && o.MeshIndex < level.MeshNames.Count)
            .GroupBy(o => level.MeshNames[o.MeshIndex])
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(12)
            .Select(g => $"{g.Key} x{g.Count()}");
        AnsiConsole.MarkupLine("  most placed: {0}", Markup.Escape(string.Join(", ", placed)));

        var dangling = level.Objects.Count(o => o.MeshIndex < 0 || o.MeshIndex >= level.MeshNames.Count);
        if (dangling > 0)
        {
            AnsiConsole.MarkupLine("  [yellow]{0} placement(s) index past the mesh list[/]", dangling);
        }
    }

    private static Command CreateBlockCommand()
    {
        var command = new Command("block", "Inspect or render classic world blocks (Daggerfall BLOCKS.BSA RMB/RDB)");
        command.Subcommands.Add(CreateBlockInfoCommand());
        command.Subcommands.Add(CreateBlockExportCommand());
        return command;
    }

    private static Command CreateBlockInfoCommand()
    {
        var command = new Command("info", "Summarize the block archive, or one block with --entry");
        var inputArg = new Argument<string>("input") { Description = "BLOCKS.BSA, or a Daggerfall install/data directory" };
        var entryOption = new Option<string?>("--entry", "-e") { Description = "Block name (e.g. WALLAA03.RMB, N0000071.RDB)" };
        command.Arguments.Add(inputArg);
        command.Options.Add(entryOption);
        command.SetAction((parseResult, _) => Guarded(() => RunBlockInfo(
            parseResult.GetValue(inputArg)!,
            parseResult.GetValue(entryOption))));
        return command;
    }

    private static Command CreateBlockExportCommand()
    {
        var command = new Command("export", "Render one block's diagnostic images to PNG (RMB: automap + ground grid; RDB: object plan)");
        var inputArg = new Argument<string>("input") { Description = "BLOCKS.BSA, or a Daggerfall install/data directory" };
        var entryOption = new Option<string>("--entry", "-e") { Description = "Block name", Required = true };
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output directory",
            DefaultValueFactory = _ => "TestOutput/classic-blocks"
        };
        var scaleOption = new Option<int>("--scale")
        {
            Description = "Pixels per automap cell (default 8); ground tiles draw 4x this, dungeon plans are 64x this pixels square",
            DefaultValueFactory = _ => 8
        };
        command.Arguments.Add(inputArg);
        command.Options.Add(entryOption);
        command.Options.Add(outputOption);
        command.Options.Add(scaleOption);
        command.SetAction((parseResult, _) => Guarded(() => RunBlockExport(
            parseResult.GetValue(inputArg)!,
            parseResult.GetValue(entryOption)!,
            parseResult.GetValue(outputOption)!,
            parseResult.GetValue(scaleOption))));
        return command;
    }

    private static string ResolveBlocksPath(string input)
    {
        if (File.Exists(input))
        {
            return input;
        }

        if (!Directory.Exists(input))
        {
            throw new FileNotFoundException($"Input not found: {input}", input);
        }

        foreach (var candidate in new[]
                 {
                     Path.Combine(input, DaggerfallBlocksFile.FileName),
                     Path.Combine(input, "ARENA2", DaggerfallBlocksFile.FileName)
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"No {DaggerfallBlocksFile.FileName} under '{input}' (or its ARENA2).", input);
    }

    private static void RunBlockInfo(string input, string? entryName)
    {
        var path = ResolveBlocksPath(input);
        var blocks = DaggerfallBlocksFile.Open(path);

        if (entryName is not null)
        {
            var index = blocks.IndexOf(entryName);
            if (index < 0)
            {
                throw new InvalidOperationException($"No block named '{entryName}'.");
            }

            PrintBlock(blocks, index);
            return;
        }

        var byType = new Dictionary<DaggerfallBlockType, int>();
        var rmbModels = 0;
        var rmbSubBlocks = 0;
        var rdbObjects = 0;
        var failures = new List<string>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var type = blocks.TypeAt(i);
            byType[type] = byType.GetValueOrDefault(type) + 1;
            try
            {
                switch (type)
                {
                    case DaggerfallBlockType.Rmb:
                        var rmb = blocks.ParseRmb(i);
                        rmbModels += rmb.AllModels.Count();
                        rmbSubBlocks += rmb.SubRecords.Count;
                        break;
                    case DaggerfallBlockType.Rdb:
                        rdbObjects += blocks.ParseRdb(i).AllObjects.Count();
                        break;
                    default:
                        break;
                }
            }
            catch (InvalidDataException e)
            {
                failures.Add($"{blocks.Name(i)}: {e.Message}");
            }
        }

        AnsiConsole.MarkupLine("[bold cyan]{0}[/] — [grey]{1} records[/]", Markup.Escape(Path.GetFileName(path)), blocks.Count);
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Kind").AddColumn("Records", c => c.RightAligned());
        foreach (var (type, count) in byType.OrderBy(kvp => kvp.Key))
        {
            table.AddRow(type.ToString().ToUpperInvariant(), count.ToString("N0", CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]RMB: {0:N0} sub-blocks, {1:N0} placed models. RDB: {2:N0} objects.[/]", rmbSubBlocks, rmbModels, rdbObjects);
        foreach (var failure in failures)
        {
            AnsiConsole.MarkupLine("[red]unparsed[/] {0}", Markup.Escape(failure));
        }
    }

    private static void PrintBlock(DaggerfallBlocksFile blocks, int index)
    {
        var name = blocks.Name(index);
        var type = blocks.TypeAt(index);
        AnsiConsole.MarkupLine("[bold]{0}[/] [grey](record #{1}, {2}, {3:N0} bytes)[/]",
            Markup.Escape(name), index, type.ToString().ToUpperInvariant(), blocks.RecordBytes(index).Length);

        switch (type)
        {
            case DaggerfallBlockType.Rmb:
                PrintRmb(blocks.ParseRmb(index));
                break;
            case DaggerfallBlockType.Rdb:
                PrintRdb(blocks.ParseRdb(index));
                break;
            default:
                break;
        }
    }

    private static void PrintRmb(DaggerfallRmbBlock block)
    {
        AnsiConsole.MarkupLine("  header name [yellow]{0}[/], {1} sub-blocks, {2} loose models, {3} loose flats",
            Markup.Escape(block.HeaderName), block.SubRecords.Count, block.Misc3dObjects.Count, block.MiscFlats.Count);
        for (var i = 0; i < block.SubRecords.Count; i++)
        {
            var sub = block.SubRecords[i];
            var building = block.Buildings[i];
            AnsiConsole.MarkupLine("  [grey]#{0}[/] {1} q{2} at ({3}, {4}) rot {5:F1}° — ext {6} models/{7} flats/{8} doors, int {9} models/{10} flats/{11} people/{12} doors",
                i, building.BuildingType, building.Quality, sub.XPos, sub.ZPos, sub.YRotation / DaggerfallRmbBlock.RotationDivisor,
                sub.Exterior.Models.Count, sub.Exterior.Flats.Count, sub.Exterior.Doors.Count,
                sub.Interior.Models.Count, sub.Interior.Flats.Count, sub.Interior.People.Count, sub.Interior.Doors.Count);
        }

        var modelIds = block.AllModels.Select(m => m.ModelId).Distinct().Order().ToList();
        AnsiConsole.MarkupLine("  model ids ({0}): {1}", modelIds.Count,
            Markup.Escape(string.Join(", ", modelIds.Take(24).Select(id => id.ToString(CultureInfo.InvariantCulture))) + (modelIds.Count > 24 ? ", …" : string.Empty)));
        var textures = block.GroundTiles.Select(t => t.TextureRecord).Distinct().Order().ToList();
        AnsiConsole.MarkupLine("  ground texture records: {0}; scenery on {1} tiles",
            Markup.Escape(string.Join(", ", textures.Select(t => t.ToString(CultureInfo.InvariantCulture)))),
            block.GroundScenery.Count(s => s.HasScenery));
    }

    private static void PrintRdb(DaggerfallRdbBlock block)
    {
        var objects = block.AllObjects.ToList();
        AnsiConsole.MarkupLine("  {0} dungeon block, {1}x{2} object lists ({3} used), DAGR tag '{4}'",
            block.Type, block.Width, block.Height, block.ObjectRoots.Count(r => r.Objects.Count > 0), Markup.Escape(block.ObjectHeader.Dagr));
        AnsiConsole.MarkupLine("  {0} objects: {1} models, {2} flats, {3} lights; {4} actions",
            objects.Count,
            objects.Count(o => o.Type == DaggerfallRdbResourceType.Model),
            objects.Count(o => o.Type == DaggerfallRdbResourceType.Flat),
            objects.Count(o => o.Type == DaggerfallRdbResourceType.Light),
            objects.Count(o => o.Model?.Action is not null));

        var modelIds = objects.Select(o => o.Model)
            .OfType<DaggerfallRdbModelResource>()
            .Select(m => block.ModelReferences[m.ModelIndex])
            .Where(r => r.ModelIdNumber is not null)
            .Select(r => $"{r.ModelId}{(r.Description.Length > 0 ? "/" + r.Description : string.Empty)}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
        AnsiConsole.MarkupLine("  model references ({0}): {1}", modelIds.Count,
            Markup.Escape(string.Join(", ", modelIds.Take(24)) + (modelIds.Count > 24 ? ", …" : string.Empty)));
    }

    private static void RunBlockExport(string input, string entryName, string outputDir, int scale)
    {
        var path = ResolveBlocksPath(input);
        var blocks = DaggerfallBlocksFile.Open(path);
        var index = blocks.IndexOf(entryName);
        if (index < 0)
        {
            throw new InvalidOperationException($"No block named '{entryName}'.");
        }

        Directory.CreateDirectory(outputDir);
        var stem = Path.GetFileNameWithoutExtension(blocks.Name(index)).ToUpperInvariant();
        var written = new List<string>();
        switch (blocks.TypeAt(index))
        {
            case DaggerfallBlockType.Rmb:
                var rmb = blocks.ParseRmb(index);
                var automap = DaggerfallBlockRenderer.RenderAutoMap(rmb, scale);
                var automapPath = Path.Combine(outputDir, stem + "_automap.png");
                PngWriter.SaveRgba(automap.Pixels, automap.Width, automap.Height, automapPath);
                written.Add(automapPath);
                var ground = DaggerfallBlockRenderer.RenderGround(rmb, scale * 4);
                var groundPath = Path.Combine(outputDir, stem + "_ground.png");
                PngWriter.SaveRgba(ground.Pixels, ground.Width, ground.Height, groundPath);
                written.Add(groundPath);
                PrintRmb(rmb);
                break;
            case DaggerfallBlockType.Rdb:
                var rdb = blocks.ParseRdb(index);
                var plan = DaggerfallBlockRenderer.RenderDungeonPlan(rdb, scale * 64);
                var planPath = Path.Combine(outputDir, stem + "_plan.png");
                PngWriter.SaveRgba(plan.Pixels, plan.Width, plan.Height, planPath);
                written.Add(planPath);
                PrintRdb(rdb);
                break;
            default:
                throw new NotSupportedException($"{blocks.Name(index)} is a {blocks.TypeAt(index)} record; only RMB and RDB blocks render.");
        }

        foreach (var file in written)
        {
            AnsiConsole.MarkupLine("[green]Wrote[/] {0}", Markup.Escape(file));
        }
    }

    private static Command CreateMeshCommand()
    {
        var command = new Command("mesh", "Inspect or export classic 3D meshes (Daggerfall ARCH3D.BSA, Battlespire 3D.BSA/3D.BS6/.3D)");
        command.Subcommands.Add(CreateMeshInfoCommand());
        command.Subcommands.Add(CreateMeshExportCommand());
        return command;
    }

    private static Command CreateMeshInfoCommand()
    {
        var command = new Command("info", "Summarize a mesh archive, or one mesh with --entry");
        var inputArg = new Argument<string>("input")
        {
            Description = "ARCH3D.BSA / 3D.BSA / 3D.BS6 / a .3D file, or a game install or data directory"
        };
        var entryOption = new Option<string?>("--entry", "-e")
        {
            Description = "One mesh to describe: a Daggerfall object id, or a Battlespire entry name"
        };
        command.Arguments.Add(inputArg);
        command.Options.Add(entryOption);
        command.SetAction((parseResult, _) => Guarded(() => RunMeshInfo(
            parseResult.GetValue(inputArg)!,
            parseResult.GetValue(entryOption))));
        return command;
    }

    private static Command CreateMeshExportCommand()
    {
        var command = new Command("export", "Export one mesh to GLB (textured when TEXTURE.nnn + ART_PAL.COL sit beside the archive)");
        var inputArg = new Argument<string>("input")
        {
            Description = "ARCH3D.BSA / 3D.BSA / 3D.BS6 / a .3D file, or a game install or data directory"
        };
        var entryOption = new Option<string?>("--entry", "-e")
        {
            Description = "The mesh to export: a Daggerfall object id, or a Battlespire entry name (omit for a loose .3D file)"
        };
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output directory for the GLB",
            DefaultValueFactory = _ => "TestOutput/classic-meshes"
        };
        command.Arguments.Add(inputArg);
        command.Options.Add(entryOption);
        command.Options.Add(outputOption);
        command.SetAction((parseResult, _) => Guarded(() => RunMeshExport(
            parseResult.GetValue(inputArg)!,
            parseResult.GetValue(entryOption),
            parseResult.GetValue(outputOption)!)));
        return command;
    }

    /// <summary>Battlespire's mesh archives, in the order a directory input is searched.</summary>
    private static readonly string[] BattlespireMeshArchives = ["3D.BSA", "3D.BS6"];

    /// <summary>
    ///     Resolves a mesh input to a file: a loose <c>.3D</c>, a mesh archive, or a directory
    ///     holding one (an install root, ARENA2, or GAMEDATA).
    /// </summary>
    private static string ResolveMeshPath(string input)
    {
        if (File.Exists(input))
        {
            return input;
        }

        if (!Directory.Exists(input))
        {
            throw new FileNotFoundException($"Input not found: {input}", input);
        }

        var candidates = new List<string>
        {
            Path.Combine(input, DaggerfallArch3DFile.FileName),
            Path.Combine(input, "ARENA2", DaggerfallArch3DFile.FileName)
        };
        foreach (var archive in BattlespireMeshArchives)
        {
            candidates.Add(Path.Combine(input, archive));
            candidates.Add(Path.Combine(input, "GAMEDATA", archive));
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"No mesh archive under '{input}': looked for {DaggerfallArch3DFile.FileName} and {string.Join("/", BattlespireMeshArchives)} (also under ARENA2 and GAMEDATA).",
            input);
    }

    /// <summary>True when a resolved path is Battlespire's, whose meshes use the 10-byte plane header.</summary>
    private static bool IsBattlespireMeshPath(string path)
    {
        var name = Path.GetFileName(path);
        return IsLooseMeshName(name)
               || BattlespireMeshArchives.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A loose XnGine mesh: Battlespire's <c>.3D</c> or Redguard's <c>.3D</c>/<c>.3DC</c>.</summary>
    private static bool IsLooseMeshName(string name)
    {
        return name.EndsWith(".3D", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".3DC", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Which plane-list layout a loose mesh uses. The extension cannot decide this — Battlespire
    ///     and Redguard both ship <c>.3D</c> files that both label themselves <c>v2.7</c>, yet
    ///     Battlespire's plane header is 10 bytes and Redguard's is 8, the same as Daggerfall's.
    ///     Measured 2026-09-04: all 199 Redguard meshes (52 <c>.3D</c> v2.7, 120 <c>.3DC</c> v2.6, 27
    ///     <c>.3DC</c> v2.7) tile with the 8-byte header and NONE tile with 10, 12, 14 or 16. So the
    ///     owning install decides, resolved by walking up from the file.
    /// </summary>
    private static XnGineMeshLayout ResolveLooseMeshLayout(string path)
    {
        var game = ClassicGameLocator.DetectRootForFile(path)?.Profile.Game;
        return game == BethesdaGame.Battlespire ? XnGineMeshLayout.Battlespire : XnGineMeshLayout.Daggerfall;
    }

    private static void RunMeshInfo(string input, string? entry)
    {
        var path = ResolveMeshPath(input);
        if (IsBattlespireMeshPath(path))
        {
            RunBattlespireMeshInfo(path, entry);
            return;
        }

        uint? objectId = null;
        if (entry is not null)
        {
            objectId = uint.TryParse(entry, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new InvalidOperationException($"'{entry}' is not an object id; {DaggerfallArch3DFile.FileName} records are numbered.");
        }

        var archive = DaggerfallArch3DFile.Open(path);
        AnsiConsole.MarkupLine("[bold cyan]{0}[/] — [grey]{1} records, {2} distinct ids[/]",
            Markup.Escape(Path.GetFileName(path)), archive.Count, archive.DistinctIdCount);

        if (objectId is { } id)
        {
            var index = archive.IndexOf(id);
            if (index < 0)
            {
                throw new InvalidOperationException($"No mesh with object id {id}.");
            }

            PrintMesh(archive.Parse(index), index);
            return;
        }

        var versions = new Dictionary<string, int>(StringComparer.Ordinal);
        var failures = new List<string>();
        var planes = 0;
        for (var i = 0; i < archive.Count; i++)
        {
            if (!archive.TryParse(i, out var mesh, out var error))
            {
                failures.Add($"#{i} id {archive.RecordId(i)}: {error}");
                continue;
            }

            versions[mesh.VersionTag] = versions.GetValueOrDefault(mesh.VersionTag) + 1;
            planes += mesh.Planes.Count;
        }

        var table = new Table().Border(TableBorder.Rounded).AddColumn("Version").AddColumn("Records", c => c.RightAligned());
        foreach (var (tag, count) in versions.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
        {
            table.AddRow(Markup.Escape(tag), count.ToString("N0", CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]{0:N0} planes across the archive; first id {1}, last id {2}.[/]",
            planes, archive.RecordId(0), archive.RecordId(archive.Count - 1));
        foreach (var failure in failures)
        {
            AnsiConsole.MarkupLine("[red]unparsed[/] {0}", Markup.Escape(failure));
        }
    }

    private static void PrintMesh(XnGineMesh mesh, int index)
    {
        var decomposed = XnGineMeshDecomposer.Decompose(mesh);
        var size = mesh.Size;
        AnsiConsole.MarkupLine("[bold]Mesh {0}[/] [grey](record #{1}, {2})[/]", mesh.ObjectId, index, Markup.Escape(mesh.VersionTag));
        AnsiConsole.MarkupLine("  points {0}, planes {1}, triangles {2}, object-data entries {3}",
            mesh.Points.Count, mesh.Planes.Count, decomposed.TriangleCount, mesh.ObjectDataCount);
        AnsiConsole.MarkupLine("  radius {0:F2}, size {1:F2} x {2:F2} x {3:F2} units",
            mesh.RadiusUnits, size.X, size.Y, size.Z);
        AnsiConsole.MarkupLine("  textures: {0}", Markup.Escape(string.Join(", ",
            mesh.UniqueTextures.Select(t => string.Create(CultureInfo.InvariantCulture, $"TEXTURE.{t.Archive:D3}#{t.Record}")))));

        var polygonSizes = mesh.Planes.GroupBy(p => p.Points.Count).OrderBy(g => g.Key)
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key}-gon x{g.Count()}"));
        AnsiConsole.MarkupLine("  polygons: {0}", Markup.Escape(string.Join(", ", polygonSizes)));
    }

    /// <summary>Describes one Battlespire mesh, or censuses its archive.</summary>
    private static void RunBattlespireMeshInfo(string path, string? entry)
    {
        var name = Path.GetFileName(path);
        if (IsLooseMeshName(name))
        {
            PrintBattlespireMesh(name, BattlespireMeshArchive.ParseLoose(
                File.ReadAllBytes(path), name, ResolveLooseMeshLayout(path)));
            return;
        }

        using var archive = BattlespireMeshArchive.Open(path);
        if (entry is not null)
        {
            var index = archive.IndexOf(entry);
            if (index < 0)
            {
                throw new InvalidOperationException($"No mesh named '{entry}' in {name}.");
            }

            PrintBattlespireMesh(archive.EntryName(index), archive.Parse(index));
            return;
        }

        var planes = 0;
        var points = 0;
        var failures = new List<string>();
        for (var i = 0; i < archive.Count; i++)
        {
            if (!archive.TryParse(i, out var mesh, out var error))
            {
                failures.Add($"{archive.EntryName(i)}: {error}");
                continue;
            }

            planes += mesh.Planes.Count;
            points += mesh.Points.Count;
        }

        AnsiConsole.MarkupLine("[bold cyan]{0}[/] — [grey]{1:N0} meshes, {2:N0} points, {3:N0} planes[/]",
            Markup.Escape(name), archive.Count, points, planes);
        foreach (var failure in failures)
        {
            AnsiConsole.MarkupLine("[red]unparsed[/] {0}", Markup.Escape(failure));
        }
    }

    private static void PrintBattlespireMesh(string name, XnGineMesh mesh)
    {
        var decomposed = XnGineMeshDecomposer.Decompose(mesh);
        var size = mesh.Size;
        AnsiConsole.MarkupLine("[bold]{0}[/] [grey]({1}, Battlespire layout)[/]", Markup.Escape(name), Markup.Escape(mesh.VersionTag));
        AnsiConsole.MarkupLine("  points {0}, planes {1}, triangles {2}, textures {3}",
            mesh.Points.Count, mesh.Planes.Count, decomposed.TriangleCount, mesh.UniqueTextures.Count);
        AnsiConsole.MarkupLine("  radius {0:F2}, size {1:F2} x {2:F2} x {3:F2} units", mesh.RadiusUnits, size.X, size.Y, size.Z);

        var polygonSizes = mesh.Planes.GroupBy(p => p.Points.Count).OrderBy(g => g.Key)
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key}-gon x{g.Count()}"));
        AnsiConsole.MarkupLine("  polygons: {0}", Markup.Escape(string.Join(", ", polygonSizes)));
    }

    /// <summary>Exports one Battlespire mesh to GLB. Its textures live in BSI.BSA, which is not decoded yet.</summary>
    private static void RunBattlespireMeshExport(string path, string? entry, string outputDir)
    {
        var name = Path.GetFileName(path);
        XnGineMesh mesh;
        string stem;
        if (IsLooseMeshName(name))
        {
            mesh = BattlespireMeshArchive.ParseLoose(
                File.ReadAllBytes(path), name, ResolveLooseMeshLayout(path));
            stem = Path.GetFileNameWithoutExtension(name);
        }
        else
        {
            if (entry is null)
            {
                throw new InvalidOperationException($"{name} holds many meshes — pass --entry <name>.");
            }

            using var archive = BattlespireMeshArchive.Open(path);
            var index = archive.IndexOf(entry);
            if (index < 0)
            {
                throw new InvalidOperationException($"No mesh named '{entry}' in {name}.");
            }

            mesh = archive.Parse(index);
            stem = Path.GetFileNameWithoutExtension(archive.EntryName(index));
        }

        var decomposed = XnGineMeshDecomposer.Decompose(mesh);
        var outputPath = Path.Combine(outputDir, stem.ToUpperInvariant() + ".glb");
        XnGineMeshGlbExporter.Write(decomposed, outputPath);

        PrintBattlespireMesh(name, mesh);
        AnsiConsole.MarkupLine("[green]Wrote[/] {0} [grey](untextured: Battlespire's textures live in BSI.BSA, which is not decoded yet)[/]",
            Markup.Escape(outputPath));
    }

    private static void RunMeshExport(string input, string? entry, string outputDir)
    {
        var path = ResolveMeshPath(input);
        if (IsBattlespireMeshPath(path))
        {
            RunBattlespireMeshExport(path, entry, outputDir);
            return;
        }

        if (entry is null || !uint.TryParse(entry, NumberStyles.None, CultureInfo.InvariantCulture, out var objectId))
        {
            throw new InvalidOperationException($"Pass --entry <object id>; {DaggerfallArch3DFile.FileName} records are numbered.");
        }

        var archive = DaggerfallArch3DFile.Open(path);
        var index = archive.IndexOf(objectId);
        if (index < 0)
        {
            throw new InvalidOperationException($"No mesh with object id {objectId}.");
        }

        var mesh = archive.Parse(index);
        var decomposed = XnGineMeshDecomposer.Decompose(mesh);
        var dataRoot = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var textures = new DaggerfallMeshTextureSource(dataRoot);
        var textured = 0;
        var outputPath = Path.Combine(outputDir, objectId.ToString(CultureInfo.InvariantCulture) + ".glb");

        XnGineMeshGlbExporter.Write(decomposed, outputPath, (textureArchive, record) =>
        {
            var png = textures.Resolve(textureArchive, record);
            if (png is not null)
            {
                textured++;
            }

            return png;
        });

        PrintMesh(mesh, index);
        AnsiConsole.MarkupLine("[green]Wrote[/] {0} [grey]({1} of {2} materials textured)[/]",
            Markup.Escape(outputPath), textured, decomposed.SubMeshes.Count);
    }

    private static Command CreateExeCommand()
    {
        var command = new Command("exe", "Unpack a compressed classic game executable (Arena A.EXE)");
        var inputArg = new Argument<string>("input") { Description = "The packed executable" };
        var outputOption = new Option<string?>("--output", "-o")
        {
            Description = "Where to write the unpacked image (default: alongside the input, .unpacked.exe)"
        };
        var infoOnlyOption = new Option<bool>("--info")
        {
            Description = "Report sizes without writing the unpacked image"
        };
        command.Arguments.Add(inputArg);
        command.Options.Add(outputOption);
        command.Options.Add(infoOnlyOption);
        command.SetAction((parseResult, _) => Guarded(() => RunExe(
            parseResult.GetValue(inputArg)!,
            parseResult.GetValue(outputOption),
            parseResult.GetValue(infoOnlyOption))));
        return command;
    }

    private static void RunExe(string input, string? output, bool infoOnly)
    {
        if (!File.Exists(input))
        {
            throw new FileNotFoundException($"Input not found: {input}", input);
        }

        var name = Path.GetFileName(input);
        var bytes = File.ReadAllBytes(input);

        if (!ArenaExeUnpacker.LooksPacked(bytes))
        {
            throw new InvalidDataException(
                $"'{name}' does not look PKLITE-packed (no 0xFFFF terminator before its trailer). " +
                "An already-unpacked executable needs no processing.");
        }

        var declared = ArenaExeUnpacker.ReadDeclaredSize(bytes);
        var unpacked = ArenaExeUnpacker.Unpack(bytes, name);

        AnsiConsole.MarkupLine("[bold cyan]{0}[/]", Markup.Escape(name));

        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Property");
        table.AddColumn(new TableColumn("Value").RightAligned());
        table.AddRow("Packed size", $"{bytes.Length:N0} bytes");
        table.AddRow("Declared size", $"{declared:N0} bytes");
        table.AddRow("Unpacked size", $"{unpacked.Length:N0} bytes");
        table.AddRow("Expansion", $"{(double)unpacked.Length / bytes.Length:F2}x");
        AnsiConsole.Write(table);

        if (infoOnly)
        {
            return;
        }

        var target = output ?? Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".",
            Path.GetFileNameWithoutExtension(input) + ".unpacked.exe");

        var directory = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(target, unpacked);
        AnsiConsole.MarkupLine("[green]Wrote[/] {0}", Markup.Escape(target));
    }

    private static Command CreateMapCommand()
    {
        var command = new Command("map", "Inspect or export classic voxel maps (Arena .MIF / .RMD)");
        command.Subcommands.Add(CreateMapInfoCommand());
        command.Subcommands.Add(CreateMapExportCommand());
        return command;
    }

    private static Command CreateMapInfoCommand()
    {
        var command = new Command("info", "Show a map's dimensions, levels and chunk inventory");
        var inputArg = new Argument<string>("input") { Description = "A .MIF or .RMD file" };
        var entryOption = new Option<string?>("--entry", "-e")
        {
            Description = "Virtual path of the map inside an archive input"
        };
        command.Arguments.Add(inputArg);
        command.Options.Add(entryOption);
        command.SetAction((parseResult, _) => Guarded(() => RunMapInfo(
            parseResult.GetValue(inputArg)!,
            parseResult.GetValue(entryOption))));
        return command;
    }

    private static Command CreateMapExportCommand()
    {
        var command = new Command("export", "Render a map's voxel layers to PNG (one image per layer)");
        var inputArg = new Argument<string>("input") { Description = "A .MIF or .RMD file, or an archive with --entry" };
        var entryOption = new Option<string?>("--entry", "-e")
        {
            Description = "Virtual path of the map inside an archive input"
        };
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output directory for PNG layers",
            DefaultValueFactory = _ => "TestOutput/classic-maps"
        };
        var scaleOption = new Option<int>("--scale")
        {
            Description = "Pixels per voxel (default 4)",
            DefaultValueFactory = _ => 4
        };
        command.Arguments.Add(inputArg);
        command.Options.Add(entryOption);
        command.Options.Add(outputOption);
        command.Options.Add(scaleOption);
        command.SetAction((parseResult, _) => Guarded(() => RunMapExport(
            parseResult.GetValue(inputArg)!,
            parseResult.GetValue(entryOption),
            parseResult.GetValue(outputOption)!,
            parseResult.GetValue(scaleOption))));
        return command;
    }

    private static Task<int> Guarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
                                       or NotSupportedException)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] {0}", Markup.Escape(ex.Message));
            return Task.FromResult(1);
        }

        return Task.FromResult(0);
    }

    private static (byte[] Bytes, string Name) LoadMapSource(string input, string? entryName)
    {
        if (!File.Exists(input))
        {
            throw new FileNotFoundException($"Input not found: {input}", input);
        }

        if (entryName is null)
        {
            return (File.ReadAllBytes(input), Path.GetFileName(input));
        }

        using var archive = ArchiveReader.Open(input);
        var bytes = archive.ReadFile(entryName)
                    ?? throw new FileNotFoundException(
                        $"Entry '{entryName}' not found in {Path.GetFileName(input)} " +
                        $"({archive.FormatName}, {archive.TotalFiles} files).");
        return (bytes, Path.GetFileName(entryName.Replace('/', '\\')));
    }

    private static void RunMapInfo(string input, string? entryName)
    {
        var (bytes, name) = LoadMapSource(input, entryName);

        if (DaggerfallWoodsFile.IsWoodsFileName(name))
        {
            var woods = DaggerfallWoodsFile.Parse(bytes, name);
            var heights = woods.HeightMap.Span;
            var max = 0;
            var sea = 0;
            foreach (var h in heights)
            {
                max = Math.Max(max, h);
                if (h <= 3)
                {
                    sea++;
                }
            }

            AnsiConsole.MarkupLine(
                "[bold cyan]{0}[/] — Daggerfall world heightmap {1}x{2}, elevation 0..{3}, {4:P0} at sea level, 5x5 sub-grid per pixel",
                Markup.Escape(name),
                DaggerfallWoodsFile.Width,
                DaggerfallWoodsFile.Height,
                max,
                (double)sea / heights.Length);
            return;
        }

        if (DaggerfallPakFile.IsPakFileName(name))
        {
            var pak = DaggerfallPakFile.Parse(bytes, name);
            var values = pak.Values.Distinct().Order().ToList();
            AnsiConsole.MarkupLine(
                "[bold cyan]{0}[/] — Daggerfall world overlay {1}x{2}, {3} distinct value(s): {4}",
                Markup.Escape(name),
                DaggerfallPakFile.Width,
                DaggerfallPakFile.Height,
                values.Count,
                string.Join(", ", values));
            return;
        }

        if (name.EndsWith(".RMD", StringComparison.OrdinalIgnoreCase))
        {
            var chunk = ArenaRmdFile.Parse(bytes, name);
            AnsiConsole.MarkupLine(
                "[bold cyan]{0}[/] — wilderness chunk {1}x{2}, {3}",
                Markup.Escape(name),
                ArenaRmdFile.Width,
                ArenaRmdFile.Depth,
                chunk.WasCompressed ? "word-RLE compressed" : "stored uncompressed");
            AnsiConsole.MarkupLine(
                "  [grey]distinct voxels — FLOR {0}, MAP1 {1}, MAP2 {2}[/]",
                chunk.Floor.Distinct().Count(),
                chunk.Map1.Distinct().Count(),
                chunk.Map2.Distinct().Count());
            return;
        }

        var map = ArenaMifFile.Parse(bytes, name);
        AnsiConsole.MarkupLine(
            "[bold cyan]{0}[/] — {1}x{2} voxels, {3} level(s), starts on level {4}",
            Markup.Escape(map.Name),
            map.Width,
            map.Depth,
            map.Levels.Count,
            map.StartingLevelIndex);

        if (map.DeclaredLevelCount != map.Levels.Count)
        {
            AnsiConsole.MarkupLine(
                "  [yellow]header declares {0} level(s); {1} were actually present[/]",
                map.DeclaredLevelCount,
                map.Levels.Count);
        }

        var starts = map.StartPoints.Where(p => !p.IsUnset).ToList();
        if (starts.Count > 0)
        {
            AnsiConsole.MarkupLine("  [grey]start points:[/] {0}",
                string.Join(", ", starts.Select(p => $"({p.X}, {p.Y})")));
        }

        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Level");
        table.AddColumn("Name");
        table.AddColumn("INF");
        table.AddColumn(new TableColumn("Floors").RightAligned());
        table.AddColumn(new TableColumn("Locks").RightAligned());
        table.AddColumn(new TableColumn("Triggers").RightAligned());
        table.AddColumn("Layers");

        for (var i = 0; i < map.Levels.Count; i++)
        {
            var level = map.Levels[i];
            var layers = new List<string>();
            if (level.Floor.Length > 0)
            {
                layers.Add("FLOR");
            }

            if (level.Map1.Length > 0)
            {
                layers.Add("MAP1");
            }

            if (level.Map2.Length > 0)
            {
                layers.Add("MAP2");
            }

            layers.AddRange(level.UndecodedChunks.Keys.Select(k => $"{k}?"));

            table.AddRow(
                i.ToString(),
                Markup.Escape(level.LevelName ?? "—"),
                Markup.Escape(level.InfoFile ?? "—"),
                level.FloorTextureCount.ToString(),
                level.Locks.Count.ToString(),
                level.Triggers.Count.ToString(),
                string.Join(" ", layers));
        }

        AnsiConsole.Write(table);

        var withText = map.Levels.SelectMany(l => l.Triggers).Count(t => t.HasText);
        var withSound = map.Levels.SelectMany(l => l.Triggers).Count(t => t.HasSound);
        if (withText + withSound > 0)
        {
            AnsiConsole.MarkupLine(
                "[grey]{0} trigger(s) reference *TEXT, {1} reference @SOUND (both in the level's .INF).[/]",
                withText,
                withSound);
        }
    }

    private static void RunMapExport(string input, string? entryName, string outputDir, int scale)
    {
        var (bytes, name) = LoadMapSource(input, entryName);

        IReadOnlyList<ArenaMapRenderer.RenderedLayer> layers;
        if (DaggerfallWoodsFile.IsWoodsFileName(name))
        {
            layers = [DaggerfallMapRenderer.RenderHeightMap(DaggerfallWoodsFile.Parse(bytes, name), outputDir, scale)];
        }
        else if (DaggerfallPakFile.IsPakFileName(name))
        {
            layers = [DaggerfallMapRenderer.RenderOverlay(DaggerfallPakFile.Parse(bytes, name), outputDir, scale)];
        }
        else if (name.EndsWith(".RMD", StringComparison.OrdinalIgnoreCase))
        {
            layers = ArenaMapRenderer.RenderRmd(ArenaRmdFile.Parse(bytes, name), name, outputDir, scale);
        }
        else
        {
            layers = ArenaMapRenderer.RenderMif(ArenaMifFile.Parse(bytes, name), outputDir, scale);
        }

        AnsiConsole.MarkupLine("[green]Wrote {0} layer image(s)[/]", layers.Count);
        foreach (var layer in layers)
        {
            AnsiConsole.MarkupLine(
                "  {0}  [grey]{1} {2}x{3}, {4} distinct voxel id(s)[/]",
                Markup.Escape(layer.Path),
                layer.Layer,
                layer.Width,
                layer.Height,
                layer.DistinctVoxels);
        }
    }

    private static Command CreateTextCommand()
    {
        var command = new Command(
            "text",
            "Dump a classic game's authored text (Arena: TEMPLATE.DAT strings + .INF on-screen text; " +
            "Daggerfall: TEXT.RSC strings + BOOKS)");
        var inputArg = new Argument<string>("input")
        {
            Description = "Install/data directory, or a single TEMPLATE.DAT, .INF, TEXT.RSC, BOKnnnnn.TXT or .QRC file"
        };
        var filterOption = new Option<string?>("--filter", "-f")
        {
            Description = "Only show entries whose text or name contains this substring (case-insensitive)"
        };
        var sourceOption = new Option<string>("--source", "-s")
        {
            Description = "Which sources to read: template, inf (Arena), text, books, quests (Daggerfall), or all",
            DefaultValueFactory = _ => "all"
        };
        var limitOption = new Option<int>("--limit", "-l")
        {
            Description = "Maximum entries to print (0 = no limit)",
            DefaultValueFactory = _ => 0
        };

        command.Arguments.Add(inputArg);
        command.Options.Add(filterOption);
        command.Options.Add(sourceOption);
        command.Options.Add(limitOption);
        command.SetAction((parseResult, _) =>
        {
            try
            {
                Run(
                    parseResult.GetValue(inputArg)!,
                    parseResult.GetValue(sourceOption)!,
                    parseResult.GetValue(filterOption),
                    parseResult.GetValue(limitOption));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
                                           or NotSupportedException)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] {0}", Markup.Escape(ex.Message));
                return Task.FromResult(1);
            }

            return Task.FromResult(0);
        });
        return command;
    }

    private static readonly string[] TextSources = ["all", "template", "inf", "text", "books", "quests"];

    private static void Run(string input, string source, string? filter, int limit)
    {
        if (Array.IndexOf(TextSources, source) < 0)
        {
            throw new InvalidOperationException($"Unknown --source '{source}'. Use {string.Join(", ", TextSources)}.");
        }

        var printed = 0;

        if (File.Exists(input))
        {
            PrintFile(input, filter, limit, ref printed);
            WriteFooter(printed);
            return;
        }

        if (!Directory.Exists(input))
        {
            throw new FileNotFoundException($"Input not found: {input}", input);
        }

        var (profile, root) = DetectInstall(Path.GetFullPath(input));
        AnsiConsole.MarkupLine("[bold cyan]{0}[/] — [grey]{1}[/]", profile.Game, Markup.Escape(root));
        AnsiConsole.WriteLine();

        switch (profile.Game)
        {
            case BethesdaGame.Arena:
                RunArena(root, source is "all" or "template", source is "all" or "inf", filter, limit, ref printed);
                break;
            case BethesdaGame.Daggerfall:
                RunDaggerfall(Path.Combine(root, profile.ClassicLooseRoot), source is "all" or "text",
                    source is "all" or "books", source is "all" or "quests", filter, limit, ref printed);
                break;
            default:
                throw new NotSupportedException(
                    $"'classic text' does not read {profile.Game} yet — its text formats land with its game vertical. " +
                    "Arena and Daggerfall are supported today.");
        }

        WriteFooter(printed);
    }

    /// <summary>
    ///     Resolves the install a directory belongs to. Markers are install-root relative, and the
    ///     data directory (Daggerfall's ARENA2) is the natural thing to pass, so the parent is
    ///     tried when the directory itself is a profile's loose root.
    /// </summary>
    private static (GameProfile Profile, string Root) DetectInstall(string directory)
    {
        if (ClassicGameLocator.DetectFromDirectory(directory) is { } profile)
        {
            return (profile, directory);
        }

        var parent = Path.GetDirectoryName(directory);
        if (parent is not null
            && ClassicGameLocator.DetectFromDirectory(parent) is { } parentProfile
            && string.Equals(Path.GetFileName(directory), parentProfile.ClassicLooseRoot, StringComparison.OrdinalIgnoreCase))
        {
            return (parentProfile, parent);
        }

        throw new InvalidOperationException(
            $"'{directory}' is not a recognizable classic game install (no profile's markers matched).");
    }

    private static void PrintFile(string input, string? filter, int limit, ref int printed)
    {
        var name = Path.GetFileName(input);
        var bytes = File.ReadAllBytes(input);
        if (name.EndsWith(".INF", StringComparison.OrdinalIgnoreCase))
        {
            // A loose .INF is plaintext, but one the user extracted from GLOBAL.BSA is not.
            var inf = ArenaInfFile.Parse(bytes, name, ArenaInfFile.IsProbablyEncrypted(bytes));
            PrintInf(inf, filter, limit, ref printed);
        }
        else if (name.Equals(DaggerfallTextFile.FileName, StringComparison.OrdinalIgnoreCase))
        {
            PrintTextRecords(DaggerfallTextFile.Parse(bytes), filter, limit, ref printed);
        }
        else if (DaggerfallBookFile.IsBookFileName(name))
        {
            PrintBook(DaggerfallBookFile.Parse(bytes, name), filter, limit, ref printed);
        }
        else if (name.EndsWith(".QRC", StringComparison.OrdinalIgnoreCase))
        {
            PrintQuest(DaggerfallQuestFile.Create(Path.GetFileNameWithoutExtension(name), bytes, null), filter, ref printed);
        }
        else
        {
            PrintTemplate(ArenaTemplateDat.Parse(bytes), filter, limit, ref printed);
        }
    }

    private static void RunArena(string root, bool wantTemplate, bool wantInf, string? filter, int limit, ref int printed)
    {
        if (wantTemplate)
        {
            var templatePath = Path.Combine(root, "TEMPLATE.DAT");
            if (File.Exists(templatePath))
            {
                PrintTemplate(ArenaTemplateDat.Parse(File.ReadAllBytes(templatePath)), filter, limit, ref printed);
            }
        }

        if (wantInf && (limit == 0 || printed < limit))
        {
            foreach (var (name, plain) in ArenaRecordSource.EnumerateInfFiles(root))
            {
                if (limit > 0 && printed >= limit)
                {
                    break;
                }

                PrintInf(ArenaInfFile.ParseText(System.Text.Encoding.Latin1.GetString(plain), name),
                    filter, limit, ref printed);
            }
        }
    }

    private static void RunDaggerfall(string dataRoot, bool wantText, bool wantBooks, bool wantQuests, string? filter, int limit, ref int printed)
    {
        if (wantQuests)
        {
            foreach (var name in DaggerfallQuestFile.EnumerateNames(dataRoot))
            {
                if (limit > 0 && printed >= limit)
                {
                    break;
                }

                PrintQuest(DaggerfallQuestFile.Load(dataRoot, name), filter, ref printed);
            }
        }

        if (wantText)
        {
            var textPath = Path.Combine(dataRoot, DaggerfallTextFile.FileName);
            if (File.Exists(textPath))
            {
                PrintTextRecords(DaggerfallTextFile.Parse(File.ReadAllBytes(textPath)), filter, limit, ref printed);
            }
        }

        if (wantBooks && (limit == 0 || printed < limit))
        {
            var booksDirectory = Path.Combine(dataRoot, "BOOKS");
            if (!Directory.Exists(booksDirectory))
            {
                return;
            }

            var bookPaths = Directory.EnumerateFiles(booksDirectory)
                .Where(p => DaggerfallBookFile.IsBookFileName(Path.GetFileName(p)))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
            foreach (var path in bookPaths)
            {
                if (limit > 0 && printed >= limit)
                {
                    break;
                }

                PrintBook(DaggerfallBookFile.Parse(File.ReadAllBytes(path), Path.GetFileName(path)), filter, limit, ref printed);
            }
        }
    }

    private static void PrintQuest(DaggerfallQuestFile quest, string? filter, ref int printed)
    {
        var messages = (quest.Text?.Records ?? [])
            .Where(r => filter is null || r.Subrecords.Any(s => Matches(s, filter)) || Matches(quest.Name, filter))
            .ToList();
        if (messages.Count == 0)
        {
            return;
        }

        AnsiConsole.MarkupLine("[bold]{0}[/] [grey]{1} message(s), {2:N0}-byte QBN[/]",
            Markup.Escape(quest.Name), quest.Text?.Records.Count ?? 0, quest.Compiled.Length);
        foreach (var record in messages)
        {
            AnsiConsole.MarkupLine("  [yellow]#{0}[/]", record.Id);
            foreach (var line in record.Text.Split('\n'))
            {
                AnsiConsole.MarkupLine("    {0}", Markup.Escape(line));
            }
        }

        printed++;
    }

    private static void PrintTextRecords(DaggerfallTextFile text, string? filter, int limit, ref int printed)
    {
        var wroteHeader = false;
        foreach (var record in text.Records)
        {
            if (limit > 0 && printed >= limit)
            {
                return;
            }

            var matches = record.Subrecords.Where(s => Matches(s, filter)).ToList();
            if (matches.Count == 0 && !Matches(record.Id.ToString(CultureInfo.InvariantCulture), filter))
            {
                continue;
            }

            if (!wroteHeader)
            {
                AnsiConsole.MarkupLine("[bold]{0}[/]", DaggerfallTextFile.FileName);
                wroteHeader = true;
            }

            AnsiConsole.MarkupLine("  [yellow]#{0}[/] [grey]{1} variant(s)[/]", record.Id, record.Subrecords.Count);
            foreach (var variant in filter is null ? record.Subrecords : matches)
            {
                foreach (var line in variant.Split('\n'))
                {
                    AnsiConsole.MarkupLine("    {0}", Markup.Escape(line));
                }
            }

            printed++;
        }
    }

    private static void PrintBook(DaggerfallBookFile book, string? filter, int limit, ref int printed)
    {
        if (limit > 0 && printed >= limit)
        {
            return;
        }

        var headerMatches = Matches(book.Title, filter) || Matches(book.Author, filter) || Matches(book.Name, filter);
        var pages = book.PageTexts
            .Select((pageText, index) => (Index: index, Text: pageText))
            .Where(p => headerMatches || Matches(p.Text, filter))
            .ToList();
        if (pages.Count == 0)
        {
            return;
        }

        AnsiConsole.MarkupLine("[bold]{0}[/] — [yellow]{1}[/] [grey]by {2}{3}, {4} page(s)[/]",
            Markup.Escape(book.Name), Markup.Escape(book.Title), Markup.Escape(book.Author),
            book.IsNaughty ? ", naughty" : string.Empty, book.Pages.Count);
        foreach (var (index, pageText) in pages)
        {
            AnsiConsole.MarkupLine("  [grey]page {0}[/]", index + 1);
            foreach (var line in pageText.Split('\n'))
            {
                AnsiConsole.MarkupLine("    {0}", Markup.Escape(line));
            }
        }

        printed++;
    }

    private static void PrintTemplate(ArenaTemplateDat template, string? filter, int limit, ref int printed)
    {
        var wroteHeader = false;
        foreach (var entry in template.Entries)
        {
            if (limit > 0 && printed >= limit)
            {
                return;
            }

            var matches = entry.Values.Where(v => Matches(v, filter)).ToList();
            if (matches.Count == 0 && !Matches(entry.DisplayKey, filter))
            {
                continue;
            }

            if (!wroteHeader)
            {
                AnsiConsole.MarkupLine("[bold]TEMPLATE.DAT[/]");
                wroteHeader = true;
            }

            var label = entry.Copy > 0 ? $"{entry.DisplayKey} (tileset copy {entry.Copy})" : entry.DisplayKey;
            AnsiConsole.MarkupLine("  [yellow]{0}[/] [grey]{1} value(s)[/]", Markup.Escape(label), entry.Values.Count);
            foreach (var value in filter is null ? entry.Values : matches)
            {
                AnsiConsole.MarkupLine("    {0}", Markup.Escape(Collapse(value)));
            }

            printed++;
        }
    }

    private static void PrintInf(ArenaInfFile inf, string? filter, int limit, ref int printed)
    {
        var wroteHeader = false;
        foreach (var text in inf.Texts)
        {
            if (limit > 0 && printed >= limit)
            {
                return;
            }

            var body = text.Text ?? text.Riddle?.Riddle;
            if (body is null && text.KeyId is null)
            {
                continue;
            }

            if (!Matches(body, filter) && !Matches(inf.Name, filter))
            {
                continue;
            }

            if (!wroteHeader)
            {
                AnsiConsole.MarkupLine("[bold]{0}[/]", Markup.Escape(inf.Name));
                wroteHeader = true;
            }

            var tags = new List<string>();
            if (text.KeyId is { } key)
            {
                tags.Add($"key +{key}");
            }

            if (text.Riddle is not null)
            {
                tags.Add("riddle");
            }

            if (text.DisplayedOnce)
            {
                tags.Add("once");
            }

            var suffix = tags.Count > 0 ? $" [grey]({string.Join(", ", tags)})[/]" : string.Empty;
            AnsiConsole.MarkupLine("  [yellow]*TEXT {0}[/]{1}", text.Id, suffix);

            if (body is not null)
            {
                foreach (var line in body.Split('\n'))
                {
                    AnsiConsole.MarkupLine("    {0}", Markup.Escape(line));
                }
            }

            if (text.Riddle is { } riddle)
            {
                if (riddle.Answers.Count > 0)
                {
                    AnsiConsole.MarkupLine("    [grey]answers:[/] {0}",
                        Markup.Escape(string.Join(" | ", riddle.Answers.Select(a => a.Trim()))));
                }

                if (riddle.Correct.Length > 0)
                {
                    AnsiConsole.MarkupLine("    [green]correct:[/] {0}", Markup.Escape(Collapse(riddle.Correct)));
                }

                if (riddle.Wrong.Length > 0)
                {
                    AnsiConsole.MarkupLine("    [red]wrong:[/] {0}", Markup.Escape(Collapse(riddle.Wrong)));
                }
            }

            printed++;
        }
    }

    private static void WriteFooter(int printed)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]{0} entr{1} shown.[/]", printed, printed == 1 ? "y" : "ies");
    }

    private static bool Matches(string? value, string? filter)
    {
        return filter is null ||
               (value is not null && value.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private static string Collapse(string value)
    {
        return value.Replace('\n', ' ').Replace('\r', ' ').Trim();
    }
}
