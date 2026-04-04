using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Flattens typed record objects into field dictionaries for cross-dump comparison.
///     Each method mirrors the corresponding CSV writer's column layout but returns
///     <c>Dictionary&lt;string, string&gt;</c> instead of writing CSV lines.
/// </summary>
internal static class RecordFieldFlattener
{
    /// <summary>
    ///     Flatten all records in a <see cref="RecordCollection" /> by type.
    ///     Returns (recordTypeName, formId, editorId, displayName, fields) tuples.
    /// </summary>
    internal static IEnumerable<(string TypeName, uint FormId, string? EditorId, string? DisplayName,
        Dictionary<string, string> Fields)> FlattenAll(RecordCollection records, FormIdResolver resolver)
    {
        foreach (var w in records.Weapons)
            yield return ("Weapon", w.FormId, w.EditorId, w.FullName, FlattenWeapon(w, resolver));
        foreach (var n in records.Npcs)
            yield return ("NPC", n.FormId, n.EditorId, n.FullName, FlattenNpc(n, resolver));
        foreach (var a in records.Armor)
            yield return ("Armor", a.FormId, a.EditorId, a.FullName, FlattenArmor(a));
        foreach (var a in records.Ammo)
            yield return ("Ammo", a.FormId, a.EditorId, a.FullName, FlattenAmmo(a, resolver));
        foreach (var c in records.Consumables)
            yield return ("Consumable", c.FormId, c.EditorId, c.FullName, FlattenConsumable(c, resolver));
        foreach (var q in records.Quests)
            yield return ("Quest", q.FormId, q.EditorId, q.FullName, FlattenQuest(q));
        foreach (var f in records.Factions)
            yield return ("Faction", f.FormId, f.EditorId, f.FullName, FlattenFaction(f));
        foreach (var c in records.Cells)
            yield return ("Cell", c.FormId, c.EditorId, c.FullName, FlattenCell(c));
        foreach (var w in records.Worldspaces)
            yield return ("Worldspace", w.FormId, w.EditorId, w.FullName, FlattenWorldspace(w));
        foreach (var cr in records.Creatures)
            yield return ("Creature", cr.FormId, cr.EditorId, cr.FullName, FlattenCreature(cr));
        foreach (var r in records.Races)
            yield return ("Race", r.FormId, r.EditorId, r.FullName, FlattenRace(r));
        foreach (var c in records.Containers)
            yield return ("Container", c.FormId, c.EditorId, c.FullName, FlattenContainer(c, resolver));
        foreach (var p in records.Perks)
            yield return ("Perk", p.FormId, p.EditorId, p.FullName, FlattenPerk(p));
        foreach (var m in records.MiscItems)
            yield return ("MiscItem", m.FormId, m.EditorId, m.FullName, FlattenMiscItem(m));
        foreach (var k in records.Keys)
            yield return ("Key", k.FormId, k.EditorId, k.FullName, FlattenKey(k));
        foreach (var b in records.Books)
            yield return ("Book", b.FormId, b.EditorId, b.FullName, FlattenBook(b, resolver));
        foreach (var n in records.Notes)
            yield return ("Note", n.FormId, n.EditorId, n.FullName, FlattenNote(n));
        foreach (var t in records.Terminals)
            yield return ("Terminal", t.FormId, t.EditorId, t.FullName, FlattenTerminal(t));
        foreach (var ll in records.LeveledLists)
            yield return ("LeveledList", ll.FormId, ll.EditorId, null, FlattenLeveledList(ll));
        foreach (var s in records.Spells)
            yield return ("Spell", s.FormId, s.EditorId, s.FullName, FlattenSpell(s));
    }

    internal static Dictionary<string, string> FlattenWeapon(WeaponRecord w, FormIdResolver resolver)
    {
        var d = new Dictionary<string, string>
        {
            ["WeaponType"] = w.WeaponTypeName,
            ["Skill"] = resolver.GetActorValueName((int)w.Skill) ?? $"AV#{w.Skill}",
            ["Damage"] = w.Damage.ToString(),
            ["DPS"] = w.DamagePerSecond.ToString("F1"),
            ["FireRate"] = w.ShotsPerSec.ToString("F2"),
            ["ClipSize"] = w.ClipSize.ToString(),
            ["NumProjectiles"] = w.NumProjectiles.ToString(),
            ["AmmoPerShot"] = w.AmmoPerShot.ToString(),
            ["MinRange"] = w.MinRange.ToString("F0"),
            ["MaxRange"] = w.MaxRange.ToString("F0"),
            ["Spread"] = w.Spread.ToString("F2"),
            ["MinSpread"] = w.MinSpread.ToString("F2"),
            ["Drift"] = w.Drift.ToString("F2"),
            ["StrReq"] = w.StrengthRequirement.ToString(),
            ["SkillReq"] = w.SkillRequirement.ToString(),
            ["CritDamage"] = w.CriticalDamage.ToString(),
            ["CritChance"] = w.CriticalChance.ToString("F2"),
            ["CritEffect"] = Fmt.FIdN(w.CriticalEffectFormId),
            ["Value"] = w.Value.ToString(),
            ["Weight"] = w.Weight.ToString("F2"),
            ["Health"] = w.Health.ToString(),
            ["Ammo"] = ResolveRef(w.AmmoFormId, resolver),
            ["Projectile"] = ResolveRef(w.ProjectileFormId, resolver),
            ["ImpactDataSet"] = ResolveRef(w.ImpactDataSetFormId, resolver),
            ["APCost"] = w.ActionPoints.ToString("F1"),
            ["Speed"] = w.Speed.ToString("F2"),
            ["Reach"] = w.Reach.ToString("F2"),
            ["ModelPath"] = w.ModelPath ?? ""
        };

        if (w.ProjectileData != null)
        {
            d["ProjSpeed"] = w.ProjectileData.Speed.ToString("F1");
            d["ProjGravity"] = w.ProjectileData.Gravity.ToString("F4");
            d["ProjRange"] = w.ProjectileData.Range.ToString("F0");
            d["ProjForce"] = w.ProjectileData.Force.ToString("F1");
        }

        return d;
    }

    internal static Dictionary<string, string> FlattenNpc(NpcRecord n, FormIdResolver resolver)
    {
        string gender;
        if (n.Stats == null)
            gender = "";
        else
            gender = (n.Stats.Flags & 1) == 1 ? "Female" : "Male";
        var d = new Dictionary<string, string>
        {
            ["Gender"] = gender,
            ["Level"] = n.Stats?.Level.ToString() ?? "",
            ["Race"] = ResolveRef(n.Race, resolver),
            ["Class"] = ResolveRef(n.Class, resolver),
            ["Script"] = ResolveRef(n.Script, resolver),
            ["VoiceType"] = ResolveRef(n.VoiceType, resolver),
            ["Template"] = ResolveRef(n.Template, resolver),
            ["CombatStyle"] = ResolveRef(n.CombatStyleFormId, resolver),
            ["Flags"] = n.Stats?.Flags.ToString() ?? "",
            ["CalcMin"] = n.Stats?.CalcMin.ToString() ?? "",
            ["CalcMax"] = n.Stats?.CalcMax.ToString() ?? "",
            ["Height"] = n.Height?.ToString("F2") ?? "",
            ["Weight"] = n.Weight?.ToString("F2") ?? ""
        };

        // SPECIAL stats
        if (n.SpecialStats is { Length: >= 7 })
        {
            string[] specNames = ["ST", "PE", "EN", "CH", "IN", "AG", "LK"];
            for (var i = 0; i < 7; i++)
                d[$"SPECIAL_{specNames[i]}"] = n.SpecialStats[i].ToString();
        }

        // Skills
        if (n.Skills is { Length: >= 13 })
        {
            string[] skillNames =
            [
                "Barter", "EnergyWeapons", "Explosives", "Guns", "Lockpick",
                "Medicine", "MeleeWeapons", "Repair", "Science", "Sneak",
                "Speech", "Survival", "Unarmed"
            ];
            for (var i = 0; i < 13; i++)
                d[skillNames[i]] = n.Skills[i].ToString();
        }

        // AI data
        if (n.AiData != null)
        {
            d["Aggression"] = n.AiData.Aggression.ToString();
            d["Confidence"] = n.AiData.Confidence.ToString();
            d["Mood"] = n.AiData.Mood.ToString();
            d["EnergyLevel"] = n.AiData.EnergyLevel.ToString();
            d["Responsibility"] = n.AiData.Responsibility.ToString();
        }

        // Sub-records as semicolon-separated lists
        if (n.Factions is { Count: > 0 })
            d["Factions"] = string.Join("; ",
                n.Factions.Select(f => $"{Fmt.FId(f.FactionFormId)}:Rank{f.Rank}"));

        if (n.Inventory is { Count: > 0 })
            d["Inventory"] = string.Join("; ",
                n.Inventory.Select(i => $"{Fmt.FId(i.ItemFormId)}x{i.Count}"));

        if (n.Spells is { Count: > 0 })
            d["Spells"] = string.Join("; ", n.Spells.Select(Fmt.FId));

        if (n.Packages is { Count: > 0 })
            d["Packages"] = string.Join("; ", n.Packages.Select(Fmt.FId));

        return d;
    }

    internal static Dictionary<string, string> FlattenArmor(ArmorRecord a)
    {
        return new Dictionary<string, string>
        {
            ["DT"] = a.DamageThreshold.ToString("F1"),
            ["DR"] = a.DamageResistance.ToString(),
            ["Value"] = a.Value.ToString(),
            ["Weight"] = a.Weight.ToString("F2"),
            ["Health"] = a.Health.ToString(),
            ["ModelPath"] = a.ModelPath ?? ""
        };
    }

    internal static Dictionary<string, string> FlattenAmmo(AmmoRecord a, FormIdResolver resolver)
    {
        return new Dictionary<string, string>
        {
            ["Speed"] = a.Speed.ToString("F2"),
            ["Value"] = a.Value.ToString(),
            ["Weight"] = a.Weight.ToString("F2"),
            ["ClipRounds"] = a.ClipRounds.ToString(),
            ["Flags"] = a.Flags.ToString(),
            ["Projectile"] = ResolveRef(a.ProjectileFormId, resolver),
            ["ModelPath"] = a.ModelPath ?? ""
        };
    }

    internal static Dictionary<string, string> FlattenConsumable(ConsumableRecord c, FormIdResolver resolver)
    {
        var d = new Dictionary<string, string>
        {
            ["Value"] = c.Value.ToString(),
            ["Weight"] = c.Weight.ToString("F2"),
            ["Addiction"] = ResolveRef(c.AddictionFormId, resolver),
            ["AddictionChance"] = c.AddictionChance.ToString("F2"),
            ["ModelPath"] = c.ModelPath ?? ""
        };

        if (c.Effects.Count > 0)
            d["Effects"] = string.Join("; ", c.Effects.Select(e => Fmt.FId(e.EffectFormId)));

        return d;
    }

    internal static Dictionary<string, string> FlattenQuest(QuestRecord q)
    {
        var d = new Dictionary<string, string>
        {
            ["Flags"] = q.Flags.ToString(),
            ["Priority"] = q.Priority.ToString(),
            ["StageCount"] = q.Stages.Count.ToString(),
            ["ObjectiveCount"] = q.Objectives.Count.ToString()
        };

        if (q.Stages.Count > 0)
            d["Stages"] = string.Join("; ",
                q.Stages.Select(s => $"Stage{s.Index}(flags={s.Flags})"));

        if (q.Objectives.Count > 0)
            d["Objectives"] = string.Join("; ",
                q.Objectives.Select(o => $"Obj{o.Index}:{Fmt.CsvEscape(o.DisplayText)}"));

        return d;
    }

    internal static Dictionary<string, string> FlattenFaction(FactionRecord f)
    {
        var d = new Dictionary<string, string>
        {
            ["Flags"] = f.Flags.ToString(),
            ["IsHiddenFromPlayer"] = f.IsHiddenFromPlayer.ToString(),
            ["CrimeGoldMultiplier"] = f.CrimeGoldMultiplier.ToString("F2"),
            ["RankCount"] = f.Ranks.Count.ToString(),
            ["RelationCount"] = f.Relations.Count.ToString()
        };

        if (f.Ranks.Count > 0)
            d["Ranks"] = string.Join("; ",
                f.Ranks.Select(r => $"{r.RankNumber}:{r.MaleTitle ?? r.FemaleTitle ?? "(unnamed)"}"));

        return d;
    }

    internal static Dictionary<string, string> FlattenCell(CellRecord c)
    {
        return new Dictionary<string, string>
        {
            ["GridX"] = c.GridX?.ToString() ?? "",
            ["GridY"] = c.GridY?.ToString() ?? "",
            ["IsInterior"] = c.IsInterior.ToString(),
            ["HasWater"] = c.HasWater.ToString(),
            ["Flags"] = c.Flags.ToString(),
            ["HasHeightmap"] = (c.Heightmap != null).ToString(),
            ["PlacedObjectCount"] = c.PlacedObjects.Count.ToString(),
            ["Worldspace"] = Fmt.FIdN(c.WorldspaceFormId)
        };
    }

    internal static Dictionary<string, string> FlattenWorldspace(WorldspaceRecord w)
    {
        return new Dictionary<string, string>
        {
            ["Parent"] = Fmt.FIdN(w.ParentWorldspaceFormId),
            ["Climate"] = Fmt.FIdN(w.ClimateFormId),
            ["Water"] = Fmt.FIdN(w.WaterFormId),
            ["DefaultLandHeight"] = w.DefaultLandHeight?.ToString("F2") ?? "",
            ["DefaultWaterHeight"] = w.DefaultWaterHeight?.ToString("F2") ?? "",
            ["Flags"] = w.Flags?.ToString() ?? "",
            ["CellCount"] = w.Cells.Count.ToString()
        };
    }

    internal static Dictionary<string, string> FlattenCreature(CreatureRecord cr)
    {
        var d = new Dictionary<string, string>
        {
            ["CreatureType"] = cr.CreatureType.ToString(),
            ["Level"] = cr.Stats?.Level.ToString() ?? "",
            ["CombatSkill"] = cr.CombatSkill.ToString(),
            ["MagicSkill"] = cr.MagicSkill.ToString(),
            ["StealthSkill"] = cr.StealthSkill.ToString(),
            ["AttackDamage"] = cr.AttackDamage.ToString(),
            ["ModelPath"] = cr.ModelPath ?? ""
        };

        if (cr.Factions is { Count: > 0 })
            d["Factions"] = string.Join("; ",
                cr.Factions.Select(f => $"{Fmt.FId(f.FactionFormId)}:Rank{f.Rank}"));

        return d;
    }

    internal static Dictionary<string, string> FlattenRace(RaceRecord r)
    {
        return new Dictionary<string, string>
        {
            ["Description"] = r.Description ?? "",
            ["DataFlags"] = r.DataFlags.ToString(),
            ["IsPlayable"] = r.IsPlayable.ToString()
        };
    }

    internal static Dictionary<string, string> FlattenContainer(ContainerRecord c, FormIdResolver resolver)
    {
        var d = new Dictionary<string, string>
        {
            ["Respawns"] = c.Respawns.ToString(),
            ["ModelPath"] = c.ModelPath ?? "",
            ["Script"] = Fmt.FIdN(c.Script),
            ["ItemCount"] = c.Contents.Count.ToString()
        };

        if (c.Contents.Count > 0)
            d["Contents"] = string.Join("; ",
                c.Contents.Select(i =>
                    $"{resolver.ResolveCsv(i.ItemFormId)}x{i.Count}"));

        return d;
    }

    internal static Dictionary<string, string> FlattenPerk(PerkRecord p)
    {
        return new Dictionary<string, string>
        {
            ["Description"] = p.Description ?? "",
            ["MinLevel"] = p.MinLevel.ToString(),
            ["Playable"] = p.IsPlayable.ToString(),
            ["Trait"] = p.IsTrait.ToString(),
            ["Ranks"] = p.Ranks.ToString()
        };
    }

    internal static Dictionary<string, string> FlattenMiscItem(MiscItemRecord m)
    {
        return new Dictionary<string, string>
        {
            ["Value"] = m.Value.ToString(),
            ["Weight"] = m.Weight.ToString("F2"),
            ["ModelPath"] = m.ModelPath ?? ""
        };
    }

    internal static Dictionary<string, string> FlattenKey(KeyRecord k)
    {
        return new Dictionary<string, string>
        {
            ["Value"] = k.Value.ToString(),
            ["Weight"] = k.Weight.ToString("F2"),
            ["ModelPath"] = k.ModelPath ?? ""
        };
    }

    internal static Dictionary<string, string> FlattenBook(BookRecord b, FormIdResolver resolver)
    {
        return new Dictionary<string, string>
        {
            ["Value"] = b.Value.ToString(),
            ["Weight"] = b.Weight.ToString("F2"),
            ["Flags"] = $"0x{b.Flags:X2}",
            ["TeachesSkill"] = b.TeachesSkill.ToString(),
            ["SkillTaught"] = b.TeachesSkill
                ? resolver.GetSkillName(b.SkillTaught) ?? b.SkillTaught.ToString()
                : "",
            ["Enchantment"] = Fmt.FIdN(b.EnchantmentFormId),
            ["Text"] = b.Text ?? "",
            ["ModelPath"] = b.ModelPath ?? ""
        };
    }

    internal static Dictionary<string, string> FlattenNote(NoteRecord n)
    {
        return new Dictionary<string, string>
        {
            ["NoteType"] = n.NoteTypeName,
            ["Text"] = n.Text ?? ""
        };
    }

    internal static Dictionary<string, string> FlattenTerminal(TerminalRecord t)
    {
        return new Dictionary<string, string>
        {
            ["Difficulty"] = t.DifficultyName,
            ["Flags"] = t.Flags.ToString(),
            ["MenuItemCount"] = t.MenuItems.Count.ToString()
        };
    }

    internal static Dictionary<string, string> FlattenLeveledList(LeveledListRecord ll)
    {
        return new Dictionary<string, string>
        {
            ["ListType"] = ll.ListType,
            ["ChanceNone"] = ll.ChanceNone.ToString(),
            ["Flags"] = ll.Flags.ToString(),
            ["EntryCount"] = ll.Entries.Count.ToString()
        };
    }

    internal static Dictionary<string, string> FlattenSpell(SpellRecord s)
    {
        return new Dictionary<string, string>
        {
            ["SpellType"] = s.TypeName,
            ["Cost"] = s.Cost.ToString(),
            ["Level"] = s.Level.ToString(),
            ["Flags"] = s.Flags.ToString(),
            ["EffectCount"] = s.Effects.Count.ToString()
        };
    }

    /// <summary>
    ///     Format a nullable FormID reference as "EditorId (0xHEX)" or just "0xHEX".
    /// </summary>
    private static string ResolveRef(uint? formId, FormIdResolver resolver)
    {
        if (formId is null or 0) return "";
        var editorId = resolver.ResolveCsv(formId.Value);
        var hex = Fmt.FId(formId.Value);
        return !string.IsNullOrEmpty(editorId) ? $"{editorId} ({hex})" : hex;
    }
}
