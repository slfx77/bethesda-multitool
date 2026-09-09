using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Tactics;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Vfs;

namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>
///     Synthesizes records for Fallout Tactics: one <c>TMIS</c> per mission the install mounts.
///     Original RE 2026-09-08 (<see cref="TacticsMissionWorld" />); every Tactics reference is
///     GPL, so nothing is ported.
///     <para>
///         ⚑ The population is what the LAYERED mount sees, not the file count: 128 shipped
///         missions (103 archived + 25 loose) collapse to <b>116 distinct paths</b>, because the
///         12 loose campaign missions under <c>core/campaigns/missions/core</c> shadow their
///         archived copies — the 1.27 patch's overrides — exactly as the game resolves them.
///         A census that walks archives and the loose tree separately says 128; this says 116.
///     </para>
///     <para>
///         FormIDs are keyed by the mission's PATH through <see cref="ClassicNameHash" /> (24 bits)
///         so a <c>diff</c> between two installs compares like with like; a collision is resolved
///         by stepping to the next free id and is impossible to hide (retail: 116 paths, 0 collisions).
///     </para>
/// </summary>
internal static class TacticsRecordSource
{
    /// <summary>Domain byte for the mission records — the Tactics block starts after the Fallout families' 0x50..0x55.</summary>
    public const byte MissionDomain = 0x58;

    /// <summary>Record type for one mission.</summary>
    public const string MissionRecordType = "TMIS";

    /// <summary>Reads every mission the install mounts and appends its record.</summary>
    public static void Populate(string installRoot, RecordCollection records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installRoot);
        ArgumentNullException.ThrowIfNull(records);

        using var files = GameFileSystem.OpenGameRoot(GameProfiles.For(BethesdaGame.FalloutTactics), installRoot);
        Populate(files, records, cancellationToken);
    }

    /// <summary>Reads every mission a mounted install exposes and appends its record.</summary>
    public static void Populate(IGameFileSystem files, RecordCollection records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(records);

        var seen = new HashSet<string>(VfsPath.Comparer);
        var used = new HashSet<uint>();
        foreach (var entry in files.EnumerateFiles().OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.Path.EndsWith(".mis", StringComparison.OrdinalIgnoreCase)
                || !seen.Add(VfsPath.Normalize(entry.Path)))
            {
                continue;
            }

            var bytes = files.TryReadAllBytes(entry.Path);
            if (bytes is null
                || !TacticsMissionFile.TryParse(bytes, Path.GetFileName(entry.Path), out var mission, out _)
                || !TacticsMissionWorld.TryParse(mission, out var world, out _))
            {
                continue;
            }

            records.GenericRecords.Add(Build(entry.Path, mission, world, used));
        }
    }

    /// <summary>The record for one mission.</summary>
    public static GenericEsmRecord Build(string path, TacticsMissionFile mission, TacticsMissionWorld world, ISet<uint> usedFormIds)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(mission);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(usedFormIds);

        var normalized = VfsPath.Normalize(path);
        var index = ClassicNameHash.Of(normalized, 24);
        while (!usedFormIds.Add(ClassicFormIdScheme.Compose(MissionDomain, index)))
        {
            index = (index + 1) & ClassicFormIdScheme.MaxIndex;
        }

        var placed = world.PlacedEntities.ToList();
        var tileTypes = world.Instances
            .GroupBy(i => world.TileHeaders[i.TileIndex].Type)
            .ToDictionary(g => g.Key, g => g.Count());

        var fields = new Dictionary<string, object?>
        {
            ["Path"] = normalized,
            ["Version"] = mission.Version,
            ["WorldVersion"] = world.HeaderVersion,
            ["Teams"] = string.Join(", ", world.Teams),
            ["TeamIds"] = string.Join(",", world.TeamIds),
            ["Squad"] = string.Join(", ", world.Squads),
            ["SpeechFile"] = world.SpeechFile,
            ["HasMinimap"] = world.HasMinimap,
            ["Regions"] = $"{world.RegionsX}x{world.RegionsZ}",
            ["RegionOrigin"] = $"{world.RegionOriginX},{world.RegionOriginZ}",
            ["Bounds"] = string.Join(",", world.Bounds),
            ["Extents"] = string.Join(",", world.Extents),
            ["TileTable"] = world.TilePaths.Count - 1,
            ["TileInstances"] = world.Instances.Count,
            ["FloorTiles"] = tileTypes.GetValueOrDefault(TacticsTileType.Floor),
            ["WallTiles"] = tileTypes.GetValueOrDefault(TacticsTileType.Wall),
            ["ObjectTiles"] = tileTypes.GetValueOrDefault(TacticsTileType.Object),
            ["StairTiles"] = tileTypes.GetValueOrDefault(TacticsTileType.Stair),
            ["RoofTiles"] = tileTypes.GetValueOrDefault(TacticsTileType.Roof),
            ["EntityClasses"] = world.EntityClasses.Count,
            ["Entities"] = world.Entities.Count,
            ["EntitySlots"] = world.EntitySlotCapacity,
            ["PlacedEntities"] = placed.Count,
            ["Actors"] = placed.Count(e => e.ClassName == "Actor"),
            ["Lights"] = placed.Count(e => e.ClassName == "Light"),
            ["WayPoints"] = placed.Count(e => e.ClassName == "WayPoint"),
            ["SpawnPoints"] = placed.Count(e => e.ClassName == "SpawnPoint"),
            ["Zones"] = string.Join(", ", world.Zones.Select(z => z.Name)),
            ["Players"] = world.Players.Count,
            ["Triggers"] = world.Triggers.Count,
            ["Variables"] = world.VariableNames.Count,
            ["Objectives"] = world.Objectives.Count,
            ["SpeechNodes"] = world.SpeechNodeCount,
            ["AmbientSounds"] = world.AmbientSounds.Count
        };

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(MissionDomain, index),
            RecordType = MissionRecordType,
            EditorId = ClassicRecordNaming.ToEditorId(Path.GetFileNameWithoutExtension(normalized)),
            FullName = ClassicRecordNaming.Summarize(normalized),
            Fields = fields
        };
    }
}
