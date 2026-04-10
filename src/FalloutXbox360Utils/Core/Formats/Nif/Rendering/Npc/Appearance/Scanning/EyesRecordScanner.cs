using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Processing;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

internal static class EyesRecordScanner
{
    internal static EyesScanEntry? Process(
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
        string? texturePath = null;

        foreach (var subrecord in subrecords)
        {
            switch (subrecord.Signature)
            {
                case "EDID":
                    editorId = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "ICON":
                    texturePath = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
            }
        }

        return new EyesScanEntry
        {
            EditorId = editorId,
            TexturePath = texturePath
        };
    }
}
