using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Models;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

/// <summary>Parses an armor add-on (ARMA) record into a scan entry of its biped-slot meshes for NPC assembly.</summary>
internal static class ArmorAddonRecordScanner
{
    internal static ArmaAddonScanEntry? Process(
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
        string? maleModel = null;
        string? femaleModel = null;
        uint bipedFlags = 0;

        foreach (var subrecord in subrecords)
        {
            switch (subrecord.Signature)
            {
                case "EDID":
                    editorId = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "MODL":
                    maleModel = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "MOD2":
                    femaleModel = EsmRecordParser.GetSubrecordString(subrecord);
                    break;
                case "BMDT" when subrecord.Data.Length >= 8:
                {
                    if (SubrecordSchemaView.TryRead("BMDT", null, subrecord.Data, bigEndian) is { } v)
                    {
                        bipedFlags = v.UInt32("BipedFlags");
                    }

                    break;
                }
            }
        }

        if (bipedFlags == 0 || (maleModel == null && femaleModel == null))
        {
            return null;
        }

        return new ArmaAddonScanEntry
        {
            EditorId = editorId,
            BipedFlags = bipedFlags,
            MaleModelPath = maleModel,
            FemaleModelPath = femaleModel
        };
    }
}


