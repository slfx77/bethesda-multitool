using FalloutXbox360Utils.Core.VersionTracking.Models;

namespace FalloutXbox360Utils.Core.VersionTracking.Processing;

/// <summary>
///     Compares individual tracked records field-by-field and returns changes.
/// </summary>
public static class RecordFieldComparer
{
    public static List<FieldChange> CompareQuests(TrackedQuest a, TrackedQuest b)
    {
        var changes = new List<FieldChange>();
        CompareField(changes, "FullName", a.FullName, b.FullName);
        CompareField(changes, "Flags", $"0x{a.Flags:X2}", $"0x{b.Flags:X2}");
        CompareField(changes, "Priority", a.Priority, b.Priority);
        CompareFloat(changes, "QuestDelay", a.QuestDelay, b.QuestDelay);
        CompareFormId(changes, "Script", a.ScriptFormId, b.ScriptFormId);
        CompareStages(changes, a.Stages, b.Stages);
        CompareObjectives(changes, a.Objectives, b.Objectives);
        return changes;
    }

    public static List<FieldChange> CompareNpcs(TrackedNpc a, TrackedNpc b)
    {
        var changes = new List<FieldChange>();
        CompareField(changes, "FullName", a.FullName, b.FullName);
        CompareFormId(changes, "Race", a.RaceFormId, b.RaceFormId);
        CompareFormId(changes, "Class", a.ClassFormId, b.ClassFormId);
        CompareFormId(changes, "Script", a.ScriptFormId, b.ScriptFormId);
        CompareBytes(changes, "SPECIAL", a.SpecialStats, b.SpecialStats);
        CompareBytes(changes, "Skills", a.Skills, b.Skills);
        CompareFormIdList(changes, "Factions", a.FactionFormIds, b.FactionFormIds);
        CompareField(changes, "Level", a.Level, b.Level);
        return changes;
    }

    public static List<FieldChange> CompareDialogues(TrackedDialogue a, TrackedDialogue b)
    {
        var changes = new List<FieldChange>();
        CompareFormId(changes, "Topic", a.TopicFormId, b.TopicFormId);
        CompareFormId(changes, "Quest", a.QuestFormId, b.QuestFormId);
        CompareFormId(changes, "Speaker", a.SpeakerFormId, b.SpeakerFormId);
        CompareField(changes, "InfoFlags", $"0x{a.InfoFlags:X2}", $"0x{b.InfoFlags:X2}");
        CompareField(changes, "PromptText", a.PromptText, b.PromptText);
        CompareStringList(changes, "ResponseTexts", a.ResponseTexts, b.ResponseTexts);
        return changes;
    }

    public static List<FieldChange> CompareWeapons(TrackedWeapon a, TrackedWeapon b)
    {
        var changes = new List<FieldChange>();
        CompareField(changes, "FullName", a.FullName, b.FullName);
        CompareField(changes, "Value", a.Value, b.Value);
        CompareField(changes, "Damage", a.Damage, b.Damage);
        CompareField(changes, "ClipSize", a.ClipSize, b.ClipSize);
        CompareFloat(changes, "Weight", a.Weight, b.Weight);
        CompareFloat(changes, "Speed", a.Speed, b.Speed);
        CompareFloat(changes, "MinSpread", a.MinSpread, b.MinSpread);
        CompareFloat(changes, "MaxRange", a.MaxRange, b.MaxRange);
        CompareFormId(changes, "Ammo", a.AmmoFormId, b.AmmoFormId);
        CompareField(changes, "WeaponType", a.WeaponType, b.WeaponType);
        return changes;
    }

    public static List<FieldChange> CompareArmor(TrackedArmor a, TrackedArmor b)
    {
        var changes = new List<FieldChange>();
        CompareField(changes, "FullName", a.FullName, b.FullName);
        CompareField(changes, "Value", a.Value, b.Value);
        CompareFloat(changes, "Weight", a.Weight, b.Weight);
        CompareFloat(changes, "DT", a.DamageThreshold, b.DamageThreshold);
        CompareField(changes, "DR", a.DamageResistance, b.DamageResistance);
        CompareField(changes, "BipedFlags", $"0x{a.BipedFlags:X8}", $"0x{b.BipedFlags:X8}");
        return changes;
    }

    public static List<FieldChange> CompareItems(TrackedItem a, TrackedItem b)
    {
        var changes = new List<FieldChange>();
        CompareField(changes, "FullName", a.FullName, b.FullName);
        CompareField(changes, "Value", a.Value, b.Value);
        CompareFloat(changes, "Weight", a.Weight, b.Weight);
        if (a.RecordType == "ALCH" && b.RecordType == "ALCH")
        {
            CompareField(changes, "Flags", $"0x{a.Flags:X8}", $"0x{b.Flags:X8}");
            CompareFormIdList(changes, "Effects", a.EffectFormIds, b.EffectFormIds);
        }

        return changes;
    }

    public static List<FieldChange> CompareScripts(TrackedScript a, TrackedScript b)
    {
        var changes = new List<FieldChange>();
        CompareField(changes, "ScriptType", a.ScriptType, b.ScriptType);
        CompareField(changes, "VariableCount", a.VariableCount, b.VariableCount);
        CompareField(changes, "RefObjectCount", a.RefObjectCount, b.RefObjectCount);
        CompareField(changes, "CompiledSize", a.CompiledSize, b.CompiledSize);

        // For source text, just note if it changed (don't include full text in diff)
        var sourceA = NormalizeScript(a.SourceText);
        var sourceB = NormalizeScript(b.SourceText);
        if (sourceA != sourceB)
        {
            var descA = string.IsNullOrEmpty(a.SourceText) ? "(none)" : $"({a.SourceText.Length} chars)";
            var descB = string.IsNullOrEmpty(b.SourceText) ? "(none)" : $"({b.SourceText.Length} chars)";
            changes.Add(new FieldChange("SourceText", descA, descB));
        }

        return changes;
    }

    public static List<FieldChange> CompareLocations(TrackedLocation a, TrackedLocation b)
    {
        var changes = new List<FieldChange>();
        CompareField(changes, "FullName", a.FullName, b.FullName);
        CompareField(changes, "GridX", a.GridX, b.GridX);
        CompareField(changes, "GridY", a.GridY, b.GridY);
        CompareFormId(changes, "Worldspace", a.WorldspaceFormId, b.WorldspaceFormId);
        CompareField(changes, "IsInterior", a.IsInterior, b.IsInterior);
        return changes;
    }

    public static List<FieldChange> ComparePlacements(TrackedPlacement a, TrackedPlacement b)
    {
        var changes = new List<FieldChange>();
        CompareField(changes, "MarkerName", a.MarkerName, b.MarkerName);
        CompareFormId(changes, "BaseObject", a.BaseFormId, b.BaseFormId);
        CompareFloat(changes, "X", a.X, b.X, tolerance: 1.0f);
        CompareFloat(changes, "Y", a.Y, b.Y, tolerance: 1.0f);
        CompareFloat(changes, "Z", a.Z, b.Z, tolerance: 1.0f);
        return changes;
    }

    public static List<FieldChange> CompareCreatures(TrackedCreature a, TrackedCreature b)
    {
        var changes = new List<FieldChange>();
        CompareField(changes, "FullName", a.FullName, b.FullName);
        CompareField(changes, "CreatureType", a.CreatureType, b.CreatureType);
        CompareField(changes, "Level", a.Level, b.Level);
        CompareField(changes, "AttackDamage", a.AttackDamage, b.AttackDamage);
        CompareField(changes, "CombatSkill", a.CombatSkill, b.CombatSkill);
        CompareField(changes, "MagicSkill", a.MagicSkill, b.MagicSkill);
        CompareField(changes, "StealthSkill", a.StealthSkill, b.StealthSkill);
        CompareFormId(changes, "Script", a.ScriptFormId, b.ScriptFormId);
        CompareFormId(changes, "DeathItem", a.DeathItemFormId, b.DeathItemFormId);
        CompareFormIdList(changes, "Factions", a.FactionFormIds, b.FactionFormIds);
        CompareFormIdList(changes, "Spells", a.SpellFormIds, b.SpellFormIds);
        return changes;
    }

    public static List<FieldChange> ComparePerks(TrackedPerk a, TrackedPerk b)
    {
        var changes = new List<FieldChange>();
        CompareField(changes, "FullName", a.FullName, b.FullName);
        CompareField(changes, "Description", a.Description, b.Description);
        CompareField(changes, "Trait", a.Trait, b.Trait);
        CompareField(changes, "MinLevel", a.MinLevel, b.MinLevel);
        CompareField(changes, "Ranks", a.Ranks, b.Ranks);
        CompareField(changes, "Playable", a.Playable, b.Playable);
        CompareField(changes, "EntryCount", a.EntryCount, b.EntryCount);
        return changes;
    }

    public static List<FieldChange> CompareAmmo(TrackedAmmo a, TrackedAmmo b)
    {
        var changes = new List<FieldChange>();
        CompareField(changes, "FullName", a.FullName, b.FullName);
        CompareFloat(changes, "Speed", a.Speed, b.Speed);
        CompareField(changes, "Flags", $"0x{a.Flags:X2}", $"0x{b.Flags:X2}");
        CompareField(changes, "Value", a.Value, b.Value);
        CompareFloat(changes, "Weight", a.Weight, b.Weight);
        CompareFormId(changes, "Projectile", a.ProjectileFormId, b.ProjectileFormId);
        return changes;
    }

    public static List<FieldChange> CompareLeveledLists(TrackedLeveledList a, TrackedLeveledList b)
    {
        var changes = new List<FieldChange>();
        CompareField(changes, "ChanceNone", a.ChanceNone, b.ChanceNone);
        CompareField(changes, "Flags", $"0x{a.Flags:X2}", $"0x{b.Flags:X2}");
        CompareFormId(changes, "Global", a.GlobalFormId, b.GlobalFormId);
        CompareLeveledEntries(changes, a.Entries, b.Entries);
        return changes;
    }

    public static List<FieldChange> CompareNotes(TrackedNote a, TrackedNote b)
    {
        var changes = new List<FieldChange>();
        CompareField(changes, "FullName", a.FullName, b.FullName);
        CompareField(changes, "NoteType", a.NoteType, b.NoteType);

        // For text, just note if it changed (don't include full text in diff)
        if (a.Text != b.Text)
        {
            var descA = string.IsNullOrEmpty(a.Text) ? "(none)" : $"({a.Text.Length} chars)";
            var descB = string.IsNullOrEmpty(b.Text) ? "(none)" : $"({b.Text.Length} chars)";
            changes.Add(new FieldChange("Text", descA, descB));
        }

        return changes;
    }

    public static List<FieldChange> CompareTerminals(TrackedTerminal a, TrackedTerminal b)
    {
        var changes = new List<FieldChange>();
        CompareField(changes, "FullName", a.FullName, b.FullName);
        CompareField(changes, "Difficulty", a.Difficulty, b.Difficulty);
        CompareField(changes, "Flags", $"0x{a.Flags:X2}", $"0x{b.Flags:X2}");
        CompareField(changes, "Password", a.Password, b.Password);
        CompareField(changes, "MenuItemCount", a.MenuItemCount, b.MenuItemCount);

        // For header text, just note if it changed
        if (a.HeaderText != b.HeaderText)
        {
            var descA = string.IsNullOrEmpty(a.HeaderText) ? "(none)" : $"({a.HeaderText.Length} chars)";
            var descB = string.IsNullOrEmpty(b.HeaderText) ? "(none)" : $"({b.HeaderText.Length} chars)";
            changes.Add(new FieldChange("HeaderText", descA, descB));
        }

        CompareStringList(changes, "MenuItemTexts", a.MenuItemTexts, b.MenuItemTexts);
        return changes;
    }

    #region Helpers

    private static void CompareField<T>(List<FieldChange> changes, string name, T? a, T? b)
    {
        var sa = a?.ToString();
        var sb = b?.ToString();
        if (sa != sb)
        {
            changes.Add(new FieldChange(name, sa, sb));
        }
    }

    private static void CompareFloat(List<FieldChange> changes, string name, float a, float b, float tolerance = 0.001f)
    {
        if (MathF.Abs(a - b) > tolerance)
        {
            changes.Add(new FieldChange(name, a.ToString("F2"), b.ToString("F2")));
        }
    }

    private static void CompareFormId(List<FieldChange> changes, string name, uint? a, uint? b)
    {
        if (a != b)
        {
            changes.Add(new FieldChange(name,
                a.HasValue ? $"0x{a.Value:X8}" : "(none)",
                b.HasValue ? $"0x{b.Value:X8}" : "(none)"));
        }
    }

    private static void CompareFormId(List<FieldChange> changes, string name, uint a, uint b)
    {
        if (a != b)
        {
            changes.Add(new FieldChange(name, $"0x{a:X8}", $"0x{b:X8}"));
        }
    }

    private static void CompareBytes(List<FieldChange> changes, string name, byte[]? a, byte[]? b)
    {
        if (a == null && b == null)
        {
            return;
        }

        if (a == null || b == null || !a.AsSpan().SequenceEqual(b))
        {
            changes.Add(new FieldChange(name,
                a != null ? string.Join(",", a) : "(none)",
                b != null ? string.Join(",", b) : "(none)"));
        }
    }

    private static void CompareFormIdList(List<FieldChange> changes, string name, List<uint> a, List<uint> b)
    {
        var setA = new HashSet<uint>(a);
        var setB = new HashSet<uint>(b);
        if (!setA.SetEquals(setB))
        {
            changes.Add(new FieldChange(name,
                string.Join(", ", a.Select(x => $"0x{x:X8}")),
                string.Join(", ", b.Select(x => $"0x{x:X8}"))));
        }
    }

    private static void CompareStringList(List<FieldChange> changes, string name, List<string> a, List<string> b)
    {
        if (!a.SequenceEqual(b))
        {
            changes.Add(new FieldChange(name,
                string.Join(" | ", a),
                string.Join(" | ", b)));
        }
    }

    private static void CompareLeveledEntries(List<FieldChange> changes, List<TrackedLeveledEntry> a, List<TrackedLeveledEntry> b)
    {
        static string Serialize(TrackedLeveledEntry e) => $"L{e.Level}:0x{e.FormId:X8}x{e.Count}";

        var setA = new HashSet<string>(a.Select(Serialize));
        var setB = new HashSet<string>(b.Select(Serialize));

        foreach (var entry in setA.Except(setB))
        {
            changes.Add(new FieldChange("Entry", entry, "(removed)"));
        }

        foreach (var entry in setB.Except(setA))
        {
            changes.Add(new FieldChange("Entry", "(absent)", entry));
        }
    }

    private static void CompareStages(List<FieldChange> changes, List<TrackedQuestStage> a, List<TrackedQuestStage> b)
    {
        var dictA = a.ToDictionary(s => s.Index);
        var dictB = b.ToDictionary(s => s.Index);
        var allIndices = new HashSet<int>(dictA.Keys.Concat(dictB.Keys));

        foreach (var idx in allIndices.OrderBy(i => i))
        {
            var inA = dictA.TryGetValue(idx, out var stageA);
            var inB = dictB.TryGetValue(idx, out var stageB);

            if (inA && !inB)
            {
                changes.Add(new FieldChange($"Stage {idx}", stageA!.LogEntry ?? "(empty)", "(removed)"));
            }
            else if (!inA && inB)
            {
                changes.Add(new FieldChange($"Stage {idx}", "(absent)", stageB!.LogEntry ?? "(empty)"));
            }
            else if (inA && inB && stageA!.LogEntry != stageB!.LogEntry)
            {
                changes.Add(new FieldChange($"Stage {idx}", stageA.LogEntry, stageB.LogEntry));
            }
        }
    }

    private static void CompareObjectives(List<FieldChange> changes, List<TrackedQuestObjective> a, List<TrackedQuestObjective> b)
    {
        var dictA = a.ToDictionary(o => o.Index);
        var dictB = b.ToDictionary(o => o.Index);
        var allIndices = new HashSet<int>(dictA.Keys.Concat(dictB.Keys));

        foreach (var idx in allIndices.OrderBy(i => i))
        {
            var inA = dictA.TryGetValue(idx, out var objA);
            var inB = dictB.TryGetValue(idx, out var objB);

            if (inA && !inB)
            {
                changes.Add(new FieldChange($"Objective {idx}", objA!.DisplayText ?? "(empty)", "(removed)"));
            }
            else if (!inA && inB)
            {
                changes.Add(new FieldChange($"Objective {idx}", "(absent)", objB!.DisplayText ?? "(empty)"));
            }
            else if (inA && inB && objA!.DisplayText != objB!.DisplayText)
            {
                changes.Add(new FieldChange($"Objective {idx}", objA.DisplayText, objB.DisplayText));
            }
        }
    }

    private static string? NormalizeScript(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return null;
        }

        // Normalize whitespace for comparison
        return string.Join('\n', source.Split('\n').Select(l => l.TrimEnd()));
    }

    #endregion
}
