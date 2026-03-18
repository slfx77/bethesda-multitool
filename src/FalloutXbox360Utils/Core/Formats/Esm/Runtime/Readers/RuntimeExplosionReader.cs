using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Typed runtime reader for BGSExplosion (EXPL, 184 bytes, FormType 0x51).
///     Reads full name, model, and BGSExplosionData fields.
/// </summary>
internal sealed class RuntimeExplosionReader
{
    private readonly RuntimeMemoryContext _context;

    public RuntimeExplosionReader(RuntimeMemoryContext context)
    {
        _context = context;
    }

    public ExplosionRecord? ReadRuntimeExplosion(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != ExplFormType)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + StructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[StructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, StructSize);
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

        var fullName = entry.DisplayName ?? _context.ReadBSStringT(offset, FullNameOffset);
        var modelPath = _context.ReadBSStringT(offset, ModelOffset);

        // BGSExplosionData (52 bytes at +132)
        var force = BinaryUtils.ReadFloatBE(buffer, ExpDataOffset);
        var damage = BinaryUtils.ReadFloatBE(buffer, ExpDataOffset + 4);
        var radius = BinaryUtils.ReadFloatBE(buffer, ExpDataOffset + 8);
        var light = _context.FollowPointerToFormId(buffer, ExpDataOffset + 12) ?? 0u;
        var sound1 = _context.FollowPointerToFormId(buffer, ExpDataOffset + 16) ?? 0u;
        var flags = BinaryUtils.ReadUInt32BE(buffer, ExpDataOffset + 20);
        var isRadius = BinaryUtils.ReadFloatBE(buffer, ExpDataOffset + 24);
        var impactDataSet = _context.FollowPointerToFormId(buffer, ExpDataOffset + 28) ?? 0u;
        var sound2 = _context.FollowPointerToFormId(buffer, ExpDataOffset + 32) ?? 0u;

        // Enchantment pointer (TESEnchantableForm)
        var enchantment = _context.FollowPointerToFormId(buffer, EnchantmentPtrOffset) ?? 0u;

        // Validate floats
        if (!RuntimeMemoryContext.IsNormalFloat(force)) force = 0f;
        if (!RuntimeMemoryContext.IsNormalFloat(damage)) damage = 0f;
        if (!RuntimeMemoryContext.IsNormalFloat(radius)) radius = 0f;
        if (!RuntimeMemoryContext.IsNormalFloat(isRadius)) isRadius = 0f;

        return new ExplosionRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            ModelPath = modelPath,
            Force = force,
            Damage = damage,
            Radius = radius,
            Light = light,
            Sound1 = sound1,
            Flags = flags,
            ISRadius = isRadius,
            ImpactDataSet = impactDataSet,
            Sound2 = sound2,
            Enchantment = enchantment,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte ExplFormType = 0x51;
    private const int StructSize = 184;
    private const int FormIdOffset = 12;
    private const int FullNameOffset = 68;        // TESFullName.cFullName BSStringT
    private const int ModelOffset = 80;           // TESModel.cModel BSStringT
    private const int EnchantmentPtrOffset = 104; // TESEnchantableForm.pFormEnchanting pointer
    private const int ExpDataOffset = 132;        // BGSExplosionData (52 bytes)

    #endregion
}
