using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Walks a runtime <c>TESCondition</c>'s embedded <c>BSSimpleList&lt;TESConditionItem*&gt;</c>
///     and converts each item into an on-disk-shape <see cref="DialogueCondition" />. Shared
///     by record readers whose ESM record carries a CTDA list (IDLE, PERK, QUST, INFO, IDLE, PACK, …).
///
///     <para>Runtime layout (Fallout_Release_Beta PDB, used by xex*.dmp):</para>
///     <list type="bullet">
///         <item><c>TESCondition</c> (8 bytes): a single field <c>listConditions</c> at +0 of type
///         <c>BSSimpleList&lt;TESConditionItem*&gt;</c>. The TESCondition itself is embedded
///         (not a pointer) inside the owning record, so the 8 bytes of the BSSimpleList head
///         live at <c>ownerRecord + conditionsFieldOffset</c>.</item>
///         <item><c>BSSimpleList</c> (8 bytes):
///             <c>m_item</c> @ +0 (TESConditionItem*),
///             <c>m_pkNext</c> @ +4 (pointer to the next 8-byte BSSimpleList node, or null).</item>
///         <item><c>TESConditionItem</c> (28 bytes): a single <c>Data</c> field at +0 of type
///         <c>CONDITION_ITEM_DATA</c> (28 bytes).</item>
///         <item><c>CONDITION_ITEM_DATA</c> (28 bytes):
///             <c>iFlags</c> @ +0 (uint8 — CTDA Type byte: operator + OR + swap + UseGlobal),
///             <c>fValue</c> @ +4 OR <c>pGlobal</c> @ +4 (union, 4 bytes — comparison value or
///                 pointer to TESGlobal when UseGlobal bit 0x04 is set),
///             <c>FunctionData</c> @ +8 (FUNCTION_DATA, 12 bytes:
///                 <c>iFunction</c> @ +0 (uint16),
///                 padding(2),
///                 <c>pParam</c> @ +4 (<c>void*[2]</c>, 8 bytes)),
///             <c>eObject</c> @ +20 (uint32 enum — RunOn: 0=Subject 1=Target 2=Reference 3=CombatTarget 4=LinkedRef),
///             <c>pRunOnRef</c> @ +24 (TESObjectREFR* or null).</item>
///     </list>
///
///     <para>Runtime <c>pParam</c> entries are <i>either</i> a TESForm* (when the function's
///     parameter type per <see cref="PerkConditionParameterResolver" /> is a FormID type) <i>or</i>
///     a uint32 value cast to a pointer (for ActorValue / Stage / Int / enum parameters). We
///     read the 4 bytes verbatim, then disambiguate using <see cref="PerkConditionParameterResolver.IsFormParameter" />:
///     if it's a FormID parameter we follow the pointer to TESForm+12 (the FormID); otherwise
///     we emit the 32-bit value as-is.</para>
/// </summary>
internal static class TesConditionListWalker
{
    /// <summary>Type byte bit 0x04: comparison value is a TESGlobal* (pGlobal) instead of literal fValue.</summary>
    private const byte CtdaTypeUseGlobalBit = 0x04;

    /// <summary>Safety: stop walking if we see more nodes than this (loop-detection / corruption guard).</summary>
    private const int MaxConditionItems = 256;

    private const int BsSimpleListSize = 8;
    private const int ConditionItemDataSize = 28;
    private const int CitemFlagsOffset = 0;
    private const int CitemCompareValueOffset = 4;
    private const int CitemFunctionIndexOffset = 8;        // FunctionData @+8, iFunction @+0
    private const int CitemParam1Offset = 12;              //                pParam[0] @+4 → CITEM+12
    private const int CitemParam2Offset = 16;              //                pParam[1] @+8 → CITEM+16
    private const int CitemRunOnOffset = 20;
    private const int CitemRunOnRefOffset = 24;

    /// <summary>
    ///     Walk the BSSimpleList embedded at <paramref name="ownerBuffer" />[<paramref name="listOffset" />..+8]
    ///     and return one <see cref="DialogueCondition" /> per visited TESConditionItem. Returns
    ///     an empty list when the list is empty, the pointers are invalid, or the walk hits
    ///     <see cref="MaxConditionItems" />.
    /// </summary>
    public static List<DialogueCondition> Walk(
        RuntimeMemoryContext context,
        byte[] ownerBuffer,
        int listOffset)
    {
        var conditions = new List<DialogueCondition>();
        if (listOffset + BsSimpleListSize > ownerBuffer.Length)
        {
            return conditions;
        }

        // First node's m_item / m_pkNext are inline in the owner buffer (the BSSimpleList is
        // embedded, not heap-allocated). All subsequent nodes are 8-byte BSSimpleList structs
        // we have to deref to read.
        var itemPtr = BinaryUtils.ReadUInt32BE(ownerBuffer, listOffset);
        var nextNodePtr = BinaryUtils.ReadUInt32BE(ownerBuffer, listOffset + 4);

        for (var i = 0; i < MaxConditionItems; i++)
        {
            if (itemPtr != 0 && context.IsValidPointer(itemPtr))
            {
                var citem = ReadConditionItem(context, itemPtr);
                if (citem is not null)
                {
                    conditions.Add(citem);
                }
            }

            if (nextNodePtr == 0 || !context.IsValidPointer(nextNodePtr))
            {
                break;
            }

            var nextNodeBytes = context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(nextNodePtr), BsSimpleListSize);
            if (nextNodeBytes is null)
            {
                break;
            }

            itemPtr = BinaryUtils.ReadUInt32BE(nextNodeBytes, 0);
            nextNodePtr = BinaryUtils.ReadUInt32BE(nextNodeBytes, 4);
        }

        return conditions;
    }

    private static DialogueCondition? ReadConditionItem(RuntimeMemoryContext context, uint itemVa)
    {
        var itemBytes = context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(itemVa), ConditionItemDataSize);
        if (itemBytes is null)
        {
            return null;
        }

        var iFlags = itemBytes[CitemFlagsOffset];

        // Comparison value: literal float when UseGlobal bit clear, TESGlobal* otherwise.
        float comparisonValue;
        if ((iFlags & CtdaTypeUseGlobalBit) != 0)
        {
            // Resolve global pointer → global's FormID, then reinterpret those 4 bytes as float
            // (matching how CTDA serializes Use-Global comparisons).
            var globalFormId = context.FollowPointerToFormId(itemBytes, CitemCompareValueOffset) ?? 0u;
            comparisonValue = BitConverter.UInt32BitsToSingle(globalFormId);
        }
        else
        {
            comparisonValue = BinaryUtils.ReadFloatBE(itemBytes, CitemCompareValueOffset);
        }

        var functionIndex = BinaryUtils.ReadUInt16BE(itemBytes, CitemFunctionIndexOffset);
        var parameter1 = ResolveConditionParameter(context, itemBytes, CitemParam1Offset, functionIndex, 0);
        var parameter2 = ResolveConditionParameter(context, itemBytes, CitemParam2Offset, functionIndex, 1);
        var runOn = BinaryUtils.ReadUInt32BE(itemBytes, CitemRunOnOffset);
        var reference = context.FollowPointerToFormId(itemBytes, CitemRunOnRefOffset) ?? 0u;

        return new DialogueCondition
        {
            Type = iFlags,
            ComparisonValue = comparisonValue,
            FunctionIndex = functionIndex,
            Parameter1 = parameter1,
            Parameter2 = parameter2,
            RunOn = runOn,
            Reference = reference
        };
    }

    /// <summary>
    ///     Resolve a single pParam[i] slot. <see cref="PerkConditionParameterResolver.IsFormParameter" />
    ///     decides whether the runtime stored a TESForm* (deref to FormID) or a uint32 value
    ///     cast to a pointer (read raw).
    /// </summary>
    private static uint ResolveConditionParameter(
        RuntimeMemoryContext context,
        byte[] itemBytes,
        int offset,
        ushort functionIndex,
        int parameterIndex)
    {
        if (offset + 4 > itemBytes.Length)
        {
            return 0u;
        }

        var raw = BinaryUtils.ReadUInt32BE(itemBytes, offset);
        if (raw == 0)
        {
            return 0u;
        }

        if (PerkConditionParameterResolver.IsFormParameter(functionIndex, parameterIndex))
        {
            return context.FollowPointerToFormId(itemBytes, offset) ?? 0u;
        }

        return raw;
    }
}
