using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Generates structured reports for individual records by delegating to existing
///     Geck report writers. Used by the cross-dump comparison to produce per-record data.
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
            ScriptRecord s => GeckScriptWriter.BuildScriptReport(s, resolver),
            DialogTopicRecord dt => GeckDialogueWriter.BuildDialogTopicReport(dt, resolver),
            DialogueRecord d => GeckDialogueWriter.BuildDialogueReport(d, resolver),
            _ => null
        };
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
        foreach (var s in records.Scripts)
            yield return ("Script", s.FormId, s.EditorId, null, s);
        foreach (var dt in records.DialogTopics)
            yield return ("DialogTopic", dt.FormId, dt.EditorId, dt.FullName, dt);
        foreach (var d in records.Dialogues)
            yield return ("Dialogue", d.FormId, d.EditorId, null, d);
    }
}
