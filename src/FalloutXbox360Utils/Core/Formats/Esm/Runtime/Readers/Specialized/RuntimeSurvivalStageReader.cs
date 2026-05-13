using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Shared typed runtime reader for the 4 hardcore-mode survival stage
///     records — RADS (0x5A, radiation), DEHY (0x76, dehydration),
///     HUNG (0x77, hunger), SLPD (0x78, sleep deprivation). All four share
///     the same 48-byte PDB layout with a single 8-byte DATA tuple at +40
///     (threshold uint32, modifier uint32).
/// </summary>
internal sealed class RuntimeSurvivalStageReader(RuntimeMemoryContext context)
{
    public SurvivalStageRecord? ReadRuntimeSurvivalStage(RuntimeEditorIdEntry entry, byte expectedFormType)
    {
        if (entry.TesFormOffset == null || entry.FormType != expectedFormType)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + StructSize > context.FileSize)
        {
            return null;
        }

        var buffer = new byte[StructSize];
        try
        {
            context.Accessor.ReadArray(offset, buffer, 0, StructSize);
        }
        catch
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, FormIdOffset);
        if (formId != entry.FormId || formId == 0)
        {
            return null;
        }

        var threshold = BinaryUtils.ReadUInt32BE(buffer, DataOffset);
        var modifier = BinaryUtils.ReadUInt32BE(buffer, DataOffset + 4);

        return new SurvivalStageRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            Threshold = threshold,
            Modifier = modifier,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const int StructSize = 48;
    private const int FormIdOffset = 12;
    private const int DataOffset = 40;

    #endregion
}
