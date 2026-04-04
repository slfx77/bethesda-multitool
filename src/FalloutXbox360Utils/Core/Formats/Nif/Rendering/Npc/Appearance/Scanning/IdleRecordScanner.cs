using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Processing;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

internal static class IdleRecordScanner
{
    internal static IdleScanEntry? Process(
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
        uint? parentIdleFormId = null;
        uint? previousIdleFormId = null;
        byte animData = 0;
        byte loopMin = 0;
        byte loopMax = 0;
        ushort replayDelay = 0;
        byte flagsEx = 0;

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
                case "ANAM" when subrecord.Data.Length >= 8:
                    parentIdleFormId = BinaryUtils.ReadUInt32(
                        subrecord.Data,
                        0,
                        bigEndian);
                    previousIdleFormId = BinaryUtils.ReadUInt32(
                        subrecord.Data,
                        4,
                        bigEndian);
                    if (parentIdleFormId == 0)
                    {
                        parentIdleFormId = null;
                    }

                    if (previousIdleFormId == 0)
                    {
                        previousIdleFormId = null;
                    }

                    break;
                case "DATA" when subrecord.Data.Length >= 6:
                    animData = subrecord.Data[0];
                    loopMin = subrecord.Data[1];
                    loopMax = subrecord.Data[2];
                    replayDelay = BinaryUtils.ReadUInt16(
                        subrecord.Data,
                        4,
                        bigEndian);
                    if (subrecord.Data.Length >= 7)
                    {
                        flagsEx = subrecord.Data[6];
                    }

                    break;
            }
        }

        if (editorId == null &&
            modelPath == null &&
            parentIdleFormId == null &&
            previousIdleFormId == null &&
            animData == 0 &&
            loopMin == 0 &&
            loopMax == 0 &&
            replayDelay == 0 &&
            flagsEx == 0)
        {
            return null;
        }

        return new IdleScanEntry
        {
            EditorId = editorId,
            ModelPath = modelPath,
            ParentIdleFormId = parentIdleFormId,
            PreviousIdleFormId = previousIdleFormId,
            AnimData = animData,
            LoopMin = loopMin,
            LoopMax = loopMax,
            ReplayDelay = replayDelay,
            FlagsEx = flagsEx
        };
    }
}
