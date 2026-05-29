using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers;

/// <summary>
///     Walks outgoing FormID references on a parsed <see cref="CreatureRecord" />: scripts,
///     death-item / equipped-item / template / voice-type / combat-style, sound inheritance,
///     death-item leveled-list, impact / body data, plus the SNAM / CNTO / SPLO / PKID
///     repeated lists. Sibling of <see cref="NpcReferenceWalker" /> with creature-specific
///     fields (CSCR sound-inherit, LNAM death-loot, CNAM impact data, PNAM body data).
/// </summary>
public sealed class CreatureReferenceWalker : IRecordReferenceWalker
{
    public string RecordType => "CREA";

    public Type ModelType => typeof(CreatureRecord);

    public IEnumerable<RawReference> Walk(object model)
    {
        if (model is not CreatureRecord crea)
        {
            yield break;
        }

        foreach (var raw in YieldOptional(crea.Script, "SCRI"))
        {
            yield return raw;
        }

        foreach (var raw in YieldOptional(crea.DeathItem, "INAM"))
        {
            yield return raw;
        }

        foreach (var raw in YieldOptional(crea.EquippedItem, "EITM"))
        {
            yield return raw;
        }

        foreach (var raw in YieldOptional(crea.Template, "TPLT"))
        {
            yield return raw;
        }

        foreach (var raw in YieldOptional(crea.VoiceType, "VTCK"))
        {
            yield return raw;
        }

        foreach (var raw in YieldOptional(crea.CombatStyleFormId, "ZNAM"))
        {
            yield return raw;
        }

        foreach (var raw in YieldOptional(crea.InheritsSoundsFrom, "CSCR"))
        {
            yield return raw;
        }

        foreach (var raw in YieldOptional(crea.DeathItemLootList, "LNAM"))
        {
            yield return raw;
        }

        foreach (var raw in YieldOptional(crea.ImpactDataSet, "CNAM"))
        {
            yield return raw;
        }

        foreach (var raw in YieldOptional(crea.BodyData, "PNAM"))
        {
            yield return raw;
        }

        for (var i = 0; i < crea.Factions.Count; i++)
        {
            yield return new RawReference
            {
                FieldPath = FieldPath.IndexedMember("SNAM", i, "Faction"),
                FormId = crea.Factions[i].FactionFormId,
            };
        }

        for (var i = 0; i < crea.Spells.Count; i++)
        {
            yield return new RawReference
            {
                FieldPath = FieldPath.Indexed("SPLO", i),
                FormId = crea.Spells[i],
            };
        }

        for (var i = 0; i < crea.Inventory.Count; i++)
        {
            var item = crea.Inventory[i];
            yield return new RawReference
            {
                FieldPath = FieldPath.IndexedMember("CNTO", i, "Item"),
                FormId = item.ItemFormId,
            };
            if (item.OwnerFormId is uint owner && owner != 0)
            {
                yield return new RawReference
                {
                    FieldPath = FieldPath.IndexedMember("COED", i, "Owner"),
                    FormId = owner,
                };
            }
        }

        for (var i = 0; i < crea.Packages.Count; i++)
        {
            yield return new RawReference
            {
                FieldPath = FieldPath.Indexed("PKID", i),
                FormId = crea.Packages[i],
            };
        }
    }

    private static IEnumerable<RawReference> YieldOptional(uint? formId, string signature)
    {
        if (formId is uint id && id != 0)
        {
            yield return new RawReference
            {
                FieldPath = FieldPath.Subrecord(signature),
                FormId = id,
            };
        }
    }
}
