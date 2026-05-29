using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers;

/// <summary>
///     Walks outgoing FormID references on a parsed <see cref="NpcRecord" />: simple-pointer
///     subrecords (RNAM/SCRI/CNAM/INAM/VTCK/TPLT/ZNAM head/eyes/combat-style/original-race/
///     face-NPC), repeated lists (SNAM factions, SPLO spells, CNTO inventory + COED owner,
///     PKID packages, HNAM head parts).
/// </summary>
public sealed class NpcReferenceWalker : IRecordReferenceWalker
{
    public string RecordType => "NPC_";

    public Type ModelType => typeof(NpcRecord);

    public IEnumerable<RawReference> Walk(object model)
    {
        if (model is not NpcRecord npc)
        {
            yield break;
        }

        foreach (var raw in YieldOptional(npc.Race, "RNAM"))
        {
            yield return raw;
        }

        foreach (var raw in YieldOptional(npc.Script, "SCRI"))
        {
            yield return raw;
        }

        foreach (var raw in YieldOptional(npc.Class, "CNAM"))
        {
            yield return raw;
        }

        foreach (var raw in YieldOptional(npc.DeathItem, "INAM"))
        {
            yield return raw;
        }

        foreach (var raw in YieldOptional(npc.VoiceType, "VTCK"))
        {
            yield return raw;
        }

        foreach (var raw in YieldOptional(npc.Template, "TPLT"))
        {
            yield return raw;
        }

        foreach (var raw in YieldOptional(npc.HairFormId, "HNAM"))
        {
            yield return raw;
        }

        foreach (var raw in YieldOptional(npc.EyesFormId, "ENAM"))
        {
            yield return raw;
        }

        foreach (var raw in YieldOptional(npc.CombatStyleFormId, "ZNAM"))
        {
            yield return raw;
        }

        foreach (var raw in YieldOptional(npc.OriginalRace, "ONAM"))
        {
            yield return raw;
        }

        foreach (var raw in YieldOptional(npc.FaceNpc, "FNAM"))
        {
            yield return raw;
        }

        for (var i = 0; i < npc.Factions.Count; i++)
        {
            yield return new RawReference
            {
                FieldPath = FieldPath.IndexedMember("SNAM", i, "Faction"),
                FormId = npc.Factions[i].FactionFormId,
            };
        }

        for (var i = 0; i < npc.Spells.Count; i++)
        {
            yield return new RawReference
            {
                FieldPath = FieldPath.Indexed("SPLO", i),
                FormId = npc.Spells[i],
            };
        }

        for (var i = 0; i < npc.Inventory.Count; i++)
        {
            var item = npc.Inventory[i];
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

        for (var i = 0; i < npc.Packages.Count; i++)
        {
            yield return new RawReference
            {
                FieldPath = FieldPath.Indexed("PKID", i),
                FormId = npc.Packages[i],
            };
        }

        if (npc.HeadPartFormIds is { } headParts)
        {
            for (var i = 0; i < headParts.Count; i++)
            {
                yield return new RawReference
                {
                    FieldPath = FieldPath.Indexed("PNAM", i),
                    FormId = headParts[i],
                };
            }
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
