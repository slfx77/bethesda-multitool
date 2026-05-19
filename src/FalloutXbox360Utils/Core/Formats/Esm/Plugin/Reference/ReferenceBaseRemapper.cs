using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;

internal static class ReferenceBaseRemapper
{
    public static readonly HashSet<string> RefrBaseEligibleTypes = new(StringComparer.Ordinal)
    {
        "STAT", "SCOL", "CONT", "ACTI", "DOOR", "LIGH", "FURN",
        "NPC_", "CREA", "ARMO", "ALCH", "WEAP", "MISC", "KEYM",
        "NOTE", "BOOK", "TERM", "TXST"
    };

    public static bool CanPlacedRecordUseBaseType(string placedRecordType, string baseRecordType)
    {
        return placedRecordType switch
        {
            "ACHR" => baseRecordType == "NPC_",
            "ACRE" => baseRecordType == "CREA",
            "REFR" => baseRecordType is not ("NPC_" or "CREA"),
            _ => false
        };
    }

    public static Dictionary<uint, string> BuildDmpBaseFormIdToRecordType(RecordCollection dmpRecords)
    {
        var index = new Dictionary<uint, string>();

        void Add(uint formId, string type)
        {
            if (formId != 0)
            {
                index.TryAdd(formId, type);
            }
        }

        foreach (var r in dmpRecords.Statics) Add(r.FormId, "STAT");
        foreach (var r in dmpRecords.StaticCollections) Add(r.FormId, "SCOL");
        foreach (var r in dmpRecords.Containers) Add(r.FormId, "CONT");
        foreach (var r in dmpRecords.Activators) Add(r.FormId, "ACTI");
        foreach (var r in dmpRecords.Doors) Add(r.FormId, "DOOR");
        foreach (var r in dmpRecords.Lights) Add(r.FormId, "LIGH");
        foreach (var r in dmpRecords.Furniture) Add(r.FormId, "FURN");
        foreach (var r in dmpRecords.Npcs) Add(r.FormId, "NPC_");
        foreach (var r in dmpRecords.Creatures) Add(r.FormId, "CREA");
        foreach (var r in dmpRecords.Armor) Add(r.FormId, "ARMO");
        foreach (var r in dmpRecords.Consumables) Add(r.FormId, "ALCH");
        foreach (var r in dmpRecords.Weapons) Add(r.FormId, "WEAP");
        foreach (var r in dmpRecords.MiscItems) Add(r.FormId, "MISC");
        foreach (var r in dmpRecords.Keys) Add(r.FormId, "KEYM");
        foreach (var r in dmpRecords.Notes) Add(r.FormId, "NOTE");
        foreach (var r in dmpRecords.Books) Add(r.FormId, "BOOK");
        foreach (var r in dmpRecords.Terminals) Add(r.FormId, "TERM");
        foreach (var r in dmpRecords.TextureSets) Add(r.FormId, "TXST");

        return index;
    }

    public static uint? TryFindMasterBaseByEditorIdStem(
        Dictionary<string, Dictionary<string, List<uint>>> stemLookup,
        string? prototypeBaseEditorId,
        string expectedBaseType,
        out bool ambiguous,
        out List<uint>? candidates)
    {
        ambiguous = false;
        candidates = null;

        if (!RefrBaseEligibleTypes.Contains(expectedBaseType))
        {
            return null;
        }

        var stem = EditorIdStem.Normalize(prototypeBaseEditorId);
        if (stem is null)
        {
            return null;
        }

        if (!stemLookup.TryGetValue(expectedBaseType, out var byStem)
            || !byStem.TryGetValue(stem, out var hits)
            || hits.Count == 0)
        {
            return null;
        }

        if (hits.Count > 1)
        {
            ambiguous = true;
            candidates = hits;
            return null;
        }

        return hits[0];
    }
}
