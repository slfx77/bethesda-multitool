using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Actors;

/// <summary>
/// Resolves FO3/FNV template declarations without selecting spawn-time leveled actors.
/// Flags take precedence over leftover local data: TESNPC::CopyFromTemplateForm at
/// MemDebug VA 0x824461A0 copies the selected group, including TESContainer, wholesale.
/// Creature field groups follow the local xEdit wbDefinitionsFNV CREA schema.
/// </summary>
internal sealed class ActorTemplateResolver
{
    private const int MaximumDepth = 16;
    private readonly Dictionary<uint, NpcRecord> _npcs;
    private readonly Dictionary<uint, CreatureRecord> _creatures;
    private readonly Dictionary<uint, LeveledListRecord> _lists;
    private readonly HashSet<uint> _knownIds;
    private readonly ActorSourceIdentity _identity;
    private readonly BethesdaGame _game;

    internal ActorTemplateResolver(RecordCollection records, ActorSourceIdentity? identity = null)
    {
        _identity = identity ?? new ActorSourceIdentity();
        _game = records.Game;
        _npcs = records.Npcs.GroupBy(r => r.FormId).ToDictionary(g => g.Key, g => g.Last());
        _creatures = records.Creatures.GroupBy(r => r.FormId).ToDictionary(g => g.Key, g => g.Last());
        _lists = records.LeveledLists.GroupBy(r => r.FormId).ToDictionary(g => g.Key, g => g.Last());
        _knownIds = records.FormIdToEditorId.Keys.Concat(records.FormIdToDisplayName.Keys)
            .Concat(records.DecodedTreesByFormId.Keys).Concat(records.GenericRecords.Select(r => r.FormId))
            .Concat(records.Weapons.Select(r => r.FormId)).Concat(records.Armor.Select(r => r.FormId))
            .Concat(records.Scripts.Select(r => r.FormId)).Concat(records.Quests.Select(r => r.FormId))
            .Concat(_npcs.Keys).Concat(_creatures.Keys).Concat(_lists.Keys).ToHashSet();
    }

    /// <summary>Walks only while the selected group's inheritance bit is set.</summary>
    internal ActorTemplateGroupResolution Resolve(uint actorId, bool creature, ActorTemplateGroup group,
        ushort? playerLevel = null)
    {
        var chain = new List<ActorTemplateHop>();
        if (_game is not (BethesdaGame.Unknown or BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas))
            return new(group, "UnsupportedGame", null, null, null, chain, [],
                $"FO3/FNV template group semantics have not been validated for {_game}.");
        var seen = new HashSet<uint>();
        var current = actorId;
        while (true)
        {
            if (!seen.Add(current)) return Failed("TemplateCycle", current);
            if (chain.Count >= MaximumDepth) return Failed("Truncated", current);
            ActorTemplateHop hop;
            if (creature && _creatures.TryGetValue(current, out var crea))
                hop = new(current, "CREA", crea.EditorId, crea.Stats?.TemplateFlags, crea.Template, _identity.SourcePlugin(current));
            else if (!creature && _npcs.TryGetValue(current, out var npc))
                hop = new(current, "NPC_", npc.EditorId, npc.Stats?.TemplateFlags, npc.Template, _identity.SourcePlugin(current));
            else if (_lists.TryGetValue(current, out var list))
            {
                if (list.ListType != (creature ? "LVLC" : "LVLN")) return Failed("TemplateWrongType", current);
                IReadOnlyList<LeveledEntry> candidates = playerLevel is { } level
                    ? LeveledItemEligibility.Select(list.Entries, list.Flags, level)
                    : list.Entries.ToArray();
                return new(group, "LeveledTemplate", null, _identity.Owner(current), current, chain,
                    candidates, "Spawn-time template selection is not performed; entries are candidates only.");
            }
            else
            {
                var missing = _identity.MissingReason(current);
                return Failed(missing is "TemplateDeleted" or "TemplateTypeConflict" or "TemplateNotParsed" or "TemplateAmbiguous"
                    ? missing : _knownIds.Contains(current) ? "TemplateWrongType" : missing, current);
            }
            chain.Add(hop);
            if (hop.TemplateFlags is null && hop.Template is not null and not 0)
                return Failed("TemplateFlagsMissing", current);
            if (((hop.TemplateFlags ?? 0) & (ushort)group) == 0)
                return new(group, current == actorId ? "Authored" : "Inherited", current,
                    hop.SourcePlugin, null, chain, []);
            if (hop.Template is not { } next || next == 0) return Failed("TemplateMissing", next: null);
            current = next;
        }

        ActorTemplateGroupResolution Failed(string status, uint? next) => new(group, status, null,
            next is { } id ? _identity.Owner(id) : null, next, chain, [], status == "NotInCapture"
                ? "Absent from this partial capture; this does not establish absence from the build." : null);
    }
}
