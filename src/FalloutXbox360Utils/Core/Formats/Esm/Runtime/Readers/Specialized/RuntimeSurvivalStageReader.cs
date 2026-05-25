using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Shared typed runtime reader for the 4 hardcore-mode survival stage
///     records — RADS (0x5A, radiation), DEHY (0x76, dehydration),
///     HUNG (0x77, hunger), SLPD (0x78, sleep deprivation). All four share
///     the same 48-byte PDB layout with a single 8-byte "data" struct (threshold
///     uint32, modifier uint32). PDB field name is consistent across the four
///     classes (BGSRadiationStage/BGSDehydrationStage/BGSHungerStage/BGSSleepDeprivationStage)
///     so we look it up by name only.
/// </summary>
internal sealed class RuntimeSurvivalStageReader(RuntimeMemoryContext context)
{
    private readonly RuntimePdbFieldAccessor _fields = new(context);

    public SurvivalStageRecord? ReadRuntimeSurvivalStage(RuntimeEditorIdEntry entry, byte expectedFormType)
    {
        if (entry.FormType != expectedFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, expectedFormType);
        if (view == null)
        {
            return null;
        }

        var dataOff = view.Offset("data");
        if (dataOff is not { } o || o + 8 > view.Buffer.Length)
        {
            return null;
        }

        return new SurvivalStageRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            Threshold = BinaryUtils.ReadUInt32BE(view.Buffer, o),
            Modifier = BinaryUtils.ReadUInt32BE(view.Buffer, o + 4),
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
