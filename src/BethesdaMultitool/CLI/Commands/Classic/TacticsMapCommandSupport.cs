using BethesdaMultitool.CLI.Rendering.Map;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Tactics;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Rendering.Level2D;
using BethesdaMultitool.Core.Vfs;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Classic;

/// <summary>
///     The <c>classic map info</c> / <c>classic map export</c> legs for a Fallout Tactics
///     <c>.mis</c>. A mission names its tiles by <c>tiles/…</c> path, so the export mounts the
///     install around the input (loose <c>core\</c> over <c>core\*.bos</c>, the profile's own
///     precedence) and reads each tile through it; without an install the level is still drawn,
///     as type-coloured blocks, and says so.
/// </summary>
internal static class TacticsMapCommandSupport
{
    /// <summary>A parsed mission plus the install its tiles resolve through, when one was found.</summary>
    public sealed record Loaded(TacticsMissionFile Mission, TacticsMissionWorld World, string? InstallRoot);

    /// <summary>Parses the mission and locates the Tactics install the input sits in.</summary>
    /// <param name="bytes">The <c>.mis</c> bytes.</param>
    /// <param name="name">Its file name.</param>
    /// <param name="inputPath">The path given on the command line — a loose file or the archive holding it.</param>
    public static Loaded Load(byte[] bytes, string name, string inputPath)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(inputPath);

        var mission = TacticsMissionFile.Parse(bytes, name);
        var world = TacticsMissionWorld.Parse(mission);
        var located = ClassicGameLocator.DetectRootForFile(Path.GetFullPath(inputPath));
        var root = located is { Profile.Game: BethesdaGame.FalloutTactics } ? located.Value.Root : null;
        return new Loaded(mission, world, root);
    }

    /// <summary>Describes a mission: header, grid, tile and entity censuses, zones, triggers.</summary>
    public static void Info(Loaded loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        var world = loaded.World;

        AnsiConsole.MarkupLine(
            "[bold cyan]{0}[/] — [grey]Fallout Tactics mission, container v{1}, world v{2}; {3} regions of {4} units ({5}x{6}), bounds {7}[/]",
            Markup.Escape(loaded.Mission.Name), loaded.Mission.Version, world.HeaderVersion,
            world.RegionsX * world.RegionsZ, TacticsMissionWorld.RegionSize, world.RegionsX, world.RegionsZ,
            string.Join(",", world.Bounds));
        AnsiConsole.MarkupLine(
            "  teams: {0}; squad: {1}; speech: {2}",
            Markup.Escape(string.Join(", ", world.Teams)),
            Markup.Escape(string.Join(", ", world.Squads)),
            Markup.Escape(world.SpeechFile.Length == 0 ? "none" : world.SpeechFile));
        AnsiConsole.MarkupLine(
            "  tile table: {0} entries; {1} placed tiles; y range {2}..{3}",
            world.TilePaths.Count - 1, world.Instances.Count,
            world.Instances.Count == 0 ? 0 : world.Instances.Min(i => i.Y),
            world.Instances.Count == 0 ? 0 : world.Instances.Max(i => i.Y));

        var byType = world.Instances
            .GroupBy(i => world.TileHeaders[i.TileIndex].Type)
            .OrderByDescending(g => g.Count());
        foreach (var group in byType)
        {
            AnsiConsole.MarkupLine("    {0,-8} {1,8}", group.Key, group.Count());
        }

        var placed = world.PlacedEntities.ToList();
        AnsiConsole.MarkupLine(
            "  entities: {0} of {1} slots ({2} classes); {3} placed in the world",
            world.Entities.Count, world.EntitySlotCapacity, world.EntityClasses.Count, placed.Count);
        foreach (var group in placed.GroupBy(e => e.ClassName).OrderByDescending(g => g.Count()).Take(12))
        {
            AnsiConsole.MarkupLine("    {0,-16} {1,6}", Markup.Escape(group.Key), group.Count());
        }

        AnsiConsole.MarkupLine(
            "  zones {0}: {1}",
            world.Zones.Count, Markup.Escape(string.Join(", ", world.Zones.Select(z => z.Name))));
        AnsiConsole.MarkupLine(
            "  players {0}, triggers {1}, variables {2}, objectives {3}, speech nodes {4}, ambient sounds {5}",
            world.Players.Count, world.Triggers.Count, world.VariableNames.Count, world.Objectives.Count,
            world.SpeechNodeCount, world.AmbientSounds.Count);
        AnsiConsole.MarkupLine(
            loaded.InstallRoot is null
                ? "  [yellow]no Fallout Tactics install around the input: tiles would draw as type-coloured blocks[/]"
                : "  [grey]tiles resolve through {0}[/]",
            Markup.Escape(loaded.InstallRoot ?? string.Empty));
    }

    /// <summary>Writes one PNG per layer and reports them.</summary>
    public static IReadOnlyList<ArenaMapRenderer.RenderedLayer> Export(Loaded loaded, string outputDir, int scale)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(outputDir);
        Directory.CreateDirectory(outputDir);

        using var files = loaded.InstallRoot is null
            ? null
            : GameFileSystem.OpenGameRoot(GameProfiles.For(BethesdaGame.FalloutTactics), loaded.InstallRoot);
        if (files is null)
        {
            AnsiConsole.MarkupLine(
                "[yellow]No Fallout Tactics install found around the input: tiles are drawn as type-coloured blocks, not their art.[/]");
        }

        // `scale` is pixels per unit elsewhere; here the picture is authored at the game's own
        // pixel scale, so the option bounds the longest edge instead: scale 1 = 4096 px.
        var maxEdge = Math.Clamp(TacticsMissionLevel2DSource.MaxRasterEdge * Math.Max(1, scale), 256, 16384);
        var source = new TacticsMissionLevel2DSource(
            loaded.World,
            path => files?.TryReadAllBytes(path),
            Path.GetFileNameWithoutExtension(loaded.Mission.Name),
            maxEdge);

        var layers = new List<ArenaMapRenderer.RenderedLayer>();
        foreach (var layer in source.Layers)
        {
            var render = source.Render(layer);
            if (render is null)
            {
                continue;
            }

            var path = Path.Combine(outputDir, source.DisplayName + Level2DViewPolicy.LayerStemSuffix(layer) + ".png");
            PngWriter.SaveRgba(render.Value.Rgba, render.Value.Width, render.Value.Height, path);
            layers.Add(new ArenaMapRenderer.RenderedLayer(
                path, Level2DViewPolicy.LayerLabel(layer), render.Value.Width, render.Value.Height, source.ResolvedTiles));
        }

        AnsiConsole.MarkupLine(
            "[grey]{0} of {1} distinct tiles resolved; full-size picture {2}x{3}, divisor {4}[/]",
            source.ResolvedTiles, source.ResolvedTiles + source.UnresolvedTiles, source.FullWidth, source.FullHeight,
            source.Divisor);
        return layers;
    }
}
