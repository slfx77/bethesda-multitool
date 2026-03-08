using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

internal static class HeadPartRecordScanner
{
    internal static HdptScanEntry? Process(
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
        string? modelPath = null;

        foreach (var subrecord in subrecords)
        {
            switch (subrecord.Signature)
            {
                case "EDID":
                    editorId = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "MODL":
                    modelPath = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
            }
        }

        return new HdptScanEntry
        {
            EditorId = editorId,
            ModelPath = modelPath
        };
    }
}
