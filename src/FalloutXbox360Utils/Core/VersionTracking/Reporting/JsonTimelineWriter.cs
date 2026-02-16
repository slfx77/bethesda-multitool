using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FalloutXbox360Utils.Core.VersionTracking.Models;

namespace FalloutXbox360Utils.Core.VersionTracking.Reporting;

/// <summary>
///     Generates JSON reports from version tracking results.
/// </summary>
public static class JsonTimelineWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    /// <summary>
    ///     Generates a full timeline report as JSON.
    /// </summary>
    public static string WriteTimeline(
        List<VersionSnapshot> snapshots,
        List<VersionDiffResult> diffs)
    {
        var report = new
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Builds = snapshots.Select(s => new
            {
                s.Build.Label,
                Date = s.Build.BuildDate?.ToString("yyyy-MM-dd HH:mm:ss"),
                s.Build.SourceType,
                s.Build.BuildType,
                s.Build.IsAuthoritative,
                RecordCounts = new
                {
                    Quests = s.Quests.Count,
                    Npcs = s.Npcs.Count,
                    Dialogues = s.Dialogues.Count,
                    Weapons = s.Weapons.Count,
                    Armor = s.Armor.Count,
                    Items = s.Items.Count,
                    Scripts = s.Scripts.Count,
                    Locations = s.Locations.Count,
                    Placements = s.Placements.Count,
                    Creatures = s.Creatures.Count,
                    Perks = s.Perks.Count,
                    Ammo = s.Ammo.Count,
                    LeveledLists = s.LeveledLists.Count,
                    Notes = s.Notes.Count,
                    Terminals = s.Terminals.Count,
                    Total = s.TotalRecordCount
                }
            }).ToList(),
            Diffs = diffs.Select(d => new
            {
                From = d.FromBuild.Label,
                To = d.ToBuild.Label,
                Summary = new
                {
                    d.TotalAdded,
                    d.TotalRemoved,
                    d.TotalChanged
                },
                Quests = FormatChanges(d.QuestChanges),
                Npcs = FormatChanges(d.NpcChanges),
                Dialogues = FormatChanges(d.DialogueChanges),
                Weapons = FormatChanges(d.WeaponChanges),
                Armor = FormatChanges(d.ArmorChanges),
                Items = FormatChanges(d.ItemChanges),
                Scripts = FormatChanges(d.ScriptChanges),
                Locations = FormatChanges(d.LocationChanges),
                Placements = FormatChanges(d.PlacementChanges),
                Creatures = FormatChanges(d.CreatureChanges),
                Perks = FormatChanges(d.PerkChanges),
                Ammo = FormatChanges(d.AmmoChanges),
                LeveledLists = FormatChanges(d.LeveledListChanges),
                Notes = FormatChanges(d.NoteChanges),
                Terminals = FormatChanges(d.TerminalChanges)
            }).ToList()
        };

        return JsonSerializer.Serialize(report, JsonOptions);
    }

    private static object FormatChanges(List<RecordChange> changes)
    {
        if (changes.Count == 0)
        {
            return Array.Empty<object>();
        }

        return changes.Select(c => new
        {
            FormId = $"0x{c.FormId:X8}",
            c.EditorId,
            c.FullName,
            c.RecordType,
            ChangeType = c.ChangeType.ToString(),
            Fields = c.FieldChanges.Select(f => new
            {
                f.FieldName,
                f.OldValue,
                f.NewValue
            }).ToList()
        }).ToList();
    }
}
