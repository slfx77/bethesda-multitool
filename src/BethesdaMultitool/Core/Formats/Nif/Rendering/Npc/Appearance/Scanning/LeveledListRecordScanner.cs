using BethesdaMultitool.Core.Formats.Esm.Conversion.Models;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

/// <summary>Parses a leveled-list (LVLI/LVLN) record into the list of FormIDs it can produce.</summary>
internal static class LeveledListRecordScanner
{
    internal static List<uint>? Process(
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
        var entryFormIds = new List<uint>();

        foreach (var subrecord in subrecords)
        {
            if (subrecord.Signature != "LVLO" || subrecord.Data.Length < 8)
            {
                continue;
            }

            var entryFormId = BinaryUtils.ReadUInt32(
                subrecord.Data,
                4,
                bigEndian);
            if (entryFormId != 0)
            {
                entryFormIds.Add(entryFormId);
            }
        }

        return entryFormIds.Count > 0 ? entryFormIds : null;
    }
}
