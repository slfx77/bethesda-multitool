using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Processing;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

internal static class FormListRecordScanner
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
        var formIds = new List<uint>();

        foreach (var subrecord in subrecords)
        {
            if (subrecord.Signature != "LNAM" || subrecord.Data.Length != 4)
            {
                continue;
            }

            var formId = BinaryUtils.ReadUInt32(
                subrecord.Data,
                0,
                bigEndian);
            if (formId != 0)
            {
                formIds.Add(formId);
            }
        }

        return formIds.Count > 0 ? formIds : null;
    }
}
