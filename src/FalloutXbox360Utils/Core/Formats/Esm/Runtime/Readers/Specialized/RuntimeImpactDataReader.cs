using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSImpactData (IPCT, 136 bytes, FormType 0x5E).
///     Reads model path + decal texture / sound FormID pointers.
/// </summary>
internal sealed class RuntimeImpactDataReader(RuntimeMemoryContext context)
{
    public ImpactDataRecord? ReadRuntimeImpactData(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != IpctFormType)
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

        var modelPath = context.ReadBsStringT(offset, ModelOffset);
        var decalTextureSet = context.FollowPointerToFormId(buffer, DecalTextureSetPointerOffset) ?? 0;
        var sound1 = context.FollowPointerToFormId(buffer, Sound1PointerOffset) ?? 0;
        var sound2 = context.FollowPointerToFormId(buffer, Sound2PointerOffset) ?? 0;

        return new ImpactDataRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            ModelPath = modelPath,
            DecalTextureSetFormId = decalTextureSet,
            Sound1FormId = sound1,
            Sound2FormId = sound2,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte IpctFormType = 0x5E;
    private const int StructSize = 136;
    private const int FormIdOffset = 12;
    private const int ModelOffset = 44;
    private const int DecalTextureSetPointerOffset = 88;
    private const int Sound1PointerOffset = 92;
    private const int Sound2PointerOffset = 96;

    #endregion
}
