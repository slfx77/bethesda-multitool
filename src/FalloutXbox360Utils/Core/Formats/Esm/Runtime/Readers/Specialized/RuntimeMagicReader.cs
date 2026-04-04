using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for magic/effect structs: EffectSetting (MGEF, 192B),
///     SpellItem (SPEL, 84B), EnchantmentItem (ENCH, 84B), BGSPerk (PERK, 96B).
///     Supports auto-detected layouts via <see cref="RuntimeMagicProbe" />.
///     All 4 types share the same inheritance chain; the probed shift from MGEF
///     applies uniformly to all post-TESForm fields across all types.
/// </summary>
internal sealed class RuntimeMagicReader
{
    private readonly RuntimeMemoryContext _context;

    // Uniform shift for all post-TESForm fields, probed from MGEF samples.
    private readonly int _s;

    public RuntimeMagicReader(RuntimeMemoryContext context, RuntimeLayoutProbeResult<int[]>? probeResult = null)
    {
        _context = context;
        _s = probeResult is { Margin: >= MinProbeMargin } && probeResult.Winner.Layout.Length > 1
            ? probeResult.Winner.Layout[1]
            : 0;
    }

    #region MGEF — EffectSetting (192 bytes, FormType 0x10)

    public BaseEffectRecord? ReadRuntimeBaseEffect(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != MgefFormType)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        var structSize = MgefStructSize + _s;
        if (offset + structSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[structSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, structSize);
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

        var fullName = entry.DisplayName ?? _context.ReadBSStringT(offset, MgefFullNameOffset + _s);
        var modelPath = _context.ReadBSStringT(offset, MgefModelOffset + _s);
        var icon = _context.ReadBSStringT(offset, MgefIconOffset + _s);

        // EffectSettingData (72 bytes)
        var dataBase = MgefDataOffset + _s;
        var flags = BinaryUtils.ReadUInt32BE(buffer, dataBase);
        var baseCost = BinaryUtils.ReadFloatBE(buffer, dataBase + 4);
        var associatedItem = _context.FollowPointerToFormId(buffer, dataBase + 8);
        var magicSchool = unchecked((int)BinaryUtils.ReadUInt32BE(buffer, dataBase + 12));
        var resistValue = unchecked((int)BinaryUtils.ReadUInt32BE(buffer, dataBase + 16));
        var light = _context.FollowPointerToFormId(buffer, dataBase + 24);
        var projSpeed = BinaryUtils.ReadFloatBE(buffer, dataBase + 28);
        var effectShader = _context.FollowPointerToFormId(buffer, dataBase + 32);
        var enchantEffect = _context.FollowPointerToFormId(buffer, dataBase + 36);
        var castingSound = _context.FollowPointerToFormId(buffer, dataBase + 40);
        var boltSound = _context.FollowPointerToFormId(buffer, dataBase + 44);
        var hitSound = _context.FollowPointerToFormId(buffer, dataBase + 48);
        var areaSound = _context.FollowPointerToFormId(buffer, dataBase + 52);
        var ceEnchantFactor = BinaryUtils.ReadFloatBE(buffer, dataBase + 56);
        var ceBarterFactor = BinaryUtils.ReadFloatBE(buffer, dataBase + 60);
        var archetype = BinaryUtils.ReadUInt32BE(buffer, dataBase + 64);
        var actorValue = unchecked((int)BinaryUtils.ReadUInt32BE(buffer, dataBase + 68));

        // counterEffects BSSimpleList
        var counterEffects = WalkFormIdSimpleList(buffer, MgefCounterEffectsOffset + _s);

        if (!RuntimeMemoryContext.IsNormalFloat(baseCost))
        {
            baseCost = 0f;
        }

        return new BaseEffectRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            Flags = flags,
            BaseCost = baseCost,
            AssociatedItem = associatedItem ?? 0,
            MagicSchool = magicSchool,
            ResistValue = resistValue,
            Archetype = archetype,
            ActorValue = actorValue,
            LightFormId = light,
            ProjectileSpeed = RuntimeMemoryContext.IsNormalFloat(projSpeed) ? projSpeed : null,
            EffectShaderFormId = effectShader,
            EnchantEffectFormId = enchantEffect,
            CastingSoundFormId = castingSound,
            BoltSoundFormId = boltSound,
            HitSoundFormId = hitSound,
            AreaSoundFormId = areaSound,
            CEEnchantFactor = RuntimeMemoryContext.IsNormalFloat(ceEnchantFactor) ? ceEnchantFactor : null,
            CEBarterFactor = RuntimeMemoryContext.IsNormalFloat(ceBarterFactor) ? ceBarterFactor : null,
            CounterEffectFormIds = counterEffects.Count > 0 ? counterEffects : null,
            Icon = icon,
            ModelPath = modelPath,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #endregion

    #region SPEL — SpellItem (84 bytes, FormType 0x14)

    public SpellRecord? ReadRuntimeSpell(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != SpelFormType)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        var structSize = SpelStructSize + _s;
        if (offset + structSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[structSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, structSize);
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

        var fullName = entry.DisplayName ?? _context.ReadBSStringT(offset, SpelFullNameOffset + _s);

        // SpellItemData (16 bytes)
        var spelData = SpelDataOffset + _s;
        var type = (SpellType)BinaryUtils.ReadUInt32BE(buffer, spelData);
        var cost = BinaryUtils.ReadUInt32BE(buffer, spelData + 4);
        var level = BinaryUtils.ReadUInt32BE(buffer, spelData + 8);
        var flags = buffer[spelData + 12];

        // Walk EffectItem linked list for full effect data
        var effects = WalkEffectItemListWithData(buffer);

        return new SpellRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            Type = type,
            Cost = cost,
            Level = level,
            Flags = flags,
            Effects = effects,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #endregion

    #region ENCH — EnchantmentItem (84 bytes, FormType 0x13)

    public EnchantmentRecord? ReadRuntimeEnchantment(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != EnchFormType)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        var structSize = EnchStructSize + _s;
        if (offset + structSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[structSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, structSize);
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

        var fullName = entry.DisplayName ?? _context.ReadBSStringT(offset, EnchFullNameOffset + _s);

        // EnchantmentItemData (16 bytes)
        var enchData = EnchDataOffset + _s;
        var enchantType = BinaryUtils.ReadUInt32BE(buffer, enchData);
        var chargeAmount = BinaryUtils.ReadUInt32BE(buffer, enchData + 4);
        var enchantCost = BinaryUtils.ReadUInt32BE(buffer, enchData + 8);
        var flags = buffer[enchData + 12];

        // Walk EffectItem linked list for effects
        var effects = WalkEffectItemListWithData(buffer);

        return new EnchantmentRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            EnchantType = enchantType,
            ChargeAmount = chargeAmount,
            EnchantCost = enchantCost,
            Flags = flags,
            Effects = effects,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #endregion

    #region PERK — BGSPerk (96 bytes, FormType 0x56)

    public PerkRecord? ReadRuntimePerk(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != PerkFormType)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        var structSize = PerkStructSize + _s;
        if (offset + structSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[structSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, structSize);
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

        var fullName = entry.DisplayName ?? _context.ReadBSStringT(offset, PerkFullNameOffset + _s);
        var iconPath = _context.ReadBSStringT(offset, PerkIconOffset + _s);

        // PerkData (5 bytes)
        var perkData = PerkDataOffset + _s;
        var trait = buffer[perkData];
        var minLevel = buffer[perkData + 1];
        var ranks = buffer[perkData + 2];
        var playable = buffer[perkData + 3];

        return new PerkRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            IconPath = iconPath,
            Trait = trait,
            MinLevel = minLevel,
            Ranks = ranks,
            Playable = playable,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #endregion

    #region BSSimpleList Walking (for counterEffects)

    private List<uint> WalkFormIdSimpleList(byte[] structBuffer, int listOffset)
    {
        var result = new List<uint>();

        var headVa = BinaryUtils.ReadUInt32BE(structBuffer, listOffset);
        if (headVa == 0 || !_context.IsValidPointer(headVa))
        {
            return result;
        }

        var visited = new HashSet<uint>();
        var currentVa = headVa;

        for (var i = 0; i < MaxListNodes; i++)
        {
            if (currentVa == 0 || !visited.Add(currentVa))
            {
                break;
            }

            var nodeFileOffset = _context.VaToFileOffset(currentVa);
            if (nodeFileOffset == null)
            {
                break;
            }

            var nodeBuffer = _context.ReadBytes(nodeFileOffset.Value, 8);
            if (nodeBuffer == null)
            {
                break;
            }

            var itemVa = BinaryUtils.ReadUInt32BE(nodeBuffer);
            var nextVa = BinaryUtils.ReadUInt32BE(nodeBuffer, 4);

            if (itemVa != 0)
            {
                var formId = _context.FollowPointerVaToFormId(itemVa);
                if (formId is > 0)
                {
                    result.Add(formId.Value);
                }
            }

            currentVa = nextVa;
        }

        return result;
    }

    #endregion

    #region EffectItem List Walking

    /// <summary>
    ///     Walk EffectItem list and return full EnchantmentEffect data per item.
    /// </summary>
    private List<EnchantmentEffect> WalkEffectItemListWithData(byte[] structBuffer)
    {
        var result = new List<EnchantmentEffect>();

        var headVa = BinaryUtils.ReadUInt32BE(structBuffer, EffectListOffset + _s);
        if (headVa == 0 || !_context.IsValidPointer(headVa))
        {
            return result;
        }

        var visited = new HashSet<uint>();
        var currentVa = headVa;

        for (var i = 0; i < MaxListNodes; i++)
        {
            if (currentVa == 0 || !visited.Add(currentVa))
            {
                break;
            }

            var nodeFileOffset = _context.VaToFileOffset(currentVa);
            if (nodeFileOffset == null)
            {
                break;
            }

            var nodeBuffer = _context.ReadBytes(nodeFileOffset.Value, 8);
            if (nodeBuffer == null)
            {
                break;
            }

            var itemVa = BinaryUtils.ReadUInt32BE(nodeBuffer);
            var nextVa = BinaryUtils.ReadUInt32BE(nodeBuffer, 4);

            if (itemVa != 0)
            {
                var itemOffset = _context.VaToFileOffset(itemVa);
                if (itemOffset != null)
                {
                    // EffectItem: pSetting(4) + fMagnitude(4) + iArea(4) + iDuration(4) + iType(4) + iActorValue(4)
                    var eiBuf = _context.ReadBytes(itemOffset.Value, EffectItemSize);
                    if (eiBuf != null)
                    {
                        var settingFormId = _context.FollowPointerVaToFormId(
                            BinaryUtils.ReadUInt32BE(eiBuf));
                        var magnitude = BinaryUtils.ReadFloatBE(eiBuf, 4);
                        var area = BinaryUtils.ReadUInt32BE(eiBuf, 8);
                        var duration = BinaryUtils.ReadUInt32BE(eiBuf, 12);
                        var type = BinaryUtils.ReadUInt32BE(eiBuf, 16);
                        var actorValue = unchecked((int)BinaryUtils.ReadUInt32BE(eiBuf, 20));

                        if (settingFormId is > 0)
                        {
                            result.Add(new EnchantmentEffect
                            {
                                EffectFormId = settingFormId.Value,
                                Magnitude = RuntimeMemoryContext.IsNormalFloat(magnitude) ? magnitude : 0f,
                                Area = area,
                                Duration = duration,
                                Type = type,
                                ActorValue = actorValue
                            });
                        }
                    }
                }
            }

            currentVa = nextVa;
        }

        return result;
    }

    #endregion

    #region Constants

    // Shared TESForm
    private const int FormIdOffset = 12;

    // MGEF — EffectSetting (192 bytes)
    private const byte MgefFormType = 0x10;
    private const int MgefStructSize = 192;
    private const int MgefModelOffset = 44;
    private const int MgefFullNameOffset = 76;
    private const int MgefIconOffset = 88;
    private const int MgefDataOffset = 104; // EffectSettingData, 72 bytes
    private const int MgefCounterEffectsOffset = 176;

    // SPEL — SpellItem (84 bytes)
    private const byte SpelFormType = 0x14;
    private const int SpelStructSize = 84;
    private const int SpelFullNameOffset = 44;
    private const int EffectListOffset = 56; // BSSimpleList<EffectItem*> — shared by SPEL & ENCH
    private const int SpelDataOffset = 68; // SpellItemData, 16 bytes

    // ENCH — EnchantmentItem (84 bytes)
    private const byte EnchFormType = 0x13;
    private const int EnchStructSize = 84;
    private const int EnchFullNameOffset = 44;
    private const int EnchDataOffset = 68; // EnchantmentItemData, 16 bytes

    // PERK — BGSPerk (96 bytes)
    private const byte PerkFormType = 0x56;
    private const int PerkStructSize = 96;
    private const int PerkFullNameOffset = 44;
    private const int PerkIconOffset = 64;
    private const int PerkDataOffset = 72; // PerkData, 5 bytes

    // EffectItem struct size (pSetting + magnitude + area + duration + type + actorValue)
    private const int EffectItemSize = 24;
    private const int MaxListNodes = 256;
    private const int MinProbeMargin = 3;

    #endregion
}
