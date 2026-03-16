using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance;

internal static class NpcAppearanceIndexBuilder
{
    internal static NpcAppearanceIndex Build(byte[] esmData, bool bigEndian)
    {
        var index = new NpcAppearanceIndex();
        var records = EsmRecordParser.ScanAllRecords(esmData, bigEndian);

        foreach (var record in records)
        {
            switch (record.Signature)
            {
                case "NPC_":
                    AddIfPresent(
                        index.Npcs,
                        record.FormId,
                        NpcRecordScanner.Process(esmData, bigEndian, record));
                    break;
                case "RACE":
                    AddIfPresent(
                        index.Races,
                        record.FormId,
                        RaceRecordScanner.Process(esmData, bigEndian, record));
                    break;
                case "HAIR":
                    AddIfPresent(
                        index.Hairs,
                        record.FormId,
                        HairRecordScanner.Process(esmData, bigEndian, record));
                    break;
                case "EYES":
                    AddIfPresent(
                        index.Eyes,
                        record.FormId,
                        EyesRecordScanner.Process(esmData, bigEndian, record));
                    break;
                case "HDPT":
                    AddIfPresent(
                        index.HeadParts,
                        record.FormId,
                        HeadPartRecordScanner.Process(esmData, bigEndian, record));
                    break;
                case "ARMO":
                    AddIfPresent(
                        index.Armors,
                        record.FormId,
                        ArmorRecordScanner.Process(esmData, bigEndian, record));
                    break;
                case "ARMA":
                    AddIfPresent(
                        index.ArmorAddons,
                        record.FormId,
                        ArmorAddonRecordScanner.Process(esmData, bigEndian, record));
                    break;
                case "WEAP":
                    AddIfPresent(
                        index.Weapons,
                        record.FormId,
                        WeaponRecordScanner.Process(esmData, bigEndian, record));
                    break;
                case "PACK":
                    AddIfPresent(
                        index.Packages,
                        record.FormId,
                        PackageRecordScanner.Process(esmData, bigEndian, record));
                    break;
                case "IDLE":
                {
                    var idle = IdleRecordScanner.Process(esmData, bigEndian, record);
                    AddIfPresent(
                        index.Idles,
                        record.FormId,
                        idle);
                    if (idle?.ParentIdleFormId is uint parentIdleFormId)
                    {
                        if (!index.IdleChildrenByParent.TryGetValue(parentIdleFormId, out var children))
                        {
                            children = [];
                            index.IdleChildrenByParent[parentIdleFormId] = children;
                        }

                        children.Add(record.FormId);
                    }

                    break;
                }
                case "FLST":
                    AddIfPresent(
                        index.FormLists,
                        record.FormId,
                        FormListRecordScanner.Process(esmData, bigEndian, record));
                    break;
                case "LVLI":
                    AddIfPresent(
                        index.LeveledItems,
                        record.FormId,
                        LeveledListRecordScanner.Process(esmData, bigEndian, record));
                    break;
                case "LVLN":
                    AddIfPresent(
                        index.LeveledNpcs,
                        record.FormId,
                        LeveledListRecordScanner.Process(esmData, bigEndian, record));
                    break;
            }
        }

        return index;
    }

    private static void AddIfPresent<TValue>(
        Dictionary<uint, TValue> index,
        uint formId,
        TValue? value)
        where TValue : class
    {
        if (value != null)
        {
            index[formId] = value;
        }
    }

    private static void AddIfPresent(
        Dictionary<uint, List<uint>> index,
        uint formId,
        List<uint>? value)
    {
        if (value != null)
        {
            index[formId] = value;
        }
    }
}
