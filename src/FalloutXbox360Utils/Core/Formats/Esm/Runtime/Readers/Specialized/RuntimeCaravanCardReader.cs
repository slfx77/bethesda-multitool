using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESCaravanCard (CCRD, 204 bytes, FormType 0x73).
///     Reads FullName, model path, value, script + sound FormIDs. Skips the
///     CARAVANCARDDATA suit/rank tuple (variable per-build encoding).
/// </summary>
internal sealed class RuntimeCaravanCardReader(RuntimeMemoryContext context)
{
    public CaravanCardRecord? ReadRuntimeCaravanCard(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != CcrdFormType)
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
        var modelPath = context.ReadBsStringT(offset, ModelOffset);
        var value = BinaryUtils.ReadUInt32BE(buffer, ValueOffset);
        var scriptFormId = context.FollowPointerToFormId(buffer, ScriptPointerOffset) ?? 0;
        var pickupSound = context.FollowPointerToFormId(buffer, PickupSoundPointerOffset) ?? 0;
        var putdownSound = context.FollowPointerToFormId(buffer, PutdownSoundPointerOffset) ?? 0;

        return new CaravanCardRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            ModelPath = modelPath,
            Value = value,
            ScriptFormId = scriptFormId,
            PickupSoundFormId = pickupSound,
            PutdownSoundFormId = putdownSound,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte CcrdFormType = 0x73;
    private const int StructSize = 204;
    private const int FormIdOffset = 12;
    private const int FullNameOffset = 68;
    private const int ModelOffset = 80;
    private const int ValueOffset = 140;
    private const int ScriptPointerOffset = 148;
    private const int PickupSoundPointerOffset = 160;
    private const int PutdownSoundPointerOffset = 164;

    #endregion
}
