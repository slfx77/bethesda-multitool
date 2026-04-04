using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Processing;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

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
