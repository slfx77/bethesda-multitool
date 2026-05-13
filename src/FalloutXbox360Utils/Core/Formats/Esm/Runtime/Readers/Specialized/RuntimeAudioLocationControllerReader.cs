using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for MediaLocationController (ALOC, 200 bytes,
///     FormType 0x70). Reads FullName + the 4 timer uints; skips the boolean
///     runtime-state fields (bIsActive/bInCombat/etc) since they are
///     mutated at runtime and not parity-relevant.
/// </summary>
internal sealed class RuntimeAudioLocationControllerReader(RuntimeMemoryContext context)
{
    public AudioLocationControllerRecord? ReadRuntimeAudioLocationController(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != AlocFormType)
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

        var fullName = context.ReadBsStringT(offset, FullNameOffset);
        var locationDelay = BinaryUtils.ReadUInt32BE(buffer, LocationDelayOffset);
        var layerTime = BinaryUtils.ReadUInt32BE(buffer, LayerTimeOffset);
        var loopTime = BinaryUtils.ReadUInt32BE(buffer, LoopTimeOffset);
        var mediaStartTime = BinaryUtils.ReadUInt32BE(buffer, MediaStartTimeOffset);

        return new AudioLocationControllerRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            LocationDelay = locationDelay,
            LayerTime = layerTime,
            LoopTime = loopTime,
            MediaStartTime = mediaStartTime,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte AlocFormType = 0x70;
    private const int StructSize = 200;
    private const int FormIdOffset = 12;
    private const int FullNameOffset = 44;
    private const int LocationDelayOffset = 52;
    private const int LayerTimeOffset = 56;
    private const int LoopTimeOffset = 60;
    private const int MediaStartTimeOffset = 64;

    #endregion
}
