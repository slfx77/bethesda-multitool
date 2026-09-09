using System.Globalization;
using BethesdaMultitool.CLI.Rendering.Map;
using BethesdaMultitool.Core.Formats.VanBuren;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Classic;

/// <summary>
///     The <c>classic map info</c> / <c>classic map export</c> legs for a Van Buren <c>EMAP</c>,
///     which needs more than its own bytes: the scene and walk grid its header names live as
///     other entries of the same <c>.grp</c>, resolved through <c>resource.rht</c> beside
///     <c>data/</c> when it is there.
/// </summary>
internal static class VanBurenMapCommandSupport
{
    /// <summary>Parses the map and resolves its companions from the archive it was read from.</summary>
    /// <param name="bytes">The <c>EMAP</c> payload.</param>
    /// <param name="name">Its entry name.</param>
    /// <param name="archivePath">The <c>.grp</c> it came from, or a loose file's path.</param>
    /// <param name="fromArchive">Whether <paramref name="archivePath" /> is a <c>.grp</c> holding siblings.</param>
    public static VanBurenMapLevel Load(byte[] bytes, string name, string archivePath, bool fromArchive)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(archivePath);

        var map = VanBurenMapFile.Parse(bytes, name);
        if (!fromArchive)
        {
            return VanBurenMapCompanions.Resolve(map, Path.GetFileNameWithoutExtension(archivePath), [], _ => null,
                null);
        }

        var archiveBytes = File.ReadAllBytes(archivePath);
        var archive = VanBurenGrpArchive.Parse(archiveBytes, Path.GetFileName(archivePath));
        var candidates = archive.Entries
            .Select(e => new VanBurenMapCompanions.Candidate(e.Index, e.Name, e.Tag))
            .ToList();
        var byName = archive.Entries.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var index = VanBurenResourceIndex.TryLoadBeside(archivePath);
        return VanBurenMapCompanions.Resolve(
            map,
            Path.GetFileNameWithoutExtension(archivePath),
            candidates,
            entryName => byName.TryGetValue(entryName, out var entry)
                ? VanBurenGrpArchive.Read(archiveBytes, entry)
                : null,
            index);
    }

    /// <summary>Describes a level: its header trio, companions, chunk census and placements.</summary>
    public static void Info(VanBurenMapLevel level)
    {
        ArgumentNullException.ThrowIfNull(level);
        var map = level.Map;
        var header = map.Header;

        AnsiConsole.MarkupLine(
            "[bold cyan]{0}[/] — [grey]Van Buren EMAP v{1}, {2} chunks; scene {3}, walk grid {4}, area map {5}[/]",
            Markup.Escape(level.Stem), header.Version, map.Chunks.Count,
            Markup.Escape(header.SceneName), Markup.Escape(header.WalkGridName), Markup.Escape(header.AreaMapName));
        AnsiConsole.MarkupLine(
            "  companions paired by {0}: scene {1}, walk grid {2}",
            Markup.Escape(level.PairedBy),
            Markup.Escape(level.SceneEntryName ?? "none"),
            Markup.Escape(level.WalkGridEntryName ?? "none"));
        if (level.HasFrame)
        {
            AnsiConsole.MarkupLine(
                "  frame: {0}x{1} cells of {2} units from ({3:0.##}, {4:0.##}) to ({5:0.##}, {6:0.##})",
                level.GridWidth, level.GridHeight, level.CellSize,
                level.Origin!.Value.X, level.Origin.Value.Z, level.Extent!.Value.X, level.Extent.Value.Z);
        }
        else
        {
            AnsiConsole.MarkupLine(
                "  [grey]no scene found, so no world frame — the plan is framed by its placements[/]");
        }

        if (level.WalkGrid is { } grid)
        {
            var blocked = grid.Cells.Count(c => c == VanBurenWalkGrid.Blocked);
            AnsiConsole.MarkupLine(
                "  walk grid: {0}x{1}, {2} runs, {3} distinct values, {4:N0} of {5:N0} cells blocked (47) — [grey]values are diagnostic, meaning not established[/]",
                grid.Width, grid.Height, grid.RunCount, grid.Cells.Distinct().Count(), blocked, grid.Cells.Length);
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Kind").AddColumn("Count", c => c.RightAligned()).AddColumn("Detail");
        table.AddRow("Entities (EME2)", map.Entities.Count.ToString(CultureInfo.InvariantCulture),
            Markup.Escape(string.Join(", ", map.Entities.Select(e => e.Template).Distinct().Take(6))));
        table.AddRow("Entry points (EMEP)",
            map.EntryPoints.Count(p => p.Position != default).ToString(CultureInfo.InvariantCulture),
            map.EntryPoints.Count > 0 ? $"{map.EntryPoints.Count} slots" : string.Empty);
        table.AddRow("Triggers (EMTR)", map.Triggers.Count.ToString(CultureInfo.InvariantCulture),
            Markup.Escape(string.Join(", ",
                map.Triggers.Select(t =>
                        $"kind {t.Kind} {t.Detail.Tag}{(t.Detail.Name is null ? string.Empty : " " + t.Detail.Name)}")
                    .Distinct().Take(6))));
        table.AddRow("Way-point paths (EPTH)", map.Paths.Count.ToString(CultureInfo.InvariantCulture),
            Markup.Escape(string.Join(", ", map.Paths.Select(p => $"{p.Name} ({p.Points.Count})"))));
        table.AddRow("Nav points (EMNP)", map.NavPoints.Count.ToString(CultureInfo.InvariantCulture), string.Empty);
        table.AddRow("Effects (EMEF)", map.Effects.Count.ToString(CultureInfo.InvariantCulture),
            Markup.Escape(string.Join(", ", map.Effects.SelectMany(e => e.Effects).Distinct().Take(4))));
        table.AddRow("Sounds (EMSD)", map.Sounds.Count.ToString(CultureInfo.InvariantCulture),
            Markup.Escape(string.Join(", ", map.Sounds.Select(s => s.File).Distinct().Take(4))));
        table.AddRow("Notes (EMNO)", map.Notes.Count.ToString(CultureInfo.InvariantCulture),
            Markup.Escape(string.Join(", ", map.Notes.Select(n => n.Icon).Distinct())));
        table.AddRow("Water (2MWT)", map.Waters.Count.ToString(CultureInfo.InvariantCulture),
            Markup.Escape(string.Join(", ", map.Waters.Select(w => w.Name))));
        table.AddRow("Camera constraints (ECAM)", map.CameraConstraints is null ? "0" : "1",
            map.CameraConstraints is { } cam
                ? string.Join(", ", cam.Select(v => v.ToString("0.##", CultureInfo.InvariantCulture)))
                : string.Empty);
        AnsiConsole.Write(table);

        if (map.UnclaimedTags.Count > 0)
        {
            AnsiConsole.MarkupLine("[grey]Chunks kept but not decoded: {0}[/]",
                Markup.Escape(string.Join(", ", map.UnclaimedTags)));
        }
    }

    /// <summary>Writes the level's PNGs.</summary>
    public static IReadOnlyList<ArenaMapRenderer.RenderedLayer> Export(VanBurenMapLevel level, string outputDir,
        int scale)
    {
        return VanBurenMapRenderer.RenderLevel(level, outputDir, scale);
    }
}
