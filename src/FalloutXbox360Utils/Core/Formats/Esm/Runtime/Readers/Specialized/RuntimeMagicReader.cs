using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for magic/effect structs: EffectSetting (MGEF, 192B),
///     SpellItem (SPEL, 84B), EnchantmentItem (ENCH, 84B), BGSPerk (PERK, 96B).
///     All 4 types share the same inheritance chain. The per-build shift previously
///     supplied by <c>RuntimeMagicProbe</c> was always zero across every observed dump
///     (32/32 samples in the Phase 1B.5 probe sweep), so it has been removed.
///     Phase 1B's closeout migrates the field reads to <see cref="PdbStructView" />:
///     offsets come straight from <c>pdb_layouts.json</c> instead of being hand-pinned
///     in this file.
/// </summary>
internal sealed class RuntimeMagicReader
{
    private readonly RuntimeMemoryContext _context;
    private readonly RuntimePdbFieldAccessor _fields;

    public RuntimeMagicReader(RuntimeMemoryContext context)
    {
        _context = context;
        _fields = new RuntimePdbFieldAccessor(context);
    }

    #region MGEF — EffectSetting (192 bytes, FormType 0x10)

    public BaseEffectRecord? ReadRuntimeBaseEffect(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != MgefFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry);
        if (view == null)
        {
            return null;
        }

        var fullName = entry.DisplayName ?? view.BsString("cFullName", "TESFullName");
        var modelPath = view.BsString("cModel", "TESModel");
        var icon = view.BsString("TextureName", "TESTexture");

        // EffectSettingData (72-byte substruct at "data") — no per-field PDB entries,
        // walk the bytes from the substruct base.
        if (view.Offset("data", "EffectSetting") is not { } dataBase)
        {
            return null;
        }

        var buffer = view.Buffer;
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

        // counterEffects BSSimpleList — head lives at the "counterEffects" PDB field.
        var counterEffects = view.Offset("counterEffects", "EffectSetting") is { } ceOff
            ? WalkFormIdSimpleList(buffer, ceOff)
            : [];

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
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }

    #endregion

    #region SPEL — SpellItem (84 bytes, FormType 0x14)

    public SpellRecord? ReadRuntimeSpell(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != SpelFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry);
        if (view == null)
        {
            return null;
        }

        var fullName = entry.DisplayName ?? view.BsString("cFullName", "TESFullName");

        // SpellItemData (16-byte substruct)
        if (view.Offset("data", "SpellItem") is not { } spelData)
        {
            return null;
        }

        var buffer = view.Buffer;
        var type = (SpellType)BinaryUtils.ReadUInt32BE(buffer, spelData);
        var cost = BinaryUtils.ReadUInt32BE(buffer, spelData + 4);
        var level = BinaryUtils.ReadUInt32BE(buffer, spelData + 8);
        var flags = buffer[spelData + 12];

        // Walk EffectItem linked list (BSSimpleList head at "m_item")
        var effectListOffset = view.Offset("m_item", "BSSimpleList<EffectItem *>") ?? -1;
        var effects = effectListOffset >= 0
            ? RuntimeEffectItemListReader.Read(_context, buffer, effectListOffset, MaxListNodes)
            : [];

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
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }

    #endregion

    #region ENCH — EnchantmentItem (84 bytes, FormType 0x13)

    public EnchantmentRecord? ReadRuntimeEnchantment(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != EnchFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry);
        if (view == null)
        {
            return null;
        }

        var fullName = entry.DisplayName ?? view.BsString("cFullName", "TESFullName");

        // EnchantmentItemData (16-byte substruct)
        if (view.Offset("data", "EnchantmentItem") is not { } enchData)
        {
            return null;
        }

        var buffer = view.Buffer;
        var enchantType = BinaryUtils.ReadUInt32BE(buffer, enchData);
        var chargeAmount = BinaryUtils.ReadUInt32BE(buffer, enchData + 4);
        var enchantCost = BinaryUtils.ReadUInt32BE(buffer, enchData + 8);
        var flags = buffer[enchData + 12];

        // Walk EffectItem linked list (BSSimpleList head at "m_item")
        var effectListOffset = view.Offset("m_item", "BSSimpleList<EffectItem *>") ?? -1;
        var effects = effectListOffset >= 0
            ? RuntimeEffectItemListReader.Read(_context, buffer, effectListOffset, MaxListNodes)
            : [];

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
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }

    #endregion

    #region BSSimpleList Walking (for counterEffects)

    private List<uint> WalkFormIdSimpleList(byte[] structBuffer, int listOffset)
    {
        var result = new List<uint>();

        foreach (var itemVa in _context.WalkInlineBSSimpleListItemPointers(
                     structBuffer,
                     listOffset,
                     MaxListNodes))
        {
            var formId = _context.FollowPointerVaToFormId(itemVa);
            if (formId is > 0)
            {
                result.Add(formId.Value);
            }
        }

        return result;
    }

    #endregion

    #region PERK — BGSPerk (96 bytes, FormType 0x56)

    public PerkRecord? ReadRuntimePerk(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != PerkFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry);
        if (view == null)
        {
            return null;
        }

        var fullName = entry.DisplayName ?? view.BsString("cFullName", "TESFullName");
        var iconPath = view.BsString("TextureName", "TESTexture");

        // PerkData (5-byte substruct)
        if (view.Offset("Data", "BGSPerk") is not { } perkData)
        {
            return null;
        }

        var buffer = view.Buffer;
        var trait = buffer[perkData];
        var minLevel = buffer[perkData + 1];
        var ranks = buffer[perkData + 2];
        var playable = buffer[perkData + 3];

        // Walk PerkEntries BSSimpleList
        var entriesListOffset = view.Offset("PerkEntries", "BGSPerk") ?? -1;
        var entries = entriesListOffset >= 0
            ? WalkPerkEntryList(buffer, entriesListOffset)
            : [];

        // Walk PerkConditions TESCondition linked list
        var conditionsListOffset = view.Offset("PerkConditions", "BGSPerk") ?? -1;
        var conditions = conditionsListOffset >= 0
            ? WalkPerkConditions(buffer, conditionsListOffset)
            : [];

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
            Entries = entries,
            Conditions = conditions,
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Walk BSSimpleList of BGSPerkEntry* pointers.
    ///     BGSPerkEntry runtime layout (speculative from GECK analysis):
    ///     +0: vtable (4B), +4: rank (uint8), +5: priority (uint8), +6: pad (2B),
    ///     +8: type-specific data (pointer for Ability/QuestStage, entryPoint ID for EntryPoint).
    ///     Type is inferred from the pointer at +8: SPEL=Ability, QUST=QuestStage, else EntryPoint.
    /// </summary>
    private List<PerkEntry> WalkPerkEntryList(byte[] structBuffer, int listOffset)
    {
        var result = new List<PerkEntry>();

        var firstItemVa = BinaryUtils.ReadUInt32BE(structBuffer, listOffset);
        var nextVa = BinaryUtils.ReadUInt32BE(structBuffer, listOffset + 4);
        var visited = new HashSet<uint>();

        ReadPerkEntry(firstItemVa, result);

        for (var i = 1; nextVa != 0 && i < MaxListNodes && visited.Add(nextVa); i++)
        {
            var nodeFileOffset = _context.VaToFileOffset(nextVa);
            if (nodeFileOffset == null)
            {
                break;
            }

            // BSSimpleList node: m_item (BGSPerkEntry*, 4B) + m_pkNext (4B)
            var nodeBuffer = _context.ReadBytes(nodeFileOffset.Value, 8);
            if (nodeBuffer == null)
            {
                break;
            }

            var entryVa = BinaryUtils.ReadUInt32BE(nodeBuffer);
            nextVa = BinaryUtils.ReadUInt32BE(nodeBuffer, 4);

            ReadPerkEntry(entryVa, result);
        }

        return result;
    }

    /// <summary>
    ///     Read a single BGSPerkEntry from memory. Infers type from pointer at +8:
    ///     if it resolves to SPEL (0x14), type=Ability; otherwise type=EntryPoint (most common).
    /// </summary>
    private void ReadPerkEntry(uint entryVa, List<PerkEntry> result)
    {
        var entry = ReadPerkEntry(entryVa);
        if (entry != null)
        {
            result.Add(entry);
        }
    }

    private PerkEntry? ReadPerkEntry(uint entryVa)
    {
        var entryFileOffset = _context.VaToFileOffset(entryVa);
        if (entryFileOffset == null)
        {
            return null;
        }

        // Read enough for: vtable(4) + rank(1) + priority(1) + pad(2) + data ptr(4) = 12 bytes
        var entryBuffer = _context.ReadBytes(entryFileOffset.Value, 12);
        if (entryBuffer == null)
        {
            return null;
        }

        var rank = entryBuffer[4];
        var priority = entryBuffer[5];

        // Try to determine type by following the pointer at +8
        var dataVa = BinaryUtils.ReadUInt32BE(entryBuffer, 8);
        byte type = 2; // Default: EntryPoint (most common)
        uint? abilityFormId = null;

        if (dataVa != 0 && _context.IsValidPointer(dataVa))
        {
            // Check if pointer resolves to a SpellItem (SPEL, FormType 0x14) → Ability entry
            var formId = _context.FollowPointerVaToFormId(dataVa, 0x14);
            if (formId is > 0)
            {
                type = 1; // Ability
                abilityFormId = formId;
            }
            else
            {
                // Check if pointer resolves to a Quest (QUST, FormType 0x40) → QuestStage entry
                formId = _context.FollowPointerVaToFormId(dataVa, 0x40);
                if (formId is > 0)
                {
                    type = 0; // QuestStage
                }
            }
        }

        return new PerkEntry
        {
            Type = type,
            Rank = rank,
            Priority = priority,
            AbilityFormId = abilityFormId
        };
    }

    /// <summary>
    ///     Walk TESCondition linked list (BSSimpleList of TESConditionItem*).
    ///     Each TESConditionItem is 28 bytes: Type(1) + pad(3) + ComparisonValue(4) +
    ///     FunctionIndex(2) + pad(2) + Param1(4) + Param2(4) + RunOn(4) + Reference(4).
    ///     Extracts skill/stat requirements (GetActorValue) and perk prerequisites (HasPerk).
    /// </summary>
    private List<PerkCondition> WalkPerkConditions(byte[] structBuffer, int listOffset)
    {
        var results = new List<PerkCondition>();

        // BSSimpleList inline: first item pointer + next node pointer (8 bytes)
        var firstItemVa = BinaryUtils.ReadUInt32BE(structBuffer, listOffset);
        var nextVa = BinaryUtils.ReadUInt32BE(structBuffer, listOffset + 4);

        // Read first inline item
        ReadPerkConditionItem(firstItemVa, results);

        // Walk linked list
        var visited = new HashSet<uint>();
        while (nextVa != 0 && results.Count < MaxListNodes && visited.Add(nextVa))
        {
            var nodeBuf = _context.ReadBytesAtVa(nextVa, 8);
            if (nodeBuf == null)
            {
                break;
            }

            ReadPerkConditionItem(BinaryUtils.ReadUInt32BE(nodeBuf), results);
            nextVa = BinaryUtils.ReadUInt32BE(nodeBuf, 4);
        }

        return results;
    }

    private void ReadPerkConditionItem(uint conditionItemVa, List<PerkCondition> results)
    {
        if (!_context.IsValidPointer(conditionItemVa))
        {
            return;
        }

        var buffer = _context.ReadBytesAtVa(conditionItemVa, 28);
        if (buffer == null)
        {
            return;
        }

        var type = buffer[0];
        var comparisonOperator = (byte)((type >> 5) & 0x7);
        if (comparisonOperator > 5)
        {
            return;
        }

        var functionIndex = BinaryUtils.ReadUInt16BE(buffer, 8);
        var comparisonValue = BinaryUtils.ReadFloatBE(buffer, 4);
        if (!RuntimeMemoryContext.IsNormalFloat(comparisonValue))
        {
            comparisonValue = 0;
        }

        var rawParam1 = BinaryUtils.ReadUInt32BE(buffer, 12);
        var rawParam2 = BinaryUtils.ReadUInt32BE(buffer, 16);

        // Skip empty conditions
        if (type == 0 && functionIndex == 0 && rawParam1 == 0 && MathF.Abs(comparisonValue) < 0.0001f)
        {
            return;
        }

        var functionName = PerkConditionParameterResolver.ResolveScriptFunctionName(functionIndex);

        if (functionIndex == 0x1C1 && rawParam1 != 0 && _context.IsValidPointer(rawParam1))
        {
            var perkFormId = _context.FollowPointerVaToFormId(rawParam1);
            if (perkFormId is > 0)
            {
                rawParam1 = perkFormId.Value;
            }
        }

        var param1 = PerkConditionParameterResolver.ResolveParameter(functionIndex, 0, rawParam1);
        var param2 = PerkConditionParameterResolver.ResolveParameter(functionIndex, 1, rawParam2);

        results.Add(new PerkCondition
        {
            FunctionIndex = functionIndex,
            FunctionName = functionName,
            Parameter1 = rawParam1,
            Parameter1Display = param1.Display,
            Parameter1FormId = param1.FormId,
            Parameter2 = rawParam2,
            Parameter2Display = param2.Display,
            Parameter2FormId = param2.FormId,
            ComparisonOperator = comparisonOperator,
            ComparisonValue = comparisonValue
        });
    }

    #endregion

    #region FormType + List Walker Constants

    // FormType constants — kept here (not from PDB) because the reader's entry-point
    // type checks reference them; offsets all come from PdbStructLayouts via
    // PdbStructView at call time.
    private const byte MgefFormType = 0x10; // EffectSetting (192B)
    private const byte SpelFormType = 0x14; // SpellItem (84B)
    private const byte EnchFormType = 0x13; // EnchantmentItem (84B)
    private const byte PerkFormType = 0x56; // BGSPerk (96B)

    // Cap on linked-list traversals for runaway-list protection.
    private const int MaxListNodes = 256;

    #endregion
}
