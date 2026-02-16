using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.VersionTracking.Models;

namespace FalloutXbox360Utils.Core.VersionTracking.Extraction;

/// <summary>
///     Maps a RecordCollection (from RecordParser.ReconstructAll()) to a VersionSnapshot.
///     Shared by both ESM and DMP extraction pipelines.
/// </summary>
public static class SnapshotMapper
{
    /// <summary>
    ///     Maps a full RecordCollection to a lightweight VersionSnapshot.
    /// </summary>
    public static VersionSnapshot MapToSnapshot(RecordCollection records, BuildInfo buildInfo)
    {
        return new VersionSnapshot
        {
            Build = buildInfo,
            Quests = MapQuests(records.Quests),
            Npcs = MapNpcs(records.Npcs),
            Dialogues = MapDialogues(records.Dialogues),
            Weapons = MapWeapons(records.Weapons),
            Armor = MapArmor(records.Armor),
            Items = MapItems(records.Consumables, records.MiscItems, records.Keys),
            Scripts = MapScripts(records.Scripts),
            Locations = MapLocations(records.Cells, records.Worldspaces),
            Placements = MapPlacements(records.Cells, records.MapMarkers, records.FormIdToEditorId),
            Creatures = MapCreatures(records.Creatures),
            Perks = MapPerks(records.Perks),
            Ammo = MapAmmo(records.Ammo),
            LeveledLists = MapLeveledLists(records.LeveledLists),
            Notes = MapNotes(records.Notes),
            Terminals = MapTerminals(records.Terminals),
            ExtractedAt = DateTimeOffset.UtcNow
        };
    }

    private static Dictionary<uint, TrackedQuest> MapQuests(List<QuestRecord> quests)
    {
        var dict = new Dictionary<uint, TrackedQuest>();
        foreach (var q in quests)
        {
            if (q.FormId == 0)
            {
                continue;
            }

            dict.TryAdd(q.FormId, new TrackedQuest
            {
                FormId = q.FormId,
                EditorId = q.EditorId,
                FullName = q.FullName,
                Flags = q.Flags,
                Priority = q.Priority,
                QuestDelay = q.QuestDelay,
                ScriptFormId = q.Script,
                Stages = q.Stages.Select(s => new TrackedQuestStage(s.Index, s.LogEntry, s.Flags)).ToList(),
                Objectives = q.Objectives.Select(o => new TrackedQuestObjective(o.Index, o.DisplayText)).ToList()
            });
        }

        return dict;
    }

    private static Dictionary<uint, TrackedNpc> MapNpcs(List<NpcRecord> npcs)
    {
        var dict = new Dictionary<uint, TrackedNpc>();
        foreach (var n in npcs)
        {
            if (n.FormId == 0)
            {
                continue;
            }

            dict.TryAdd(n.FormId, new TrackedNpc
            {
                FormId = n.FormId,
                EditorId = n.EditorId,
                FullName = n.FullName,
                RaceFormId = n.Race,
                ClassFormId = n.Class,
                ScriptFormId = n.Script,
                SpecialStats = n.SpecialStats,
                Skills = n.Skills,
                FactionFormIds = n.Factions.Select(f => f.FactionFormId).ToList(),
                SpellFormIds = [.. n.Spells],
                Level = (ushort?)n.Stats?.Level
            });
        }

        return dict;
    }

    private static Dictionary<uint, TrackedDialogue> MapDialogues(List<DialogueRecord> dialogues)
    {
        var dict = new Dictionary<uint, TrackedDialogue>();
        foreach (var d in dialogues)
        {
            if (d.FormId == 0)
            {
                continue;
            }

            dict.TryAdd(d.FormId, new TrackedDialogue
            {
                FormId = d.FormId,
                EditorId = d.EditorId,
                TopicFormId = d.TopicFormId,
                QuestFormId = d.QuestFormId,
                SpeakerFormId = d.SpeakerFormId,
                ResponseTexts = d.Responses.Select(r => r.Text ?? "").Where(t => t.Length > 0).ToList(),
                InfoFlags = d.InfoFlags,
                PromptText = d.PromptText
            });
        }

        return dict;
    }

    private static Dictionary<uint, TrackedWeapon> MapWeapons(List<WeaponRecord> weapons)
    {
        var dict = new Dictionary<uint, TrackedWeapon>();
        foreach (var w in weapons)
        {
            if (w.FormId == 0)
            {
                continue;
            }

            dict.TryAdd(w.FormId, new TrackedWeapon
            {
                FormId = w.FormId,
                EditorId = w.EditorId,
                FullName = w.FullName,
                Value = w.Value,
                Damage = w.Damage,
                ClipSize = w.ClipSize,
                Weight = w.Weight,
                Speed = w.Speed,
                MinSpread = w.MinSpread,
                MaxRange = w.MaxRange,
                AmmoFormId = w.AmmoFormId,
                WeaponType = (byte)w.WeaponType
            });
        }

        return dict;
    }

    private static Dictionary<uint, TrackedArmor> MapArmor(List<ArmorRecord> armor)
    {
        var dict = new Dictionary<uint, TrackedArmor>();
        foreach (var a in armor)
        {
            if (a.FormId == 0)
            {
                continue;
            }

            dict.TryAdd(a.FormId, new TrackedArmor
            {
                FormId = a.FormId,
                EditorId = a.EditorId,
                FullName = a.FullName,
                Value = a.Value,
                Weight = a.Weight,
                DamageThreshold = a.DamageThreshold,
                DamageResistance = a.DamageResistance,
                BipedFlags = a.BipedFlags
            });
        }

        return dict;
    }

    private static Dictionary<uint, TrackedItem> MapItems(
        List<ConsumableRecord> consumables, List<MiscItemRecord> miscItems, List<KeyRecord> keys)
    {
        var dict = new Dictionary<uint, TrackedItem>();

        foreach (var c in consumables)
        {
            if (c.FormId == 0)
            {
                continue;
            }

            dict.TryAdd(c.FormId, new TrackedItem
            {
                FormId = c.FormId,
                EditorId = c.EditorId,
                FullName = c.FullName,
                RecordType = "ALCH",
                Value = (int)c.Value,
                Weight = c.Weight,
                Flags = c.Flags,
                EffectFormIds = [.. c.EffectFormIds]
            });
        }

        foreach (var m in miscItems)
        {
            if (m.FormId == 0)
            {
                continue;
            }

            dict.TryAdd(m.FormId, new TrackedItem
            {
                FormId = m.FormId,
                EditorId = m.EditorId,
                FullName = m.FullName,
                RecordType = "MISC",
                Value = m.Value,
                Weight = m.Weight
            });
        }

        foreach (var k in keys)
        {
            if (k.FormId == 0)
            {
                continue;
            }

            dict.TryAdd(k.FormId, new TrackedItem
            {
                FormId = k.FormId,
                EditorId = k.EditorId,
                FullName = k.FullName,
                RecordType = "KEYM",
                Value = k.Value,
                Weight = k.Weight
            });
        }

        return dict;
    }

    private static Dictionary<uint, TrackedScript> MapScripts(List<ScriptRecord> scripts)
    {
        var dict = new Dictionary<uint, TrackedScript>();
        foreach (var s in scripts)
        {
            if (s.FormId == 0)
            {
                continue;
            }

            dict.TryAdd(s.FormId, new TrackedScript
            {
                FormId = s.FormId,
                EditorId = s.EditorId,
                SourceText = s.SourceText,
                ScriptType = s.ScriptType,
                VariableCount = s.VariableCount,
                RefObjectCount = s.RefObjectCount,
                CompiledSize = s.CompiledSize
            });
        }

        return dict;
    }

    private static Dictionary<uint, TrackedLocation> MapLocations(
        List<CellRecord> cells, List<WorldspaceRecord> worldspaces)
    {
        var dict = new Dictionary<uint, TrackedLocation>();

        foreach (var c in cells)
        {
            if (c.FormId == 0)
            {
                continue;
            }

            dict.TryAdd(c.FormId, new TrackedLocation
            {
                FormId = c.FormId,
                EditorId = c.EditorId,
                FullName = c.FullName,
                RecordType = "CELL",
                GridX = c.GridX,
                GridY = c.GridY,
                WorldspaceFormId = c.WorldspaceFormId,
                IsInterior = c.IsInterior
            });
        }

        foreach (var w in worldspaces)
        {
            if (w.FormId == 0)
            {
                continue;
            }

            dict.TryAdd(w.FormId, new TrackedLocation
            {
                FormId = w.FormId,
                EditorId = w.EditorId,
                FullName = w.FullName,
                RecordType = "WRLD",
                IsInterior = false
            });
        }

        return dict;
    }

    /// <summary>
    ///     Maps only notable placements (those with EditorIDs or map marker names).
    /// </summary>
    private static Dictionary<uint, TrackedPlacement> MapPlacements(
        List<CellRecord> cells, List<PlacedReference> mapMarkers,
        Dictionary<uint, string> formIdToEditorId)
    {
        var dict = new Dictionary<uint, TrackedPlacement>();

        // Map markers are always notable
        foreach (var marker in mapMarkers)
        {
            if (marker.FormId == 0)
            {
                continue;
            }

            dict.TryAdd(marker.FormId, new TrackedPlacement
            {
                FormId = marker.FormId,
                EditorId = formIdToEditorId.GetValueOrDefault(marker.FormId),
                MarkerName = marker.MarkerName,
                BaseFormId = marker.BaseFormId,
                X = marker.X,
                Y = marker.Y,
                Z = marker.Z,
                IsMapMarker = true
            });
        }

        // Track placed objects that have editor IDs
        foreach (var cell in cells)
        {
            foreach (var obj in cell.PlacedObjects)
            {
                if (obj.FormId == 0 || dict.ContainsKey(obj.FormId))
                {
                    continue;
                }

                var editorId = formIdToEditorId.GetValueOrDefault(obj.FormId);
                if (string.IsNullOrEmpty(editorId) && !obj.IsMapMarker)
                {
                    continue; // Skip unnamed non-marker placements
                }

                dict.TryAdd(obj.FormId, new TrackedPlacement
                {
                    FormId = obj.FormId,
                    EditorId = editorId,
                    MarkerName = obj.MarkerName,
                    BaseFormId = obj.BaseFormId,
                    X = obj.X,
                    Y = obj.Y,
                    Z = obj.Z,
                    IsMapMarker = obj.IsMapMarker
                });
            }
        }

        return dict;
    }

    private static Dictionary<uint, TrackedCreature> MapCreatures(List<CreatureRecord> creatures)
    {
        var dict = new Dictionary<uint, TrackedCreature>();
        foreach (var c in creatures)
        {
            if (c.FormId == 0)
            {
                continue;
            }

            dict.TryAdd(c.FormId, new TrackedCreature
            {
                FormId = c.FormId,
                EditorId = c.EditorId,
                FullName = c.FullName,
                CreatureType = c.CreatureType,
                Level = (ushort?)c.Stats?.Level,
                AttackDamage = c.AttackDamage,
                CombatSkill = c.CombatSkill,
                MagicSkill = c.MagicSkill,
                StealthSkill = c.StealthSkill,
                ScriptFormId = c.Script,
                DeathItemFormId = c.DeathItem,
                FactionFormIds = c.Factions.Select(f => f.FactionFormId).ToList(),
                SpellFormIds = [.. c.Spells]
            });
        }

        return dict;
    }

    private static Dictionary<uint, TrackedPerk> MapPerks(List<PerkRecord> perks)
    {
        var dict = new Dictionary<uint, TrackedPerk>();
        foreach (var p in perks)
        {
            if (p.FormId == 0)
            {
                continue;
            }

            dict.TryAdd(p.FormId, new TrackedPerk
            {
                FormId = p.FormId,
                EditorId = p.EditorId,
                FullName = p.FullName,
                Description = p.Description,
                Trait = p.Trait,
                MinLevel = p.MinLevel,
                Ranks = p.Ranks,
                Playable = p.Playable,
                EntryCount = p.Entries.Count
            });
        }

        return dict;
    }

    private static Dictionary<uint, TrackedAmmo> MapAmmo(List<AmmoRecord> ammo)
    {
        var dict = new Dictionary<uint, TrackedAmmo>();
        foreach (var a in ammo)
        {
            if (a.FormId == 0)
            {
                continue;
            }

            dict.TryAdd(a.FormId, new TrackedAmmo
            {
                FormId = a.FormId,
                EditorId = a.EditorId,
                FullName = a.FullName,
                Speed = a.Speed,
                Flags = a.Flags,
                Value = a.Value,
                Weight = a.Weight,
                ProjectileFormId = a.ProjectileFormId
            });
        }

        return dict;
    }

    private static Dictionary<uint, TrackedLeveledList> MapLeveledLists(List<LeveledListRecord> leveledLists)
    {
        var dict = new Dictionary<uint, TrackedLeveledList>();
        foreach (var l in leveledLists)
        {
            if (l.FormId == 0)
            {
                continue;
            }

            dict.TryAdd(l.FormId, new TrackedLeveledList
            {
                FormId = l.FormId,
                EditorId = l.EditorId,
                ListType = l.ListType,
                ChanceNone = l.ChanceNone,
                Flags = l.Flags,
                GlobalFormId = l.GlobalFormId,
                Entries = l.Entries.Select(e => new TrackedLeveledEntry(e.Level, e.FormId, e.Count)).ToList()
            });
        }

        return dict;
    }

    private static Dictionary<uint, TrackedNote> MapNotes(List<NoteRecord> notes)
    {
        var dict = new Dictionary<uint, TrackedNote>();
        foreach (var n in notes)
        {
            if (n.FormId == 0)
            {
                continue;
            }

            dict.TryAdd(n.FormId, new TrackedNote
            {
                FormId = n.FormId,
                EditorId = n.EditorId,
                FullName = n.FullName,
                NoteType = n.NoteType,
                Text = n.Text
            });
        }

        return dict;
    }

    private static Dictionary<uint, TrackedTerminal> MapTerminals(List<TerminalRecord> terminals)
    {
        var dict = new Dictionary<uint, TrackedTerminal>();
        foreach (var t in terminals)
        {
            if (t.FormId == 0)
            {
                continue;
            }

            dict.TryAdd(t.FormId, new TrackedTerminal
            {
                FormId = t.FormId,
                EditorId = t.EditorId,
                FullName = t.FullName,
                HeaderText = t.HeaderText,
                Difficulty = t.Difficulty,
                Flags = t.Flags,
                MenuItemTexts = t.MenuItems.Select(m => m.Text ?? "").Where(s => s.Length > 0).ToList(),
                MenuItemCount = t.MenuItems.Count,
                Password = t.Password
            });
        }

        return dict;
    }
}
