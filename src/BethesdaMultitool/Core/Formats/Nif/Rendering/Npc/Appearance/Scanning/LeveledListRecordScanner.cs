using BethesdaMultitool.Core.Formats.Esm.Conversion.Models;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

/// <summary>Parses the retail metadata and entries authored in a leveled-list (LVLI/LVLN) record.</summary>
internal static class LeveledListRecordScanner
{
    internal static LeveledListScanEntry? Process(
        byte[] esmData,
        bool bigEndian,
        AnalyzerRecordInfo record)
    {
        var recordData = NpcRecordDataReader.ReadRecordData(
            esmData,
            bigEndian,
            record);
        if (recordData == null)
        {
            return null;
        }

        var subrecords = EsmRecordParser.ParseSubrecords(recordData, bigEndian);
        string? editorId = null;
        byte chanceNone = 0;
        byte flags = 0;
        var entries = new List<LeveledEntry>();
        var sawLeveledMetadata = false;

        foreach (var subrecord in subrecords)
        {
            switch (subrecord.Signature)
            {
                case "EDID":
                    editorId = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "LVLD" when subrecord.Data.Length >= 1:
                    chanceNone = subrecord.Data[0];
                    sawLeveledMetadata = true;
                    break;
                case "LVLF" when subrecord.Data.Length >= 1:
                    flags = subrecord.Data[0];
                    sawLeveledMetadata = true;
                    break;
                case "LVLO" when subrecord.Data.Length >= 8:
                {
                    var level = BinaryUtils.ReadUInt16(subrecord.Data, 0, bigEndian);
                    var entryFormId = BinaryUtils.ReadUInt32(subrecord.Data, 4, bigEndian);
                    var count = subrecord.Data.Length >= 10
                        ? BinaryUtils.ReadUInt16(subrecord.Data, 8, bigEndian)
                        : (ushort)1;
                    if (entryFormId != 0)
                    {
                        entries.Add(new LeveledEntry(level, entryFormId, count));
                    }

                    sawLeveledMetadata = true;
                    break;
                }
            }
        }

        return sawLeveledMetadata
            ? new LeveledListScanEntry
            {
                EditorId = editorId,
                ChanceNone = chanceNone,
                Flags = flags,
                Entries = entries
            }
            : null;
    }
}
