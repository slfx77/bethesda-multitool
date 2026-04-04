using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Generates full-text detail blocks for individual records by delegating to existing
///     Geck report writers. Used by the cross-dump HTML comparison to produce per-record text.
/// </summary>
internal static class RecordTextFormatter
{
    /// <summary>
    ///     Build a structured report for a single record.
    ///     Returns null if the record type is not supported.
    /// </summary>
    internal static RecordReport? BuildReport(object record, FormIdResolver resolver)
    {
        return record switch
        {
            WeaponRecord w => GeckItemDetailWriter.BuildWeaponReport(w, resolver),
            NpcRecord n => GeckActorDetailWriter.BuildNpcReport(n, resolver),
            ContainerRecord c => GeckItemDetailWriter.BuildContainerReport(c, resolver),
            QuestRecord q => GeckDialogueWriter.BuildQuestReport(q, resolver),
            FactionRecord f => GeckFactionWriter.BuildFactionReport(f, resolver),
            CreatureRecord cr => GeckCreatureWriter.BuildCreatureReport(cr, resolver),
            RaceRecord r => GeckCreatureWriter.BuildRaceReport(r, resolver),
            ProjectileRecord p => GeckWorldObjectWriter.BuildProjectileReport(p, resolver),
            ExplosionRecord e => GeckWorldObjectWriter.BuildExplosionReport(e, resolver),
            LeveledListRecord l => GeckMiscWriter.BuildLeveledListReport(l, resolver),
            WeaponModRecord m => GeckItemDetailWriter.BuildWeaponModReport(m),
            RecipeRecord r => GeckItemDetailWriter.BuildRecipeReport(r, resolver),
            PerkRecord p => GeckEffectsWriter.BuildPerkReport(p, resolver),
            SpellRecord s => GeckEffectsWriter.BuildSpellReport(s, resolver),
            ArmorRecord a => GeckItemWriter.BuildArmorReport(a, resolver),
            AmmoRecord a => GeckItemWriter.BuildAmmoReport(a, resolver),
            ConsumableRecord c => GeckItemWriter.BuildConsumableReport(c, resolver),
            MiscItemRecord m => GeckItemWriter.BuildMiscItemReport(m, resolver),
            KeyRecord k => GeckItemWriter.BuildKeyReport(k, resolver),
            CellRecord c => GeckWorldWriter.BuildCellReport(c, resolver),
            WorldspaceRecord w => GeckWorldWriter.BuildWorldspaceReport(w, resolver),
            _ => null
        };
    }

    /// <summary>
    ///     Format a single record into a multi-line detail text block.
    ///     Returns null if the record type is not supported.
    /// </summary>
    internal static string? FormatRecord(object record, FormIdResolver resolver)
    {
        var sb = new StringBuilder();

        switch (record)
        {
            case WeaponRecord w:
                GeckItemDetailWriter.AppendWeaponReportEntry(sb, w, resolver);
                break;
            case NpcRecord n:
                GeckActorDetailWriter.AppendNpcReportEntry(sb, n, resolver);
                break;
            case ContainerRecord c:
                GeckContainerWriter.AppendContainerReportEntry(sb, c, resolver);
                break;
            case ArmorRecord:
                AppendSingleFromSection(sb, record, resolver,
                    (s, r) => GeckItemWriter.AppendArmorSection(s, [(ArmorRecord)r]));
                break;
            case AmmoRecord:
                AppendSingleFromSection(sb, record, resolver,
                    (s, r) => GeckItemWriter.AppendAmmoSection(s, [(AmmoRecord)r], resolver));
                break;
            case ConsumableRecord:
                AppendSingleFromSection(sb, record, resolver,
                    (s, r) => GeckItemWriter.AppendConsumablesSection(s, [(ConsumableRecord)r], resolver));
                break;
            case MiscItemRecord:
                AppendSingleFromSection(sb, record, resolver,
                    (s, r) => GeckItemWriter.AppendMiscItemsSection(s, [(MiscItemRecord)r]));
                break;
            case KeyRecord:
                AppendSingleFromSection(sb, record, resolver,
                    (s, r) => GeckItemWriter.AppendKeysSection(s, [(KeyRecord)r]));
                break;
            case QuestRecord:
                AppendSingleFromSection(sb, record, resolver,
                    (s, r) => GeckDialogueWriter.AppendQuestsSection(s, [(QuestRecord)r], resolver));
                break;
            case FactionRecord:
                AppendSingleFromSection(sb, record, resolver,
                    (s, r) => GeckFactionWriter.AppendFactionsSection(s, [(FactionRecord)r], resolver));
                break;
            case CreatureRecord:
                AppendSingleFromSection(sb, record, resolver,
                    (s, r) => GeckCreatureWriter.AppendCreaturesSection(s, [(CreatureRecord)r]));
                break;
            case RaceRecord:
                AppendSingleFromSection(sb, record, resolver,
                    (s, r) => GeckCreatureWriter.AppendRacesSection(s, [(RaceRecord)r], resolver));
                break;
            case CellRecord:
                AppendSingleFromSection(sb, record, resolver,
                    (s, r) => GeckWorldWriter.AppendCellsSection(s, [(CellRecord)r], resolver));
                break;
            case WorldspaceRecord:
                AppendSingleFromSection(sb, record, resolver,
                    (s, r) => GeckWorldWriter.AppendWorldspacesSection(s, [(WorldspaceRecord)r], resolver));
                break;
            case PerkRecord:
                AppendSingleFromSection(sb, record, resolver,
                    (s, r) => GeckEffectsWriter.AppendPerksSection(s, [(PerkRecord)r], resolver));
                break;
            case SpellRecord:
                AppendSingleFromSection(sb, record, resolver,
                    (s, r) => GeckEffectsWriter.AppendSpellsSection(s, [(SpellRecord)r], resolver));
                break;
            case ProjectileRecord:
                AppendSingleFromSection(sb, record, resolver,
                    (s, r) => GeckWorldObjectWriter.AppendProjectilesSection(s, [(ProjectileRecord)r], resolver));
                break;
            case ExplosionRecord:
                AppendSingleFromSection(sb, record, resolver,
                    (s, r) => GeckWorldObjectWriter.AppendExplosionsSection(s, [(ExplosionRecord)r], resolver));
                break;
            case LeveledListRecord:
                AppendSingleFromSection(sb, record, resolver,
                    (s, r) => GeckMiscWriter.AppendLeveledListsSection(s, [(LeveledListRecord)r], resolver));
                break;
            case WeaponModRecord:
                AppendSingleFromSection(sb, record, resolver,
                    (s, r) => GeckWeaponWriter.AppendWeaponModsSection(s, [(WeaponModRecord)r]));
                break;
            case RecipeRecord:
                AppendSingleFromSection(sb, record, resolver,
                    (s, r) => GeckContainerWriter.AppendRecipesSection(s, [(RecipeRecord)r], resolver));
                break;
            default:
                return null;
        }

        return StripNoiseLines(sb.ToString().Trim());
    }

    /// <summary>
    ///     Remove lines that are not meaningful in a cross-dump comparison context.
    ///     Offset changes every dump (crash dumps), Endianness is always the same.
    /// </summary>
    private static string StripNoiseLines(string text)
    {
        var lines = text.Split('\n');
        var filtered = lines
            .Select(l => l.TrimEnd())
            .Where(line =>
            {
                var trimmed = line.TrimStart();
                return !trimmed.StartsWith("Offset:", StringComparison.Ordinal) &&
                       !trimmed.StartsWith("Endianness:", StringComparison.Ordinal);
            });
        return string.Join('\n', filtered);
    }

    /// <summary>
    ///     Calls a section writer with a single-element list, then strips the section header
    ///     (everything before the first record header line).
    /// </summary>
    private static void AppendSingleFromSection(
        StringBuilder sb, object record, FormIdResolver resolver,
        Action<StringBuilder, object> writeSection)
    {
        var tempSb = new StringBuilder();
        writeSection(tempSb, record);
        var text = tempSb.ToString();

        // Strip the section header (lines before the first record-level content)
        // Section headers are lines like "═══ Quests (1) ═══" followed by blank lines
        var lines = text.Split('\n');
        var startIdx = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimEnd('\r');
            // Skip section header lines (contain ═ or are blank at the start)
            if (trimmed.Contains('═') || (i < 3 && string.IsNullOrWhiteSpace(trimmed)))
            {
                startIdx = i + 1;
                continue;
            }

            break;
        }

        for (var i = startIdx; i < lines.Length; i++)
        {
            sb.Append(lines[i]);
            if (i < lines.Length - 1) sb.Append('\n');
        }
    }

    /// <summary>
    ///     Enumerate all records from a RecordCollection, yielding (typeName, formId, editorId, displayName, record).
    /// </summary>
    internal static IEnumerable<(string TypeName, uint FormId, string? EditorId, string? DisplayName, object Record)>
        EnumerateAll(RecordCollection records)
    {
        foreach (var w in records.Weapons)
            yield return ("Weapon", w.FormId, w.EditorId, w.FullName, w);
        foreach (var n in records.Npcs)
            yield return ("NPC", n.FormId, n.EditorId, n.FullName, n);
        foreach (var a in records.Armor)
            yield return ("Armor", a.FormId, a.EditorId, a.FullName, a);
        foreach (var a in records.Ammo)
            yield return ("Ammo", a.FormId, a.EditorId, a.FullName, a);
        foreach (var c in records.Consumables)
            yield return ("Consumable", c.FormId, c.EditorId, c.FullName, c);
        foreach (var m in records.MiscItems)
            yield return ("MiscItem", m.FormId, m.EditorId, m.FullName, m);
        foreach (var k in records.Keys)
            yield return ("Key", k.FormId, k.EditorId, k.FullName, k);
        foreach (var c in records.Containers)
            yield return ("Container", c.FormId, c.EditorId, c.FullName, c);
        foreach (var q in records.Quests)
            yield return ("Quest", q.FormId, q.EditorId, q.FullName, q);
        foreach (var f in records.Factions)
            yield return ("Faction", f.FormId, f.EditorId, f.FullName, f);
        foreach (var c in records.Creatures)
            yield return ("Creature", c.FormId, c.EditorId, c.FullName, c);
        foreach (var r in records.Races)
            yield return ("Race", r.FormId, r.EditorId, r.FullName, r);
        foreach (var c in records.Cells)
            yield return ("Cell", c.FormId, c.EditorId, c.FullName, c);
        foreach (var w in records.Worldspaces)
            yield return ("Worldspace", w.FormId, w.EditorId, w.FullName, w);
        foreach (var p in records.Perks)
            yield return ("Perk", p.FormId, p.EditorId, p.FullName, p);
        foreach (var s in records.Spells)
            yield return ("Spell", s.FormId, s.EditorId, s.FullName, s);
        foreach (var p in records.Projectiles)
            yield return ("Projectile", p.FormId, p.EditorId, p.FullName, p);
        foreach (var e in records.Explosions)
            yield return ("Explosion", e.FormId, e.EditorId, e.FullName, e);
        foreach (var l in records.LeveledLists)
            yield return ("LeveledList", l.FormId, l.EditorId, null, l);
        foreach (var m in records.WeaponMods)
            yield return ("WeaponMod", m.FormId, m.EditorId, m.FullName, m);
        foreach (var r in records.Recipes)
            yield return ("Recipe", r.FormId, r.EditorId, r.FullName, r);
    }
}
