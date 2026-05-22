using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSExplosion (EXPL, FormType 0x51).
///     Reads full name, model, enchantment pointer, and BGSExplosionData fields
///     via the PDB layout. The Data struct (BGSExplosionData, 52 bytes) is opaque;
///     we resolve its offset by name and parse the inner layout manually.
/// </summary>
internal sealed class RuntimeExplosionReader(RuntimeMemoryContext context)
{
    private const byte ExplFormType = 0x51;

    private readonly RuntimePdbFieldAccessor _fields = new(context);
    private readonly RuntimeMemoryContext _context = context;

    public ExplosionRecord? ReadRuntimeExplosion(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != ExplFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry);
        if (view == null)
        {
            return null;
        }

        var dataOff = view.Offset("Data", "BGSExplosion");
        if (dataOff is not { } o || o + 36 > view.Buffer.Length)
        {
            return null;
        }

        // BGSExplosionData prefix layout: Force(float), Damage(float), Radius(float),
        // Light(ptr), Sound1(ptr), Flags(uint32), IsRadius(float), ImpactDataSet(ptr),
        // Sound2(ptr).
        var force = BinaryUtils.ReadFloatBE(view.Buffer, o);
        var damage = BinaryUtils.ReadFloatBE(view.Buffer, o + 4);
        var radius = BinaryUtils.ReadFloatBE(view.Buffer, o + 8);
        var light = _context.FollowPointerToFormId(view.Buffer, o + 12) ?? 0u;
        var sound1 = _context.FollowPointerToFormId(view.Buffer, o + 16) ?? 0u;
        var flags = BinaryUtils.ReadUInt32BE(view.Buffer, o + 20);
        var isRadius = BinaryUtils.ReadFloatBE(view.Buffer, o + 24);
        var impactDataSet = _context.FollowPointerToFormId(view.Buffer, o + 28) ?? 0u;
        var sound2 = _context.FollowPointerToFormId(view.Buffer, o + 32) ?? 0u;

        // Validate floats
        if (!RuntimeMemoryContext.IsNormalFloat(force)) force = 0f;
        if (!RuntimeMemoryContext.IsNormalFloat(damage)) damage = 0f;
        if (!RuntimeMemoryContext.IsNormalFloat(radius)) radius = 0f;
        if (!RuntimeMemoryContext.IsNormalFloat(isRadius)) isRadius = 0f;

        return new ExplosionRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName ?? view.BsString("cFullName", "TESFullName"),
            ModelPath = view.BsString("cModel", "TESModel"),
            Force = force,
            Damage = damage,
            Radius = radius,
            Light = light,
            Sound1 = sound1,
            Flags = flags,
            IsRadius = isRadius,
            ImpactDataSet = impactDataSet,
            Sound2 = sound2,
            Enchantment = view.FormIdPointer("pFormEnchanting", "TESEnchantableForm") ?? 0u,
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
