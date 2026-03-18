using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Typed runtime reader for magic/effect structs: EffectSetting (MGEF, 192B),
///     SpellItem (SPEL, 84B), EnchantmentItem (ENCH, 84B), BGSPerk (PERK, 96B).
/// </summary>
internal sealed class RuntimeMagicReader
{
    private readonly RuntimeMemoryContext _context;

    public RuntimeMagicReader(RuntimeMemoryContext context)
    {
        _context = context;
    }

    #region MGEF — EffectSetting (192 bytes, FormType 0x10)

    public BaseEffectRecord? ReadRuntimeBaseEffect(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != MgefFormType)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + MgefStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[MgefStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, MgefStructSize);
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

        var fullName = entry.DisplayName ?? _context.ReadBSStringT(offset, MgefFullNameOffset);
        var modelPath = _context.ReadBSStringT(offset, MgefModelOffset);
        var icon = _context.ReadBSStringT(offset, MgefIconOffset);

        // EffectSettingData (72 bytes at +104)
        var dataBase = MgefDataOffset;
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

        // counterEffects BSSimpleList at +176
        var counterEffects = WalkFormIdSimpleList(buffer, offset, MgefCounterEffectsOffset);

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
        if (offset + SpelStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[SpelStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, SpelStructSize);
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

        var fullName = entry.DisplayName ?? _context.ReadBSStringT(offset, SpelFullNameOffset);

        // SpellItemData (16 bytes at +68)
        var type = (SpellType)BinaryUtils.ReadUInt32BE(buffer, SpelDataOffset);
        var cost = BinaryUtils.ReadUInt32BE(buffer, SpelDataOffset + 4);
        var level = BinaryUtils.ReadUInt32BE(buffer, SpelDataOffset + 8);
        var flags = buffer[SpelDataOffset + 12];

        // Walk EffectItem linked list for effect FormIDs
        var effectFormIds = WalkEffectItemList(buffer, offset);

        return new SpellRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            Type = type,
            Cost = cost,
            Level = level,
            Flags = flags,
            EffectFormIds = effectFormIds,
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
        if (offset + EnchStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[EnchStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, EnchStructSize);
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

        var fullName = entry.DisplayName ?? _context.ReadBSStringT(offset, EnchFullNameOffset);

        // EnchantmentItemData (16 bytes at +68)
        var enchantType = BinaryUtils.ReadUInt32BE(buffer, EnchDataOffset);
        var chargeAmount = BinaryUtils.ReadUInt32BE(buffer, EnchDataOffset + 4);
        var enchantCost = BinaryUtils.ReadUInt32BE(buffer, EnchDataOffset + 8);
        var flags = buffer[EnchDataOffset + 12];

        // Walk EffectItem linked list for effects
        var effects = WalkEffectItemListWithData(buffer, offset);

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
        if (offset + PerkStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[PerkStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, PerkStructSize);
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

        var fullName = entry.DisplayName ?? _context.ReadBSStringT(offset, PerkFullNameOffset);
        var iconPath = _context.ReadBSStringT(offset, PerkIconOffset);

        // PerkData (5 bytes at +72)
        var trait = buffer[PerkDataOffset];
        var minLevel = buffer[PerkDataOffset + 1];
        var ranks = buffer[PerkDataOffset + 2];
        var playable = buffer[PerkDataOffset + 3];

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

    private List<uint> WalkFormIdSimpleList(byte[] structBuffer, long structFileOffset, int listOffset)
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
    ///     Walk BSSimpleList&lt;EffectItem*&gt; at +56/+60 and collect base effect FormIDs.
    ///     EffectItem layout: pSetting(4) + fMagnitude(4) + iArea(4) + iDuration(4) + iType(4) + iActorValue(4) = 24 bytes.
    /// </summary>
    private List<uint> WalkEffectItemList(byte[] structBuffer, long structFileOffset)
    {
        var result = new List<uint>();

        // BSSimpleList head pointer at +56
        var headVa = BinaryUtils.ReadUInt32BE(structBuffer, EffectListOffset);
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

            // BSSimpleList node: pItem(4) + pNext(4) = 8 bytes
            var nodeBuffer = _context.ReadBytes(nodeFileOffset.Value, 8);
            if (nodeBuffer == null)
            {
                break;
            }

            var itemVa = BinaryUtils.ReadUInt32BE(nodeBuffer);
            var nextVa = BinaryUtils.ReadUInt32BE(nodeBuffer, 4);

            if (itemVa != 0)
            {
                // EffectItem: pSetting (EffectSetting*) at +0
                var itemOffset = _context.VaToFileOffset(itemVa);
                if (itemOffset != null)
                {
                    var effectItemBuffer = _context.ReadBytes(itemOffset.Value, 4);
                    if (effectItemBuffer != null)
                    {
                        var settingFormId = _context.FollowPointerVaToFormId(
                            BinaryUtils.ReadUInt32BE(effectItemBuffer));
                        if (settingFormId is > 0)
                        {
                            result.Add(settingFormId.Value);
                        }
                    }
                }
            }

            currentVa = nextVa;
        }

        return result;
    }

    /// <summary>
    ///     Walk EffectItem list and return full EnchantmentEffect data per item.
    /// </summary>
    private List<EnchantmentEffect> WalkEffectItemListWithData(byte[] structBuffer, long structFileOffset)
    {
        var result = new List<EnchantmentEffect>();

        var headVa = BinaryUtils.ReadUInt32BE(structBuffer, EffectListOffset);
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

    #endregion
}
