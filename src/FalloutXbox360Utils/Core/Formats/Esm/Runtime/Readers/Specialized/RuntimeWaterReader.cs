using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESWaterForm (WATR, 420 bytes, FormType 0x4E).
///     Reads the fields the ESM model exposes: FullName, Damage (sAttackDamage),
///     Opacity (cAlpha), SoundFormId (pWaterSound pointer), and the 196-byte
///     WaterShaderData block (mirrors the ESM DNAM schema).
/// </summary>
internal sealed class RuntimeWaterReader(RuntimeMemoryContext context)
{
    public WaterRecord? ReadRuntimeWater(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != WatrFormType)
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
        var damage = BinaryUtils.ReadUInt16BE(buffer, AttackDamageOffset);
        var opacity = buffer[AlphaOffset];
        var soundFormId = context.FollowPointerToFormId(buffer, WaterSoundPointerOffset);

        var shaderBytes = new byte[ShaderDataSize];
        Array.Copy(buffer, ShaderDataOffset, shaderBytes, 0, ShaderDataSize);
        var visualProperties = SubrecordSchemaView.TryRead("DNAM", "WATR", shaderBytes, bigEndian: true)?.Raw;

        return new WaterRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            Damage = damage,
            Opacity = opacity,
            SoundFormId = soundFormId,
            VisualProperties = visualProperties,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte WatrFormType = 0x4E;
    private const int StructSize = 420;
    private const int FormIdOffset = 12;
    private const int FullNameOffset = 44;
    private const int AttackDamageOffset = 56;
    private const int AlphaOffset = 128;
    private const int WaterSoundPointerOffset = 140;
    private const int ShaderDataOffset = 148;
    private const int ShaderDataSize = 196;

    #endregion
}
