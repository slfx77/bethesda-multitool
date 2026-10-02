using System.CommandLine;
using System.Globalization;
using System.Text;
using BethesdaMultitool.CLI.Rendering.Map;
using BethesdaMultitool.Core.Formats.Arena;
using BethesdaMultitool.Core.Formats.Battlespire;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Fallout;
using BethesdaMultitool.Core.Formats.Granny;
using BethesdaMultitool.Core.Formats.Redguard;
using BethesdaMultitool.Core.Formats.Tactics;
using BethesdaMultitool.Core.Formats.VanBuren;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Core.Games;
using Spectre.Console;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.CLI.Commands.Classic;

/// <summary>
///     <c>classic</c> command group — reads the pre-plugin-era games (Arena, Daggerfall,
///     Battlespire, Redguard, Fallout 1/2, Fallout Tactics), whose content lives in bespoke
///     containers rather than a plugin record stream. <c>classic text</c> today; the map and
///     audio arms join as their game verticals land.
/// </summary>
public static class ClassicCommand
{
    /// <summary>Battlespire's mesh archives, in the order a directory input is searched.</summary>
    private static readonly string[] BattlespireMeshArchives = ["3D.BSA", "3D.BS6"];

    private static readonly string[] TextSources = ["all", "template", "inf", "text", "books", "quests"];

    public static Command Create()
    {
        var command = new Command("classic", "Read classic (pre-Morrowind) game data");
        command.Subcommands.Add(CreateTextCommand());
        command.Subcommands.Add(CreateMapCommand());
        command.Subcommands.Add(CreateMeshCommand());
        command.Subcommands.Add(CreateBlockCommand());
        command.Subcommands.Add(CreateDungeonCommand());
        command.Subcommands.Add(CreateLevelCommand());
        command.Subcommands.Add(CreateExeCommand());
        return command;
    }

    private static Command CreateLevelCommand()
    {
        var command = new Command("level",
            "Inspect or assemble classic level files (Battlespire BS6.BSA, Redguard maps\\*.RGM)");
        command.Subcommands.Add(CreateLevelInfoCommand());
        command.Subcommands.Add(CreateLevelExportCommand());
        return command;
    }

    private static Command CreateLevelInfoCommand()
    {
        var command = new Command("info", "Summarize the level archive, or one level with --entry");
        var inputArg = new Argument<string>("input")
            { Description = "BS6.BSA, a .BS6 file, or a Battlespire install/GAMEDATA directory" };
        var entryOption = new Option<string?>("--entry", "-e")
            { Description = "Level name inside the archive (e.g. L8.BS6)" };
        command.Arguments.Add(inputArg);
        command.Options.Add(entryOption);
        command.SetAction((parseResult, _) => Guarded(() =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var entry = parseResult.GetValue(entryOption);
            // Arena .MIF / .RMD levels (loose, or a GLOBAL.BSA entry) take their own route.
            if (ArenaLevelCommandSupport.IsArenaLevel(input, entry))
            {
                ArenaLevelCommandSupport.Info(input, entry, null);
                return;
            }

            RunLevelInfo(input, entry);
        }));
        return command;
    }

    private static Command CreateLevelExportCommand()
    {
        var command = new Command("export", "Assemble one level's placed meshes into a single GLB");
        var inputArg = new Argument<string>("input")
            { Description = "BS6.BSA, a .BS6 file, or a Battlespire install/GAMEDATA directory" };
        var entryOption = new Option<string?>("--entry", "-e")
            { Description = "Level name inside the archive (e.g. L8.BS6)" };
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output directory for the GLB",
            DefaultValueFactory = _ => "TestOutput/classic-levels"
        };
        var pngOption = new Option<bool>("--png")
            { Description = "Also rasterise a top-down and an oblique PNG preview beside the GLB (Redguard .RGM)" };
        var levelOption = new Option<int?>("--level")
            { Description = "Arena .MIF only: export just this level index (default: every level)" };
        var infOption = new Option<string?>("--inf")
            { Description = "Arena only: texture through this .INF instead of the level's own (or TCN/TWN default)" };
        command.Arguments.Add(inputArg);
        command.Options.Add(entryOption);
        command.Options.Add(outputOption);
        command.Options.Add(pngOption);
        command.Options.Add(levelOption);
        command.Options.Add(infOption);
        command.SetAction((parseResult, _) => Guarded(() =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var entry = parseResult.GetValue(entryOption);
            var output = parseResult.GetValue(outputOption)!;
            // Arena .MIF / .RMD levels (loose, or a GLOBAL.BSA entry) take their own route.
            if (ArenaLevelCommandSupport.IsArenaLevel(input, entry))
            {
                ArenaLevelCommandSupport.Export(
                    input, entry, output, parseResult.GetValue(levelOption), parseResult.GetValue(infOption));
                return;
            }

            RunLevelExport(input, entry, output, parseResult.GetValue(pngOption));
        }));
        return command;
    }

    private static void RunLevelExport(string input, string? entryName, string outputDir, bool png)
    {
        if (IsRedguardMap(input))
        {
            RunRedguardLevelExport(input, outputDir, png);
            return;
        }

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
                throw new InvalidOperationException(
                    $"{Path.GetFileName(path)} holds many levels — pass --entry <name>.");
            }

            using var archive = ArchiveReader.Open(path);
            var entry = archive.ListFiles()
                            .FirstOrDefault(e => e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException(
                            $"No level named '{entryName}' in {Path.GetFileName(path)}.");
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

        // ⚑ Flats resolve by NAME through BSI.BSA, which decodes — they were never blocked on the
        // undecoded mesh-texture mapping, and they carry the level's monsters, items and flames.
        using var sprites = BattlespireFlatSpriteSource.Open(meshDirectory);
        var flats = Bs6SceneAssembler.AssembleFlats(level.Flats, sprites.SizeOf, sprites.Register);

        // ⚑ Mesh textures resolve by NAME too, since 2026-09-08: a plane's 32-bit key is the
        // base-40 encoding of its BSI stem (GAME.EXE FUN_00075154 / FUN_00073FC8), so meshes and
        // flats both draw on BSI.BSA — flats under their reserved archive number, meshes by key.
        using var textures = BattlespireTextureResolver.Open(meshDirectory);
        var stem = Path.GetFileNameWithoutExtension(level.Name).ToUpperInvariant();
        var outputPath = Path.Combine(outputDir, stem + ".glb");
        XnGineMeshGlbExporter.WriteScene(
            stem, [.. assembly.Instances, .. flats.Instances], outputPath,
            (archive, record) => archive == Bs6FlatBillboard.FlatTextureArchive
                ? sprites.Resolve(archive, record)
                : textures.Resolve(archive, record));

        PrintLevel(level);
        AnsiConsole.MarkupLine(
            "[green]Wrote[/] {0} [grey]({1} of {2} placements assembled from {3} archived + {4} loose meshes)[/]",
            Markup.Escape(outputPath), assembly.Resolved, assembly.Placed, meshes.ArchivedCount, meshes.LooseCount);
        if (assembly.MissingNames.Count > 0)
        {
            AnsiConsole.MarkupLine("[yellow]{0} mesh name(s) unresolved:[/] {1}",
                assembly.MissingNames.Count,
                Markup.Escape(string.Join(", ", assembly.MissingNames.Take(12))));
        }

        AnsiConsole.MarkupLine(
            "[grey]{0} of {1} flats placed as textured billboards (sprites resolve by name through BSI.BSA).[/]",
            flats.Instances.Count, flats.Placed);
        if (textures.MissingNames.Count > 0)
        {
            AnsiConsole.MarkupLine("[yellow]{0} mesh texture name(s) absent from BSI.BSA:[/] {1}",
                textures.MissingNames.Count, Markup.Escape(string.Join(", ", textures.MissingNames.Take(12))));
        }
        if (flats.MissingSprites.Count > 0)
        {
            AnsiConsole.MarkupLine("[yellow]{0} sprite name(s) unresolved:[/] {1}",
                flats.MissingSprites.Count, Markup.Escape(string.Join(", ", flats.MissingSprites.Take(8))));
        }

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

    /// <summary>True when the input is a Redguard <c>maps\*.RGM</c> — by CONTENT (the <c>RAHD</c> tag), not by name.</summary>
    private static bool IsRedguardMap(string input)
    {
        if (!File.Exists(input))
        {
            return false;
        }

        Span<byte> head = stackalloc byte[RedguardRgmFile.ChunkHeaderLength];
        using var stream = File.OpenRead(input);
        var read = stream.Read(head);
        return RedguardRgmFile.IsRgmFile(head[..read]);
    }

    /// <summary>
    ///     Assembles one Redguard map into a textured GLB — its <c>MPSO</c> statics, <c>MPOB</c>
    ///     placements, <c>MPSF</c> flats and, for an outdoor world, the <c>.WLD</c> terrain — through
    ///     the same <see cref="RedguardLevelLoader" /> the GUI's level pane uses. Optionally leaves a
    ///     top-down and an oblique PNG beside it as a visual oracle.
    /// </summary>
    private static void RunRedguardLevelExport(string input, string outputDir, bool png)
    {
        using var level = RedguardLevelLoader.Load(input);
        if (level.Scene.Instances.Count == 0)
        {
            throw new InvalidOperationException(
                $"{level.Stem} resolved none of its {level.Scene.StaticsPlaced + level.Scene.PlacementsPlaced} statics and placements against {level.MeshArchiveName}.");
        }

        var outputPath = Path.Combine(outputDir, level.Stem + ".glb");
        var textured = 0;
        XnGineMeshGlbExporter.WriteScene(level.Stem, level.Instances, outputPath, (archive, record) =>
        {
            var image = level.Textures.Resolve(archive, record);
            if (image is not null)
            {
                textured++;
            }

            return image;
        });

        PrintRedguardLevel(level);
        AnsiConsole.MarkupLine(
            "[green]Wrote[/] {0} [grey]({1} instances; {2} material(s) textured through {3}{4})[/]",
            Markup.Escape(outputPath), level.Instances.Count, textured, Markup.Escape(level.PaletteName),
            level.Textures.UsesFxArt ? ", 3dfx fxart preferred" : ", software 3dart");

        if (!png)
        {
            return;
        }

        foreach (var (suffix, azimuth, elevation) in new[] { ("top", 0f, 90f), ("oblique", 45f, 30f) })
        {
            var sprite = XnGineScenePreviewRenderer.Render(level.Instances, azimuth, elevation, 1024);
            if (sprite is null)
            {
                continue;
            }

            var pngPath = Path.Combine(outputDir, $"{level.Stem}_{suffix}.png");
            PngWriter.SaveRgba(sprite.Pixels, sprite.Width, sprite.Height, pngPath);
            AnsiConsole.MarkupLine("[green]Wrote[/] {0} [grey]({1}x{2})[/]", Markup.Escape(pngPath), sprite.Width,
                sprite.Height);
        }
    }

    private static void RunRedguardLevelInfo(string input)
    {
        using var level = RedguardLevelLoader.Load(input);
        PrintRedguardLevel(level);
    }

    private static void PrintRedguardLevel(RedguardLevelAssembly level)
    {
        var map = level.Map;
        var scene = level.Scene;
        AnsiConsole.MarkupLine("[bold]{0}[/] [grey](Redguard map; meshes from {1}, {2} segments)[/]",
            Markup.Escape(map.Name), Markup.Escape(level.MeshArchiveName), level.MeshArchiveCount);
        AnsiConsole.MarkupLine(
            "  statics {0} placed, {1} resolved; object placements {2} with a mesh, {3} resolved; flats {4} of {5} placed; {6} lights, {7} markers, {8} ropes",
            scene.StaticsPlaced, scene.StaticsResolved, scene.PlacementsPlaced, scene.PlacementsResolved,
            level.Flats.Instances.Count, level.Flats.Placed, map.Lights.Count, map.Markers.Count, map.Ropes.Count);
        AnsiConsole.MarkupLine("  palette {0}; terrain {1}",
            Markup.Escape(level.PaletteName),
            level.TerrainName is null ? "none (interior)" : Markup.Escape(level.TerrainName));

        if (scene.MissingNames.Count > 0)
        {
            AnsiConsole.MarkupLine("  [yellow]{0} mesh name(s) unresolved:[/] {1}",
                scene.MissingNames.Count, Markup.Escape(string.Join(", ", scene.MissingNames.Take(12))));
        }

        // An empty ROB placeholder stands for the loose .3DC of its name, drawn in its keyframe pose with the
        // reference UVs. Both are approximations, so say so; the GUI's 3D level pane shows the same line.
        if (level.LooseKeyframeNote is { } looseNote)
        {
            AnsiConsole.MarkupLine("  {0}", Markup.Escape(looseNote));
        }

        if (level.Flats.MissingTextures.Count > 0)
        {
            AnsiConsole.MarkupLine("  [yellow]{0} flat texture(s) unresolved:[/] {1}",
                level.Flats.MissingTextures.Count, Markup.Escape(string.Join(", ", level.Flats.MissingTextures.Take(8))));
        }

        foreach (var (name, reason) in level.MeshFailures.Take(6))
        {
            AnsiConsole.MarkupLine("  [red]unparsed[/] {0}: {1}", Markup.Escape(name), Markup.Escape(reason));
        }

        var bounds = scene.Instances
            .Select(i => System.Numerics.Vector3.Transform(System.Numerics.Vector3.Zero, i.Transform))
            .ToList();
        if (bounds.Count > 0)
        {
            AnsiConsole.MarkupLine("  origins span ({0:F0}, {1:F0}, {2:F0}) to ({3:F0}, {4:F0}, {5:F0}) world units (Y negative-up)",
                bounds.Min(b => b.X), bounds.Min(b => b.Y), bounds.Min(b => b.Z),
                bounds.Max(b => b.X), bounds.Max(b => b.Y), bounds.Max(b => b.Z));
        }

        var mostPlaced = scene.Instances
            .Select(i => i.Name[(i.Name.LastIndexOf('_') + 1)..])
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(12)
            .Select(g => $"{g.Key} x{g.Count()}");
        AnsiConsole.MarkupLine("  most placed: {0}", Markup.Escape(string.Join(", ", mostPlaced)));
    }

    private static void RunLevelInfo(string input, string? entryName)
    {
        if (IsRedguardMap(input))
        {
            RunRedguardLevelInfo(input);
            return;
        }

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
                        ?? throw new InvalidOperationException(
                            $"No level named '{entryName}' in {Path.GetFileName(path)}.");
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
            level.MeshNames.Count, level.Objects.Count, level.Lights.Count, level.Flats.Count, level.ViewCount,
            level.SnapCount);

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

    private static Command CreateDungeonCommand()
    {
        var command = new Command("dungeon",
            "Inspect or assemble a whole Daggerfall dungeon (many RDB blocks on one grid)");
        command.Subcommands.Add(CreateDungeonInfoCommand());
        command.Subcommands.Add(CreateDungeonExportCommand());
        return command;
    }

    private static Command CreateDungeonInfoCommand()
    {
        var command = new Command("info", "List the dungeons in MAPS.BSA, or one dungeon's block grid with --entry");
        var inputArg = new Argument<string>("input") { Description = "A Daggerfall install or ARENA2 directory" };
        var entryOption = new Option<string?>("--entry", "-e") { Description = "Location name (substring match)" };
        var limitOption = new Option<int>("--limit", "-l")
            { Description = "Rows to list", DefaultValueFactory = _ => 40 };
        command.Arguments.Add(inputArg);
        command.Options.Add(entryOption);
        command.Options.Add(limitOption);
        command.SetAction((parseResult, _) => Guarded(() => RunDungeonInfo(
            parseResult.GetValue(inputArg)!,
            parseResult.GetValue(entryOption),
            parseResult.GetValue(limitOption))));
        return command;
    }

    private static Command CreateDungeonExportCommand()
    {
        var command = new Command("export", "Assemble a whole dungeon into one textured GLB");
        var inputArg = new Argument<string>("input") { Description = "A Daggerfall install or ARENA2 directory" };
        var entryOption = new Option<string>("--entry", "-e")
            { Description = "Location name (substring match)", Required = true };
        var outputOption = new Option<string>("--output", "-o") { Description = "Output directory", Required = true };
        command.Arguments.Add(inputArg);
        command.Options.Add(entryOption);
        command.Options.Add(outputOption);
        command.SetAction((parseResult, _) => Guarded(() => RunDungeonExport(
            parseResult.GetValue(inputArg)!,
            parseResult.GetValue(entryOption)!,
            parseResult.GetValue(outputOption)!)));
        return command;
    }

    /// <summary>Finds a location carrying a dungeon by name, preferring an exact match.</summary>
    private static DaggerfallLocation RequireDungeonLocation(DaggerfallMapsFile maps, string name)
    {
        var matches = maps.Regions
            .SelectMany(r => r.Locations)
            .Where(l => l.Dungeon is not null)
            .Where(l => l.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            throw new InvalidOperationException($"No dungeon whose name contains '{name}'.");
        }

        // An exact match wins over a longer name that merely contains it.
        return matches.Find(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? matches[0];
    }

    private static void RunDungeonInfo(string input, string? entryName, int limit)
    {
        var arena2 = Path.GetDirectoryName(Path.GetFullPath(ResolveBlocksPath(input)))!;
        var maps = DaggerfallMapsFile.Open(Path.Combine(arena2, "MAPS.BSA"));

        if (entryName is null)
        {
            var all = maps.Regions
                .SelectMany(r => r.Locations.Select(l => (Region: r.Name, Location: l)))
                .Where(x => x.Location.Dungeon is not null)
                .ToList();

            AnsiConsole.MarkupLine("[bold]{0}[/] dungeons across {1} regions", all.Count, maps.Regions.Count);
            var table = new Table().AddColumns("Region", "Location", "Type", "Blocks");
            foreach (var (region, location) in all.Take(limit))
            {
                table.AddRow(
                    Markup.Escape(region),
                    Markup.Escape(location.Name),
                    location.DungeonType.ToString(),
                    location.Dungeon!.Blocks.Count.ToString(CultureInfo.InvariantCulture));
            }

            AnsiConsole.Write(table);
            return;
        }

        var found = RequireDungeonLocation(maps, entryName);
        var dungeon = found.Dungeon!;
        AnsiConsole.MarkupLine(
            "[bold]{0}[/] - {1}, {2} block(s), {3} door(s)",
            Markup.Escape(found.Name), found.DungeonType, dungeon.Blocks.Count, dungeon.DoorCount);

        var grid = new Table().AddColumns("X", "Z", "Block", "Start");
        foreach (var block in dungeon.Blocks)
        {
            grid.AddRow(
                block.X.ToString(CultureInfo.InvariantCulture),
                block.Z.ToString(CultureInfo.InvariantCulture),
                Markup.Escape(DaggerfallDungeonBlockName.Resolve(block) ?? "(unnamed family)"),
                block.IsStartingBlock ? "yes" : string.Empty);
        }

        AnsiConsole.Write(grid);
    }

    private static void RunDungeonExport(string input, string entryName, string outputDir)
    {
        var blocksPath = ResolveBlocksPath(input);
        var arena2 = Path.GetDirectoryName(Path.GetFullPath(blocksPath))!;
        var maps = DaggerfallMapsFile.Open(Path.Combine(arena2, "MAPS.BSA"));
        var found = RequireDungeonLocation(maps, entryName);
        var blocks = DaggerfallBlocksFile.Open(blocksPath);
        var meshes = DaggerfallMeshLibrary.Open(arena2);

        // A dungeon repeats blocks across its grid, so parse each named block once.
        var parsed = new Dictionary<string, DaggerfallRdbBlock?>(StringComparer.OrdinalIgnoreCase);

        DaggerfallRdbBlock? ResolveBlock(string name)
        {
            if (parsed.TryGetValue(name, out var cached))
            {
                return cached;
            }

            var index = blocks.IndexOf(name);
            var block = index < 0 ? null : blocks.ParseRdb(index);
            parsed[name] = block;
            return block;
        }

        var assembly = DaggerfallDungeonSceneAssembler.Assemble(
            found.Name, found.Dungeon!.Blocks, ResolveBlock, meshes.Resolve);

        if (assembly.Instances.Count == 0)
        {
            throw new InvalidOperationException(
                $"'{found.Name}' assembled no geometry from {assembly.BlocksPlaced} block(s).");
        }

        Directory.CreateDirectory(outputDir);
        var stem = new string([.. found.Name.Select(c => char.IsLetterOrDigit(c) ? c : '_')]);
        var written = new List<string>();
        WriteBlockGlb(arena2, outputDir, stem, assembly.Instances, assembly.Placed, [], written);

        AnsiConsole.MarkupLine(
            "[bold]{0}[/] - {1}, {2} block(s) placed, {3} object(s)",
            Markup.Escape(found.Name), found.DungeonType, assembly.BlocksPlaced, assembly.Placed);
        if (assembly.MissingBlockNames.Count > 0)
        {
            AnsiConsole.MarkupLine("[yellow]{0} block(s) not in the archive:[/] {1}",
                assembly.MissingBlockNames.Count,
                Markup.Escape(string.Join(", ", assembly.MissingBlockNames.Take(8))));
        }

        foreach (var file in written)
        {
            AnsiConsole.MarkupLine("[green]Wrote[/] {0}", Markup.Escape(file));
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
        var inputArg = new Argument<string>("input")
            { Description = "BLOCKS.BSA, or a Daggerfall install/data directory" };
        var entryOption = new Option<string?>("--entry", "-e")
            { Description = "Block name (e.g. WALLAA03.RMB, N0000071.RDB)" };
        command.Arguments.Add(inputArg);
        command.Options.Add(entryOption);
        command.SetAction((parseResult, _) => Guarded(() => RunBlockInfo(
            parseResult.GetValue(inputArg)!,
            parseResult.GetValue(entryOption))));
        return command;
    }

    private static Command CreateBlockExportCommand()
    {
        var command = new Command("export",
            "Render one block's diagnostic images to PNG (RMB: automap + ground grid; RDB: object plan)");
        var inputArg = new Argument<string>("input")
            { Description = "BLOCKS.BSA, or a Daggerfall install/data directory" };
        var entryOption = new Option<string>("--entry", "-e") { Description = "Block name", Required = true };
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output directory",
            DefaultValueFactory = _ => "TestOutput/classic-blocks"
        };
        var scaleOption = new Option<int>("--scale")
        {
            Description =
                "Pixels per automap cell (default 8); ground tiles draw 4x this, dungeon plans are 64x this pixels square",
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
                }
            }
            catch (InvalidDataException e)
            {
                failures.Add($"{blocks.Name(i)}: {e.Message}");
            }
        }

        AnsiConsole.MarkupLine("[bold cyan]{0}[/] — [grey]{1} records[/]", Markup.Escape(Path.GetFileName(path)),
            blocks.Count);
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Kind")
            .AddColumn("Records", c => c.RightAligned());
        foreach (var (type, count) in byType.OrderBy(kvp => kvp.Key))
        {
            table.AddRow(type.ToString().ToUpperInvariant(), count.ToString("N0", CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[grey]RMB: {0:N0} sub-blocks, {1:N0} placed models. RDB: {2:N0} objects.[/]",
            rmbSubBlocks, rmbModels, rdbObjects);
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
            AnsiConsole.MarkupLine(
                "  [grey]#{0}[/] {1} q{2} at ({3}, {4}) rot {5:F1}° — ext {6} models/{7} flats/{8} doors, int {9} models/{10} flats/{11} people/{12} doors",
                i, building.BuildingType, building.Quality, sub.XPos, sub.ZPos,
                sub.YRotation / DaggerfallRmbBlock.RotationDivisor,
                sub.Exterior.Models.Count, sub.Exterior.Flats.Count, sub.Exterior.Doors.Count,
                sub.Interior.Models.Count, sub.Interior.Flats.Count, sub.Interior.People.Count,
                sub.Interior.Doors.Count);
        }

        var modelIds = block.AllModels.Select(m => m.ModelId).Distinct().Order().ToList();
        AnsiConsole.MarkupLine("  model ids ({0}): {1}", modelIds.Count,
            Markup.Escape(string.Join(", ", modelIds.Take(24).Select(id => id.ToString(CultureInfo.InvariantCulture))) +
                          (modelIds.Count > 24 ? ", …" : string.Empty)));
        var textures = block.GroundTiles.Select(t => t.TextureRecord).Distinct().Order().ToList();
        AnsiConsole.MarkupLine("  ground texture records: {0}; scenery on {1} tiles",
            Markup.Escape(string.Join(", ", textures.Select(t => t.ToString(CultureInfo.InvariantCulture)))),
            block.GroundScenery.Count(s => s.HasScenery));
    }

    private static void PrintRdb(DaggerfallRdbBlock block)
    {
        var objects = block.AllObjects.ToList();
        AnsiConsole.MarkupLine("  {0} dungeon block, {1}x{2} object lists ({3} used), DAGR tag '{4}'",
            block.Type, block.Width, block.Height, block.ObjectRoots.Count(r => r.Objects.Count > 0),
            Markup.Escape(block.ObjectHeader.Dagr));
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

        // ⚑ The GLB is the explorable artefact; the PNGs beside it stay because a top-down plan
        // reads a dungeon's shape at a glance in a way a 3D scene does not. ARCH3D.BSA sits beside
        // BLOCKS.BSA in ARENA2, so one library serves every placement in the block — opened ONCE,
        // since a block repeats the same corridor piece dozens of times.
        var dataRoot = Path.GetDirectoryName(Path.GetFullPath(path))!;
        DaggerfallMeshLibrary? meshes = null;
        if (File.Exists(Path.Combine(dataRoot, DaggerfallArch3DFile.FileName)))
        {
            meshes = DaggerfallMeshLibrary.Open(dataRoot);
        }

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
                if (meshes is not null)
                {
                    var rmbScene = DaggerfallBlockSceneAssembler.Assemble(rmb.Name, rmb.SubRecords, meshes.Resolve);
                    WriteBlockGlb(dataRoot, outputDir, stem, rmbScene.Instances, rmbScene.Placed, rmbScene.MissingIds,
                        written);
                }

                PrintRmb(rmb);
                break;
            case DaggerfallBlockType.Rdb:
                var rdb = blocks.ParseRdb(index);
                var plan = DaggerfallBlockRenderer.RenderDungeonPlan(rdb, scale * 64);
                var planPath = Path.Combine(outputDir, stem + "_plan.png");
                PngWriter.SaveRgba(plan.Pixels, plan.Width, plan.Height, planPath);
                written.Add(planPath);
                if (meshes is not null)
                {
                    var rdbScene = DaggerfallRdbSceneAssembler.Assemble(
                        rdb.Name, rdb.ModelReferences, rdb.AllObjects, meshes.Resolve);
                    WriteBlockGlb(dataRoot, outputDir, stem, rdbScene.Instances, rdbScene.Placed, rdbScene.MissingIds,
                        written);
                }

                PrintRdb(rdb);
                break;
            default:
                throw new NotSupportedException(
                    $"{blocks.Name(index)} is a {blocks.TypeAt(index)} record; only RMB and RDB blocks render.");
        }

        foreach (var file in written)
        {
            AnsiConsole.MarkupLine("[green]Wrote[/] {0}", Markup.Escape(file));
        }
    }

    /// <summary>
    ///     Writes an assembled Daggerfall block as one textured GLB. Shared by the RMB and RDB
    ///     paths, which produce the same instances from different assemblers.
    /// </summary>
    private static void WriteBlockGlb(
        string dataRoot,
        string outputDir,
        string stem,
        IReadOnlyList<XnGineMeshInstance> instances,
        int placed,
        IReadOnlyList<uint> missingIds,
        List<string> written)
    {
        if (instances.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[yellow]No geometry assembled[/] [grey](none of {0} placements resolved against {1})[/]",
                placed, Markup.Escape(DaggerfallArch3DFile.FileName));
            return;
        }

        var textures = new DaggerfallMeshTextureSource(dataRoot);
        var textured = 0;
        var outputPath = Path.Combine(outputDir, stem + ".glb");
        XnGineMeshGlbExporter.WriteScene(stem, instances, outputPath, (textureArchive, record) =>
        {
            var png = textures.Resolve(textureArchive, record);
            if (png is not null)
            {
                textured++;
            }

            return png;
        });

        written.Add(outputPath);
        AnsiConsole.MarkupLine(
            "[grey]Assembled {0} of {1} placements; {2} material(s) textured.[/]", instances.Count, placed, textured);
        if (missingIds.Count > 0)
        {
            AnsiConsole.MarkupLine("[yellow]{0} model id(s) unresolved:[/] {1}",
                missingIds.Count,
                Markup.Escape(string.Join(", ", missingIds.Take(12))));
        }
    }

    private static Command CreateMeshCommand()
    {
        var command = new Command("mesh",
            "Inspect or export classic 3D meshes (Daggerfall ARCH3D.BSA, Battlespire 3D.BSA/3D.BS6/.3D, Van Buren .grp B3D and Granny 2 payloads)");
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
            Description =
                "One mesh to describe: a Daggerfall object id, a Battlespire entry name, or a Van Buren .grp entry index / mesh name"
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
        var command = new Command("export",
            "Export one mesh to GLB (textured when TEXTURE.nnn + ART_PAL.COL sit beside the archive)");
        var inputArg = new Argument<string>("input")
        {
            Description = "ARCH3D.BSA / 3D.BSA / 3D.BS6 / a .3D file, or a game install or data directory"
        };
        var entryOption = new Option<string?>("--entry", "-e")
        {
            Description =
                "The mesh to export: a Daggerfall object id, a Battlespire entry name, or a Van Buren .grp entry index / mesh name (omit for a loose .3D file)"
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

    /// <summary>True when a resolved path is a Redguard per-map <c>.ROB</c> object archive.</summary>
    private static bool IsRedguardRobPath(string path)
    {
        return Path.GetFileName(path).EndsWith(".ROB", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     True for Redguard's animated <c>.3DC</c>. It must be checked BEFORE the loose <c>.3D</c>
    ///     path: a <c>.3DC</c> parses cleanly as a <c>.3D</c> and yields the wrong geometry in
    ///     silence, because the header offsets a <c>.3D</c> reader trusts are frame 1's.
    /// </summary>
    private static bool Is3dcPath(string path)
    {
        return Path.GetFileName(path).EndsWith(".3DC", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Describes one animated Redguard mesh: its keyframe, plus the pose stack.</summary>
    private static void Run3dcMeshInfo(string path)
    {
        var name = Path.GetFileName(path);
        var file = Redguard3DcFile.Parse(File.ReadAllBytes(path), name);

        PrintXnGineMesh(name, file.KeyframeMesh);
        AnsiConsole.MarkupLine(
            "  frames {0} ({1}), frame record {2} dwords, unaccounted region {3:N0} bytes",
            file.FrameCount,
            file.WideFrames ? "32-bit poses" : "16-bit deltas from the keyframe",
            file.FrameRecordDwords,
            file.UnaccountedLength);
    }

    /// <summary>Exports an animated mesh's keyframe to GLB.</summary>
    private static void Run3dcMeshExport(string path, string outputDir)
    {
        var name = Path.GetFileName(path);
        var file = Redguard3DcFile.Parse(File.ReadAllBytes(path), name);
        var decomposed = XnGineMeshDecomposer.Decompose(file.KeyframeMesh);
        var outputPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(name).ToUpperInvariant() + ".glb");
        XnGineMeshGlbExporter.Write(decomposed, outputPath);

        Run3dcMeshInfo(path);
        AnsiConsole.MarkupLine(
            "[green]Wrote[/] {0} [grey](keyframe pose; normals computed from the geometry — a .3DC stores none)[/]",
            Markup.Escape(outputPath));
    }

    /// <summary>True for a Van Buren <c>.grp</c> archive — the extension gates the (magic-less) tiling probe.</summary>
    private static bool IsVanBurenGrpPath(string path)
    {
        return path.EndsWith(".grp", StringComparison.OrdinalIgnoreCase) && VanBurenGrpArchive.TryProbe(path);
    }

    /// <summary>
    ///     Finds one B3D payload by entry index (<c>7</c> or <c>00007</c>, optionally with the
    ///     <c>.B3D</c> suffix a listing shows) or by its authored mesh name.
    /// </summary>
    private static (VanBurenGrpEntry Entry, VanBurenB3DFile File) FindVanBurenMesh(byte[] bytes,
        VanBurenGrpArchive archive, string entry)
    {
        var stem = entry.EndsWith(".B3D", StringComparison.OrdinalIgnoreCase) ? entry[..^4] : entry;
        if (int.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            if (index < 0 || index >= archive.Entries.Count)
            {
                throw new InvalidOperationException(
                    $"{archive.Name} has {archive.Entries.Count} entries; there is no entry {index}.");
            }

            var payload = VanBurenGrpArchive.Read(bytes, archive.Entries[index]);
            if (!VanBurenB3DFile.IsB3d(payload))
            {
                throw new InvalidOperationException(
                    $"Entry {index} of {archive.Name} is a '{archive.Entries[index].Tag}' payload, not a B3D mesh.");
            }

            return (archive.Entries[index],
                VanBurenB3DFile.Parse(payload, $"{archive.Name}/{archive.Entries[index].Name}"));
        }

        foreach (var candidate in archive.Entries)
        {
            var payload = VanBurenGrpArchive.Read(bytes, candidate);
            if (!VanBurenB3DFile.IsB3d(payload)
                || !VanBurenB3DFile.TryParse(payload, $"{archive.Name}/{candidate.Name}", out var file, out _))
            {
                continue;
            }

            if (string.Equals(file.MeshName, entry, StringComparison.OrdinalIgnoreCase))
            {
                return (candidate, file);
            }
        }

        throw new InvalidOperationException($"No B3D mesh named '{entry}' in {archive.Name}.");
    }

    private static void RunVanBurenMeshInfo(string path, string? entry)
    {
        var bytes = File.ReadAllBytes(path);
        var archive = VanBurenGrpArchive.Parse(bytes, Path.GetFileName(path));
        if (entry is not null)
        {
            if (TryFindVanBurenGranny(path, entry, out var grannyCatalog, out var granny))
            {
                PrintVanBurenGranny(granny);
                PrintVanBurenGrannyClips(grannyCatalog, granny);
                return;
            }

            var (found, file) = FindVanBurenMesh(bytes, archive, entry);
            PrintVanBurenMesh($"{archive.Name}/{found.Name}", file);
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Entry");
        table.AddColumn("Mesh");
        table.AddColumn("Form");
        table.AddColumn(new TableColumn("Verts").RightAligned());
        table.AddColumn(new TableColumn("Tris").RightAligned());
        table.AddColumn(new TableColumn("Groups").RightAligned());
        table.AddColumn(new TableColumn("Bones").RightAligned());
        table.AddColumn("Textures");

        int meshes = 0, decoded = 0, converted = 0, skinned = 0;
        long vertices = 0, triangles = 0;
        var failures = new List<string>();
        foreach (var candidate in archive.Entries)
        {
            var payload = VanBurenGrpArchive.Read(bytes, candidate);
            if (!VanBurenB3DFile.IsB3d(payload))
            {
                continue;
            }

            meshes++;
            var name = $"{archive.Name}/{candidate.Name}";
            if (!VanBurenB3DFile.TryParse(payload, name, out var file, out var error))
            {
                failures.Add(error);
                continue;
            }

            decoded++;
            var mesh = file.Meshes.Count > 0 ? file.Meshes[0] : null;
            if (mesh is not null)
            {
                vertices += mesh.Vertices.Count;
                triangles += mesh.TriangleCount;
                converted += mesh.Form == VanBurenB3dMeshForm.Converted ? 1 : 0;
                skinned += mesh.Skinned ? 1 : 0;
            }

            table.AddRow(
                Markup.Escape(candidate.Name),
                Markup.Escape(file.MeshName ?? "(unnamed)"),
                mesh?.Form.ToString() ?? "-",
                (mesh?.Vertices.Count ?? 0).ToString(CultureInfo.InvariantCulture),
                (mesh?.TriangleCount ?? 0).ToString(CultureInfo.InvariantCulture),
                (mesh?.Groups.Count ?? 0).ToString(CultureInfo.InvariantCulture),
                file.Bones.Count.ToString(CultureInfo.InvariantCulture),
                Markup.Escape(string.Join(", ",
                    (mesh?.Groups ?? []).SelectMany(g => g.Textures).Distinct(StringComparer.OrdinalIgnoreCase))));
        }

        AnsiConsole.MarkupLine(
            "[bold]{0}[/] [grey](GRP, Van Buren)[/]: {1} B3D payloads, {2} decoded ({3} converted, {4} source, {5} skinned), {6:N0} vertices, {7:N0} triangles",
            Markup.Escape(archive.Name), meshes, decoded, converted, decoded - converted, skinned, vertices, triangles);
        if (meshes == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No B3D payloads in this archive.[/]");
            PrintVanBurenGrannyCensus(path);
            return;
        }

        AnsiConsole.Write(table);
        foreach (var failure in failures)
        {
            AnsiConsole.MarkupLine("[yellow]Refused:[/] {0}", Markup.Escape(failure));
        }

        AnsiConsole.MarkupLine(
            "[grey]Texture names are carried as authored; the build's image payloads are nameless, so none resolves.[/]");
        PrintVanBurenGrannyCensus(path);
    }

    private static void PrintVanBurenMesh(string name, VanBurenB3DFile file)
    {
        AnsiConsole.MarkupLine("[bold]{0}[/] [grey](B3D 1.1, {1})[/]",
            Markup.Escape(name), file.Versioned ? "versioned meshes" : "unversioned source meshes");
        AnsiConsole.MarkupLine("  nodes: {0}", Markup.Escape(string.Join(" > ", file.NodeNames)));
        AnsiConsole.MarkupLine("  materials {0}, bones {1}, lights {2}", file.Materials.Count, file.Bones.Count,
            file.LightCount);
        if (file.Bones.Count > 0)
        {
            AnsiConsole.MarkupLine("  skeleton: {0}", Markup.Escape(string.Join(", ", file.Bones.Select(b => b.Name))));
        }

        foreach (var mesh in file.Meshes)
        {
            var (min, max) = mesh.ComputeBounds();
            AnsiConsole.MarkupLine(
                "  [bold]{0}[/] ({1}{2}): {3} vertices, {4} triangles, {5} groups; bounds ({6:F3}, {7:F3}, {8:F3}) .. ({9:F3}, {10:F3}, {11:F3})",
                Markup.Escape(mesh.NodeName), mesh.Form, mesh.Skinned ? ", skinned" : string.Empty,
                mesh.Vertices.Count, mesh.TriangleCount, mesh.Groups.Count,
                min.X, min.Y, min.Z, max.X, max.Y, max.Z);
            foreach (var group in mesh.Groups)
            {
                AnsiConsole.MarkupLine("    {0}: {1} tris, {2} {3} {4}, textures: {5}",
                    Markup.Escape(group.Name), group.TriangleCount,
                    Markup.Escape(group.Shader), Markup.Escape(group.BlendState), Markup.Escape(group.SurfaceSound),
                    Markup.Escape(group.Textures.Count > 0 ? string.Join(", ", group.Textures) : "(none)"));
            }
        }
    }

    private static void RunVanBurenMeshExport(string path, string? entry, string outputDir)
    {
        var name = Path.GetFileName(path);
        if (entry is null)
        {
            throw new InvalidOperationException($"{name} holds many meshes — pass --entry <index or mesh name>.");
        }

        var bytes = File.ReadAllBytes(path);
        var archive = VanBurenGrpArchive.Parse(bytes, name);
        if (TryFindVanBurenGranny(path, entry, out var catalog, out var granny))
        {
            RunVanBurenGrannyExport(catalog, granny, outputDir);
            return;
        }

        var (found, file) = FindVanBurenMesh(bytes, archive, entry);
        var stem = file.MeshName ?? Path.GetFileNameWithoutExtension(found.Name);
        var outputPath = Path.Combine(outputDir, stem + ".glb");
        VanBurenB3DGlbExporter.Write(file, outputPath);

        PrintVanBurenMesh($"{archive.Name}/{found.Name}", file);
        AnsiConsole.MarkupLine(
            "[green]Wrote[/] {0} [grey](Y up; the LEFT_HANDED source mirrored to glTF's right-handed frame by negating Z and reversing the winding; rigid bind pose; untextured: the .tga names cannot be resolved against the build's nameless image payloads)[/]",
            Markup.Escape(outputPath));
    }

    /// <summary>
    ///     Resolves <c>-e</c> against the archive's GRANNY 2 payloads: an entry index whose payload
    ///     opens with the Granny magic, or a model, skeleton, mesh or CLIP name (a clip is named by
    ///     its authoring scene, e.g. <c>J:\...\DCla_Melee_Move.lws</c>). ⚠ A track-group name is
    ///     deliberately NOT matched: track groups carry the creature's bare name (<c>CR_Bat</c>),
    ///     which is also the B3D render mesh's name, and the B3D lookup below must keep winning it.
    ///     False when the selector is not a Granny payload, so the B3D lookup runs instead.
    /// </summary>
    private static bool TryFindVanBurenGranny(string path, string entry, out VanBurenGrannyCatalog catalog,
        out VanBurenGrannyEntry found)
    {
        catalog = VanBurenGrannyCatalog.Open(path);
        var result = catalog.GrannyEntries.Count > 0 ? catalog.Find(entry) : null;
        found = result!;
        return result is not null;
    }

    /// <summary>
    ///     The archive's Granny 2 census: how many payloads are model, skeleton and animation files,
    ///     and a row per model/skeleton file. ⚠ Measured on the prototype: only 25 of 547 carry a
    ///     mesh — 18 collision hulls, <c>deathclaw</c>, and six door boxes whose triangle list was
    ///     never written — Granny is the build's ANIMATION system (451 clips, 74 skeleton files); the
    ///     visible character geometry is <c>B3D</c>.
    /// </summary>
    private static void PrintVanBurenGrannyCensus(string path)
    {
        var catalog = VanBurenGrannyCatalog.Open(path);
        if (catalog.GrannyEntries.Count == 0)
        {
            return;
        }

        var kinds = new Dictionary<VanBurenGrannyKind, int>();
        var meshes = 0;
        var bones = 0;
        var clips = 0;
        var failures = new List<string>();
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Entry", "Kind", "Name", "Contents");
        foreach (var grannyEntry in catalog.GrannyEntries)
        {
            VanBurenGrannyEntry decoded;
            try
            {
                decoded = catalog.Decode(grannyEntry.Index);
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException)
            {
                failures.Add($"{grannyEntry.Name}: {error.Message}");
                continue;
            }

            kinds[decoded.Kind] = kinds.GetValueOrDefault(decoded.Kind) + 1;
            var file = decoded.File;
            meshes += file.Meshes.Count;
            bones += file.Skeletons.Sum(static skeleton => skeleton.Bones.Count);
            clips += file.Animations.Count;
            if (decoded.Kind == VanBurenGrannyKind.Animation)
            {
                continue;
            }

            var contents = decoded.Kind == VanBurenGrannyKind.Model
                ? string.Join("; ",
                    file.Meshes.Select(mesh =>
                        $"{mesh.Name}: {mesh.Positions.Length:N0} verts, {(mesh.TopologyDefect is null ? $"{mesh.TriangleCount:N0} tris" : "UNWRITTEN triangle list")}, {mesh.BoneBindings.Count} bone bindings"))
                : string.Join("; ",
                    file.Skeletons.Select(skeleton => $"{skeleton.Name}: {skeleton.Bones.Count} bones"));
            table.AddRow(Markup.Escape(grannyEntry.Name), decoded.Kind.ToString(), Markup.Escape(decoded.DisplayName),
                Markup.Escape(contents));
        }

        AnsiConsole.MarkupLine(
            "[bold]{0}[/] [grey](Granny 2)[/]: {1:N0} payloads — {2} model, {3} skeleton, {4} animation files; {5:N0} meshes, {6:N0} bones, {7:N0} clips",
            Markup.Escape(catalog.Name), catalog.GrannyEntries.Count,
            kinds.GetValueOrDefault(VanBurenGrannyKind.Model), kinds.GetValueOrDefault(VanBurenGrannyKind.Skeleton),
            kinds.GetValueOrDefault(VanBurenGrannyKind.Animation),
            meshes, bones, clips);
        if (table.Rows.Count > 0)
        {
            AnsiConsole.Write(table);
        }

        AnsiConsole.MarkupLine(
            "[grey]Granny animation files are omitted from the table; -e <skeleton name> lists the clips that animate it, and export binds them.[/]");
        foreach (var failure in failures)
        {
            AnsiConsole.MarkupLine("[red]unparsed[/] {0}", Markup.Escape(failure));
        }
    }

    /// <summary>
    ///     The clips elsewhere in the archive whose track group names one of this payload's skeletons
    ///     — the set <c>classic mesh export</c> binds. ⚠ Measured 2026-09-08: the 18 <c>*_Coll</c> hulls
    ///     get NONE, because their track groups are named for the creature (<c>CR_Bat</c>), not the
    ///     hull (<c>CR_Bat_Coll</c>); <c>deathclaw</c> gets its 12.
    /// </summary>
    private static void PrintVanBurenGrannyClips(VanBurenGrannyCatalog catalog, VanBurenGrannyEntry decoded)
    {
        foreach (var skeletonName in decoded.File.Skeletons.Select(static skeleton => skeleton.Name)
                     .Distinct(StringComparer.Ordinal))
        {
            var clips = catalog.AnimationsFor(skeletonName);
            if (clips.Count == 0)
            {
                AnsiConsole.MarkupLine(
                    "  [grey]no clip in {0} has a track group named {1}; export binds nothing[/]",
                    Markup.Escape(catalog.Name), Markup.Escape(skeletonName));
                continue;
            }

            AnsiConsole.MarkupLine("  {0} clip(s) in {1} animate [bold]{2}[/] (export binds them):", clips.Count,
                Markup.Escape(catalog.Name), Markup.Escape(skeletonName));
            foreach (var clip in clips)
            {
                AnsiConsole.MarkupLine("    {0}: {1:0.###} s, {2} transform tracks", Markup.Escape(clip.Name),
                    clip.Duration, clip.TrackGroups.Sum(static group => group.TransformTracks.Count));
            }
        }
    }

    private static void PrintVanBurenGranny(VanBurenGrannyEntry decoded)
    {
        var file = decoded.File;
        AnsiConsole.MarkupLine("[bold]{0}[/] [grey](Granny 2 {1} file, {2})[/]", Markup.Escape(file.Name), decoded.Kind,
            Markup.Escape(file.ExporterName ?? "unknown exporter"));
        if (file.ArtToolInfo is { } tool)
        {
            AnsiConsole.MarkupLine("  tool {0} {1}.{2}, {3} units/m, right {4} up {5} back {6}",
                Markup.Escape(tool.ToolName), tool.MajorRevision, tool.MinorRevision, tool.UnitsPerMeter, tool.Right,
                tool.Up, tool.Back);
        }

        if (file.FromFileName is not null)
        {
            AnsiConsole.MarkupLine("  from {0}", Markup.Escape(file.FromFileName));
        }

        foreach (var skeleton in file.Skeletons)
        {
            AnsiConsole.MarkupLine(
                "  skeleton [bold]{0}[/]: {1} bones ({2} stored inverse-world matrices agree with the parent chain), roots {3}",
                Markup.Escape(skeleton.Name), skeleton.Bones.Count, skeleton.CountConsistentInverses(),
                Markup.Escape(string.Join(", ",
                    skeleton.Bones.Where(static bone => bone.ParentIndex < 0).Select(static bone => bone.Name))));
        }

        foreach (var model in file.Models)
        {
            AnsiConsole.MarkupLine("  model [bold]{0}[/]: skeleton {1}, {2} bound mesh(es)", Markup.Escape(model.Name),
                Markup.Escape(model.Skeleton?.Name ?? "none"), model.Meshes.Count);
        }

        foreach (var mesh in file.Meshes)
        {
            AnsiConsole.MarkupLine(
                "  mesh [bold]{0}[/]: {1:N0} vertices ({2}-byte {3}), {4:N0} triangles ({5}-bit indices), {6} group(s), {7} material binding(s) [[{8}]], {9} bone binding(s)",
                Markup.Escape(mesh.Name), mesh.Positions.Length, mesh.VertexStride, Markup.Escape(mesh.VertexLayout),
                mesh.TriangleCount, mesh.SixteenBitIndices ? 16 : 32,
                mesh.Groups.Count, mesh.MaterialNames.Count,
                Markup.Escape(string.Join(", ", mesh.MaterialNames.Select(static name => name ?? "null"))),
                mesh.BoneBindings.Count);
            if (mesh.TopologyDefect is not null)
            {
                AnsiConsole.MarkupLine("    [yellow]topology unwritten:[/] {0}", Markup.Escape(mesh.TopologyDefect));
            }
        }

        foreach (var clip in file.Animations)
        {
            var tracks = clip.TrackGroups.Sum(static group => group.TransformTracks.Count);
            var degrees = string.Join("/", clip.TrackGroups.SelectMany(static group => group.TransformTracks)
                .SelectMany(static track => new[] { track.Position, track.Orientation, track.ScaleShear })
                .Where(static curve => !curve.IsEmpty).Select(static curve => curve.Degree).Distinct().Order());
            AnsiConsole.MarkupLine(
                "  animation [bold]{0}[/]: {1:0.###} s, step {2:0.####} s, {3} track group(s) [[{4}]], {5} transform tracks, curve degree(s) {6}",
                Markup.Escape(clip.Name), clip.Duration, clip.TimeStep, clip.TrackGroups.Count,
                Markup.Escape(string.Join(", ", clip.TrackGroups.Select(static group => group.Name))), tracks,
                degrees.Length == 0 ? "none" : degrees);
        }

        if (file.TextureFileNames.Count > 0 || file.MaterialNames.Count > 0)
        {
            AnsiConsole.MarkupLine("  {0} texture(s) [[{1}]], {2} material(s)", file.TextureFileNames.Count,
                Markup.Escape(string.Join(", ", file.TextureFileNames.Select(static name => name ?? "null"))),
                file.MaterialNames.Count);
        }
    }

    /// <summary>
    ///     Exports one Granny payload to GLB: its skeleton as nodes, its meshes skinned to them, and
    ///     every animation clip in the same archive whose track group names the skeleton. Untextured:
    ///     no Van Buren Granny payload carries a texture.
    /// </summary>
    private static void RunVanBurenGrannyExport(VanBurenGrannyCatalog catalog, VanBurenGrannyEntry found,
        string outputDir)
    {
        if (found.Kind == VanBurenGrannyKind.Animation)
        {
            throw new InvalidOperationException(
                $"{found.File.Name} is an animation file (track group {string.Join(", ", found.File.Animations.SelectMany(static clip => clip.TrackGroups).Select(static group => group.Name).Distinct())}); export the model or skeleton it animates and the clips come with it.");
        }

        var animations = new List<Gr2AnimationClip>();
        foreach (var skeletonName in found.File.Skeletons.Select(static skeleton => skeleton.Name)
                     .Distinct(StringComparer.Ordinal))
        {
            animations.AddRange(catalog.AnimationsFor(skeletonName));
        }

        var invalid = Path.GetInvalidFileNameChars();
        var stem = string.Concat(found.DisplayName.Select(c => invalid.Contains(c) ? '_' : c));
        var outputPath = Path.Combine(outputDir, stem + ".glb");
        var summary = Gr2ModelGlbExporter.Write(found.File, outputPath, animations);
        PrintVanBurenGranny(found);
        AnsiConsole.MarkupLine(
            "[green]Wrote[/] {0} [grey]({1} model(s), {2} mesh(es) of which {3} skinned, {4} bones, {5:N0} triangles, {6} animation clip(s) over {7} tracks; source basis {8}; untextured — no Van Buren Granny payload carries a texture)[/]",
            Markup.Escape(outputPath), summary.Models, summary.Meshes, summary.SkinnedMeshes, summary.Bones,
            summary.Triangles, summary.AnimationClips, summary.AnimatedTracks,
            summary.LeftHandedSource ? "left-handed, winding reversed" : "right-handed");
        if (summary.UnwrittenMeshes > 0)
        {
            AnsiConsole.MarkupLine(
                "[yellow]{0} mesh(es) omitted:[/] their triangle list is unwritten exporter memory (0xBAADF00D fill); only the skeleton was written.",
                summary.UnwrittenMeshes);
        }
    }

    private static void RunMeshInfo(string input, string? entry)
    {
        var path = ResolveMeshPath(input);
        if (IsVanBurenGrpPath(path))
        {
            RunVanBurenMeshInfo(path, entry);
            return;
        }

        if (IsRedguardRobPath(path))
        {
            RunRedguardRobMeshInfo(path, entry);
            return;
        }

        if (Is3dcPath(path))
        {
            Run3dcMeshInfo(path);
            return;
        }

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
                : throw new InvalidOperationException(
                    $"'{entry}' is not an object id; {DaggerfallArch3DFile.FileName} records are numbered.");
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

        var table = new Table().Border(TableBorder.Rounded).AddColumn("Version")
            .AddColumn("Records", c => c.RightAligned());
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
        AnsiConsole.MarkupLine("[bold]Mesh {0}[/] [grey](record #{1}, {2})[/]", mesh.ObjectId, index,
            Markup.Escape(mesh.VersionTag));
        AnsiConsole.MarkupLine("  points {0}, planes {1}, triangles {2}, object-data entries {3}",
            mesh.Points.Count, mesh.Planes.Count, decomposed.TriangleCount, mesh.ObjectDataCount);
        AnsiConsole.MarkupLine("  radius {0:F2}, size {1:F2} x {2:F2} x {3:F2} units",
            mesh.RadiusUnits, size.X, size.Y, size.Z);
        AnsiConsole.MarkupLine("  textures: {0}", Markup.Escape(string.Join(", ",
            mesh.UniqueTextures.Select(t =>
                string.Create(CultureInfo.InvariantCulture, $"TEXTURE.{t.Archive:D3}#{t.Record}")))));

        var polygonSizes = mesh.Planes.GroupBy(p => p.Points.Count).OrderBy(g => g.Key)
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key}-gon x{g.Count()}"));
        AnsiConsole.MarkupLine("  polygons: {0}", Markup.Escape(string.Join(", ", polygonSizes)));
        PrintTextureReferences(mesh);
    }

    /// <summary>
    ///     Lists a mesh's distinct texture references, raw and split.
    ///     <para>
    ///         The split is Daggerfall's: archive = bits &gt;&gt; 7, record = bits &amp; 0x7F, which
    ///         names <c>TEXTURE.nnn</c>. Battlespire's field is a 32-bit dword instead — the base-40
    ///         encoding of the BSI stem (GAME.EXE <c>FUN_00075154</c>) — so for that layout the
    ///         decoded names are printed as well.
    ///     </para>
    /// </summary>
    private static void PrintTextureReferences(XnGineMesh mesh)
    {
        if (mesh.Layout == XnGineMeshLayout.Battlespire)
        {
            var keys = mesh.Planes.Select(p => p.TextureKey).Distinct().ToList();
            var names = keys.Select(BattlespireTextureName.Decode).OfType<string>().Order(StringComparer.Ordinal).ToList();
            var colors = keys.Count(BattlespireTextureName.IsSolidColor);
            AnsiConsole.MarkupLine("  texture names ({0}): {1}{2}", names.Count, Markup.Escape(string.Join(", ", names)),
                colors > 0
                    ? string.Create(CultureInfo.InvariantCulture, $" + {colors} solid-colour key(s)")
                    : string.Empty);
        }

        var references = mesh.Planes
            .Select(p => p.TextureBits)
            .Distinct()
            .Order()
            .Select(bits => string.Create(
                CultureInfo.InvariantCulture, $"{bits} (archive {bits >> 7}, record {bits & 0x7F})"))
            .ToList();

        AnsiConsole.MarkupLine(
            "  texture refs ({0}): {1}", references.Count, Markup.Escape(string.Join(", ", references)));

        // Battlespire's plane header is 10 bytes to Daggerfall's 8, so it carries fields this
        // reader does not name. Showing the distinct tails is what makes the extra fields
        // measurable instead of invisible.
        var tails = mesh.Planes
            .Select(p => Convert.ToHexString(p.HeaderTail.Span))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .Take(8)
            .ToList();

        if (tails.Count > 0 && tails[0].Length > 0)
        {
            AnsiConsole.MarkupLine("  plane header tails ({0} distinct): {1}",
                mesh.Planes.Select(p => Convert.ToHexString(p.HeaderTail.Span)).Distinct().Count(),
                Markup.Escape(string.Join(", ", tails)));
        }
    }

    /// <summary>Describes one Battlespire mesh, or censuses its archive.</summary>
    private static void RunBattlespireMeshInfo(string path, string? entry)
    {
        var name = Path.GetFileName(path);
        if (IsLooseMeshName(name))
        {
            PrintXnGineMesh(name, BattlespireMeshArchive.ParseLoose(
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

            PrintXnGineMesh(archive.EntryName(index), archive.Parse(index));
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

    /// <summary>
    ///     Summarises a Redguard <c>.ROB</c>, or one named segment inside it. The empty placeholder
    ///     segments are reported separately rather than counted as failures — a fifth of every
    ///     retail archive is made of them.
    /// </summary>
    private static void RunRedguardRobMeshInfo(string path, string? entry)
    {
        var name = Path.GetFileName(path);
        using var archive = RedguardRobMeshArchive.Open(path);

        if (entry is not null)
        {
            var index = archive.IndexOf(entry);
            if (index < 0)
            {
                throw new InvalidOperationException($"No segment named '{entry}' in {name}.");
            }

            PrintXnGineMesh(archive.EntryName(index), archive.Parse(index));
            return;
        }

        var planes = 0;
        var points = 0;
        var empty = 0;
        var failures = new List<string>();
        for (var i = 0; i < archive.Count; i++)
        {
            if (archive.IsEmpty(i))
            {
                empty++;
                continue;
            }

            if (!archive.TryParse(i, out var mesh, out var error))
            {
                failures.Add($"{archive.EntryName(i)}: {error}");
                continue;
            }

            planes += mesh.Planes.Count;
            points += mesh.Points.Count;
        }

        AnsiConsole.MarkupLine(
            "[bold cyan]{0}[/] — [grey]{1:N0} segments ({2:N0} empty), {3:N0} points, {4:N0} planes[/]",
            Markup.Escape(name), archive.Count, empty, points, planes);
        foreach (var failure in failures)
        {
            AnsiConsole.MarkupLine("[red]unparsed[/] {0}", Markup.Escape(failure));
        }
    }

    /// <summary>Exports one <c>.ROB</c> segment to GLB. Untextured: Redguard's TEXBSI art is not decoded yet.</summary>
    private static void RunRedguardRobMeshExport(string path, string? entry, string outputDir)
    {
        var name = Path.GetFileName(path);
        if (entry is null)
        {
            throw new InvalidOperationException($"{name} holds many meshes — pass --entry <name>.");
        }

        using var archive = RedguardRobMeshArchive.Open(path);
        var index = archive.IndexOf(entry);
        if (index < 0)
        {
            throw new InvalidOperationException($"No segment named '{entry}' in {name}.");
        }

        var mesh = archive.Parse(index);
        var outputPath = Path.Combine(outputDir, archive.EntryName(index).ToUpperInvariant() + ".glb");
        XnGineMeshGlbExporter.Write(XnGineMeshDecomposer.Decompose(mesh), outputPath);

        PrintXnGineMesh(archive.EntryName(index), mesh);
        AnsiConsole.MarkupLine(
            "[green]Wrote[/] {0} [grey](untextured: Redguard's textures live in the undecoded TEXTURE.### / TEXBSI art)[/]",
            Markup.Escape(outputPath));
    }

    private static void PrintXnGineMesh(string name, XnGineMesh mesh)
    {
        var decomposed = XnGineMeshDecomposer.Decompose(mesh);
        var size = mesh.Size;
        AnsiConsole.MarkupLine("[bold]{0}[/] [grey]({1}, {2} layout)[/]",
            Markup.Escape(name), Markup.Escape(mesh.VersionTag), mesh.Layout);
        AnsiConsole.MarkupLine("  points {0}, planes {1}, triangles {2}, textures {3}",
            mesh.Points.Count, mesh.Planes.Count, decomposed.TriangleCount, mesh.UniqueTextures.Count);
        AnsiConsole.MarkupLine("  radius {0:F2}, size {1:F2} x {2:F2} x {3:F2} units", mesh.RadiusUnits, size.X, size.Y,
            size.Z);

        var polygonSizes = mesh.Planes.GroupBy(p => p.Points.Count).OrderBy(g => g.Key)
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key}-gon x{g.Count()}"));
        AnsiConsole.MarkupLine("  polygons: {0}", Markup.Escape(string.Join(", ", polygonSizes)));
        PrintTextureReferences(mesh);
    }

    /// <summary>
    ///     Exports one Battlespire mesh to GLB, textured through <see cref="BattlespireTextureResolver" />
    ///     when <c>BSI.BSA</c> sits beside the mesh source (a GAMEDATA directory).
    /// </summary>
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
        using var textures = BattlespireTextureResolver.Open(Path.GetDirectoryName(Path.GetFullPath(path))!);
        XnGineMeshGlbExporter.Write(decomposed, outputPath, textures.Resolve);

        PrintXnGineMesh(name, mesh);
        if (!textures.HasArchive)
        {
            AnsiConsole.MarkupLine(
                "[green]Wrote[/] {0} [grey](untextured: no BSI.BSA beside the mesh source)[/]",
                Markup.Escape(outputPath));
            return;
        }

        AnsiConsole.MarkupLine("[green]Wrote[/] {0} [grey](textured through BSI.BSA)[/]", Markup.Escape(outputPath));
        if (textures.MissingNames.Count > 0)
        {
            AnsiConsole.MarkupLine("[yellow]{0} texture name(s) absent from BSI.BSA:[/] {1}",
                textures.MissingNames.Count, Markup.Escape(string.Join(", ", textures.MissingNames)));
        }
    }

    private static void RunMeshExport(string input, string? entry, string outputDir)
    {
        var path = ResolveMeshPath(input);
        if (IsVanBurenGrpPath(path))
        {
            RunVanBurenMeshExport(path, entry, outputDir);
            return;
        }

        if (IsRedguardRobPath(path))
        {
            RunRedguardRobMeshExport(path, entry, outputDir);
            return;
        }

        if (Is3dcPath(path))
        {
            Run3dcMeshExport(path, outputDir);
            return;
        }

        if (IsBattlespireMeshPath(path))
        {
            RunBattlespireMeshExport(path, entry, outputDir);
            return;
        }

        if (entry is null || !uint.TryParse(entry, NumberStyles.None, CultureInfo.InvariantCulture, out var objectId))
        {
            throw new InvalidOperationException(
                $"Pass --entry <object id>; {DaggerfallArch3DFile.FileName} records are numbered.");
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
        var inputArg = new Argument<string>("input")
            { Description = "A .MIF or .RMD file, or an archive with --entry" };
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

    /// <summary>
    ///     Describes a Redguard <c>.WLD</c> by what its layers mean (see <see cref="RedguardWldFile" />):
    ///     the tile records, the height range through the game's table, the surface texture census
    ///     and the two layers retail leaves empty.
    /// </summary>
    private static void RunRedguardWldInfo(RedguardWldFile wld)
    {
        AnsiConsole.MarkupLine(
            "[bold cyan]{0}[/] — Redguard scape: {1}x{2} tiles of {3}x{3} cells, {4} layers of {5}x{5}, {6} world units per cell; texture set {7}",
            Markup.Escape(wld.Name), wld.TilesX, wld.TilesZ, RedguardWldFile.TileSize, RedguardWldFile.LayerCount,
            RedguardWldFile.MapSize, RedguardWldFile.WorldUnitsPerCell, wld.TextureSet);
        AnsiConsole.MarkupLine("  header {0}; trailer TULO {1}", Markup.Escape(string.Join(", ", wld.Header)),
            Markup.Escape(string.Join(", ", wld.Trailer)));
        AnsiConsole.MarkupLine(
            "  world -> cell: x >> 8, (65536 - z) >> 8 (rows run from high z to low z); world Y = -height table[index]");

        var tileTable = new Table().Border(TableBorder.Rounded)
            .AddColumn("Tile").AddColumn("Grid").AddColumn("Offset", c => c.RightAligned())
            .AddColumn("Word0", c => c.RightAligned())
            .AddColumn("Set", c => c.RightAligned()).AddColumn("Level", c => c.RightAligned()).AddColumn("Flags")
            .AddColumn("Stored");
        foreach (var tile in wld.Tiles)
        {
            tileTable.AddRow(
                tile.Index.ToString(CultureInfo.InvariantCulture),
                $"({tile.TileX}, {tile.TileZ})",
                tile.Offset.ToString(CultureInfo.InvariantCulture),
                tile.Word0.ToString(CultureInfo.InvariantCulture),
                tile.TextureSet.ToString(CultureInfo.InvariantCulture),
                tile.LevelIndex.ToString(CultureInfo.InvariantCulture),
                $"0x{tile.Flags:X2}",
                tile.IsStored ? "payload" : "procedural seed");
        }

        AnsiConsole.Write(tileTable);

        var layerTable = new Table().Border(TableBorder.Rounded)
            .AddColumn("Layer").AddColumn("Meaning").AddColumn("Distinct", c => c.RightAligned())
            .AddColumn("Non-zero", c => c.RightAligned()).AddColumn("Detail");
        for (var i = 0; i < wld.Layers.Count; i++)
        {
            var layer = wld.Layers[i];
            var nonZero = layer.Indices.Count(b => b != 0);
            var detail = i switch
            {
                0 => HeightDetail(wld),
                1 => nonZero == 0
                    ? "empty (retail never places scatter flats)"
                    : "bits 2-7 flat kind, bits 0-1 separate",
                2 => SurfaceDetail(wld),
                _ => nonZero == 0 ? "empty (unread at runtime)" : "non-zero — unread at runtime"
            };
            layerTable.AddRow(
                i.ToString(CultureInfo.InvariantCulture),
                RedguardWldFile.LayerNames[i],
                layer.Indices.Distinct().Count().ToString(CultureInfo.InvariantCulture),
                ((double)nonZero / layer.Indices.Length).ToString("P1", CultureInfo.InvariantCulture),
                Markup.Escape(detail));
        }

        AnsiConsole.Write(layerTable);
    }

    private static string HeightDetail(RedguardWldFile wld)
    {
        var min = int.MaxValue;
        var max = int.MinValue;
        var stored = 0;
        var computed = 0;
        for (var z = 0; z < RedguardWldFile.MapSize; z++)
        {
            for (var x = 0; x < RedguardWldFile.MapSize; x++)
            {
                var index = wld.HeightIndexAt(x, z);
                min = Math.Min(min, index);
                max = Math.Max(max, index);
                if (wld.StoredQuadSplitAt(x, z))
                {
                    stored++;
                }

                if (x < RedguardWldFile.MapSize - 1 && z < RedguardWldFile.MapSize - 1 && wld.ComputedQuadSplitAt(x, z))
                {
                    computed++;
                }
            }
        }

        return
            $"index {min}..{max} = world Y {-RedguardWldFile.HeightTable[max]}..{-RedguardWldFile.HeightTable[min]}; quad-split bit stored on {stored} cells, loader rule sets {computed}";
    }

    private static string SurfaceDetail(RedguardWldFile wld)
    {
        var textures = new int[64];
        var rotations = new int[4];
        for (var z = 0; z < RedguardWldFile.MapSize; z++)
        {
            for (var x = 0; x < RedguardWldFile.MapSize; x++)
            {
                textures[wld.SurfaceTextureAt(x, z)]++;
                rotations[wld.SurfaceRotationAt(x, z)]++;
            }
        }

        var top = textures.Select((count, index) => (index, count)).Where(t => t.count > 0)
            .OrderByDescending(t => t.count).Take(6)
            .Select(t => $"{t.index}:{t.count}");
        return
            $"TEXTURE.{wld.TextureSet:000} record (bits 0-5, {textures.Count(c => c > 0)} used, top {string.Join(' ', top)}); rotation quarter-turns {string.Join('/', rotations)}";
    }

    /// <summary>
    ///     Describes a Fallout <c>.MAP</c>: its header, the tile grid of each present elevation,
    ///     and the decoded scripts. Reports the remaining object section, which needs prototype
    ///     information to decode.
    /// </summary>
    private static void RunFalloutMapInfo(byte[] bytes, string name)
    {
        var map = FalloutMapFile.Parse(bytes, name);

        AnsiConsole.MarkupLine(
            "[bold cyan]{0}[/] — [grey]Fallout map v{1}, {2} of 3 elevations (flags 0x{3:X})[/]",
            Markup.Escape(map.MapName), map.Version, map.Elevations.Count, map.ElevationFlags);
        AnsiConsole.MarkupLine("  player starts on tile {0}, elevation {1}, facing {2}; script {3}",
            map.PlayerPosition, map.PlayerElevation, map.PlayerOrientation,
            map.HasScript ? map.ScriptId.ToString(CultureInfo.InvariantCulture) : "none");

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Elevation")
            .AddColumn("Floor tiles", c => c.RightAligned())
            .AddColumn("Roofed", c => c.RightAligned())
            .AddColumn("Distinct floors", c => c.RightAligned())
            .AddColumn("Distinct roofs", c => c.RightAligned());
        foreach (var elevation in map.Elevations)
        {
            // Tile 1 is the empty tile, so "used" means anything else - that is what makes an
            // elevation's grid worth drawing.
            table.AddRow(
                elevation.Index.ToString(CultureInfo.InvariantCulture),
                elevation.Tiles.Count(t => t.HasFloor).ToString("N0", CultureInfo.InvariantCulture),
                elevation.Tiles.Count(t => t.HasRoof).ToString("N0", CultureInfo.InvariantCulture),
                elevation.Tiles.Select(t => t.FloorId).Distinct().Count().ToString("N0", CultureInfo.InvariantCulture),
                elevation.Tiles.Select(t => t.RoofId).Distinct().Count().ToString("N0", CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            "[grey]{0:N0} global variables, {1:N0} local variables; {2:N0} scripts decoded.[/]",
            map.GlobalVariables.Count, map.LocalVariables.Count, map.Scripts.Count(script => !script.IsPadding));
        if (!map.Undecoded.IsEmpty)
        {
            AnsiConsole.MarkupLine(
                "[grey]{0:N0} bytes of object data remain undecoded (prototype information required).[/]",
                map.Undecoded.Length);
        }
    }

    private static void RunMapInfo(string input, string? entryName)
    {
        var (bytes, name) = LoadMapSource(input, entryName);

        if (VanBurenMapFile.IsMapFile(bytes))
        {
            // A Van Buren EMAP needs its archive too: the scene and walk grid it names are
            // sibling entries, paired through resource.rht beside data/ when present.
            VanBurenMapCommandSupport.Info(VanBurenMapCommandSupport.Load(bytes, name, input, entryName is not null));
            return;
        }

        if (TacticsMissionFile.IsMission(bytes))
        {
            // A Tactics mission names its tiles by path; the install around the input supplies them.
            TacticsMapCommandSupport.Info(TacticsMapCommandSupport.Load(bytes, name, input));
            return;
        }

        if (FalloutMapFile.IsMapFile(bytes))
        {
            RunFalloutMapInfo(bytes, name);
            return;
        }

        if (RedguardWldFile.IsWldFile(bytes))
        {
            RunRedguardWldInfo(RedguardWldFile.Parse(bytes, name));
            return;
        }

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
        if (VanBurenMapFile.IsMapFile(bytes))
        {
            layers = VanBurenMapCommandSupport.Export(
                VanBurenMapCommandSupport.Load(bytes, name, input, entryName is not null), outputDir, scale);
        }
        else if (TacticsMissionFile.IsMission(bytes))
        {
            layers = TacticsMapCommandSupport.Export(TacticsMapCommandSupport.Load(bytes, name, input), outputDir, scale);
        }
        else if (FalloutMapFile.IsMapFile(bytes))
        {
            layers = FalloutMapRenderer.RenderElevations(FalloutMapFile.Parse(bytes, name), outputDir, scale);
        }
        else if (RedguardWldFile.IsWldFile(bytes))
        {
            var wld = RedguardWldFile.Parse(bytes, name);
            var art = RedguardMapRenderer.TryLocateArt(wld, entryName is null ? input : null);
            if (art is null)
            {
                AnsiConsole.MarkupLine(
                    "[yellow]No Redguard install found around the file: the surface is rendered in diagnostic hues, not TEXTURE.{0:000}.[/]",
                    wld.TextureSet);
            }

            layers = RedguardMapRenderer.RenderTerrain(wld, outputDir, scale, art);
        }
        else if (DaggerfallWoodsFile.IsWoodsFileName(name))
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
            Description =
                "Install/data directory, or a single TEMPLATE.DAT, .INF, TEXT.RSC, BOKnnnnn.TXT, .QRC or ENGLISH.RTX file"
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

    private static void Run(string input, string source, string? filter, int limit)
    {
        if (Array.IndexOf(TextSources, source) < 0)
        {
            throw new InvalidOperationException($"Unknown --source '{source}'. Use {string.Join(", ", TextSources)}.");
        }

        var printed = 0;

        if (File.Exists(input))
        {
            if (RedguardRtxFile.IsRtxFile(input))
            {
                RunRedguardText(input, filter, limit, ref printed);
            }
            else
            {
                PrintFile(input, filter, limit, ref printed);
            }

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
                RunDaggerfall(HostPath.ResolveDirectory(root, profile.ClassicLooseRoot), source is "all" or "text",
                    source is "all" or "books", source is "all" or "quests", filter, limit, ref printed);
                break;
            case BethesdaGame.Redguard:
                RunRedguardText(
                    HostPath.TryResolveExisting(root, profile.ClassicLooseRoot + "\\" + RedguardRtxFile.FileName)
                    ?? HostPath.Combine(root, profile.ClassicLooseRoot + "\\" + RedguardRtxFile.FileName),
                    filter, limit, ref printed);
                break;
            default:
                throw new NotSupportedException(
                    $"'classic text' does not read {profile.Game} yet — its text formats land with its game vertical. " +
                    "Arena, Daggerfall and Redguard are supported today.");
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
            && string.Equals(Path.GetFileName(directory), parentProfile.ClassicLooseRoot,
                StringComparison.OrdinalIgnoreCase))
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
            PrintQuest(DaggerfallQuestFile.Create(Path.GetFileNameWithoutExtension(name), bytes, null), filter,
                ref printed);
        }
        else
        {
            PrintTemplate(ArenaTemplateDat.Parse(bytes), filter, limit, ref printed);
        }
    }

    private static void RunArena(string root, bool wantTemplate, bool wantInf, string? filter, int limit,
        ref int printed)
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

                PrintInf(ArenaInfFile.ParseText(Encoding.Latin1.GetString(plain), name),
                    filter, limit, ref printed);
            }
        }
    }

    /// <summary>
    ///     Redguard's text is one database, <c>ENGLISH.RTX</c>: every line keyed by the 4-character
    ///     label the scripts use, most of them voiced. Printed in file order as <c>tag  text</c>, with
    ///     the voice's rate and length when there is one; <c>--filter</c> matches the tag or the text.
    /// </summary>
    private static void RunRedguardText(string path, string? filter, int limit, ref int printed)
    {
        if (!File.Exists(path))
        {
            return;
        }

        using var database = RedguardRtxFile.Open(path);
        AnsiConsole.MarkupLine("[bold]{0}[/] — [grey]{1:N0} records, {2:N0} voiced[/]",
            Markup.Escape(database.Name), database.Entries.Count, database.Entries.Count(e => e.IsVoiced));

        foreach (var entry in database.Entries)
        {
            if (limit > 0 && printed >= limit)
            {
                break;
            }

            if (filter is not null &&
                !entry.Text.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !entry.Tag.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var voice = entry.Sound is { } sound
                ? string.Create(CultureInfo.InvariantCulture,
                    $"  [grey]({sound.SampleRate} Hz, {sound.DurationSeconds:F1}s)[/]")
                : string.Empty;
            AnsiConsole.MarkupLine("[cyan]{0}[/]  {1}{2}", Markup.Escape(entry.Tag), Markup.Escape(entry.Text), voice);
            printed++;
        }
    }

    private static void RunDaggerfall(string dataRoot, bool wantText, bool wantBooks, bool wantQuests, string? filter,
        int limit, ref int printed)
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

                PrintBook(DaggerfallBookFile.Parse(File.ReadAllBytes(path), Path.GetFileName(path)), filter, limit,
                    ref printed);
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
