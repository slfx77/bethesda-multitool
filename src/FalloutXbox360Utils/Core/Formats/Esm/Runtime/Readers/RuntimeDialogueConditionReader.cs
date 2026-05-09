using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Script;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers;

/// <summary>
///     Reads runtime TESConditionItem entries and walks BSSimpleList chains
///     for conditions, add-topics, and form ID lists from Xbox 360 memory dumps.
///     Extracted from <see cref="RuntimeDialogueReader" />.
/// </summary>
internal sealed class RuntimeDialogueConditionReader
{
    private readonly RuntimeMemoryContext _context;

    public RuntimeDialogueConditionReader(RuntimeMemoryContext context)
    {
        _context = context;
    }

    /// <summary>
    ///     Walk TESTopicInfo.objConditions (TESCondition) and decode TESConditionItem entries
    ///     into the same semantic model used for CTDA subrecords.
    /// </summary>
    internal RuntimeConditionData ReadConditions(long infoStructOffset, int conditionsOffset)
    {
        var results = new RuntimeConditionData();

        var listOffset = infoStructOffset + conditionsOffset;
        var listBuf = _context.ReadBytes(listOffset, 8);
        if (listBuf == null)
        {
            return results;
        }

        ReadConditionListItem(BinaryUtils.ReadUInt32BE(listBuf), results);

        var nextVa = BinaryUtils.ReadUInt32BE(listBuf, 4);
        var visited = new HashSet<uint>();
        while (nextVa != 0 &&
               results.Conditions.Count < RuntimeMemoryContext.MaxListItems &&
               !visited.Contains(nextVa))
        {
            visited.Add(nextVa);

            var nodeBuf = _context.ReadBytesAtVa(nextVa, 8);
            if (nodeBuf == null)
            {
                break;
            }

            ReadConditionListItem(BinaryUtils.ReadUInt32BE(nodeBuf), results);
            nextVa = BinaryUtils.ReadUInt32BE(nodeBuf, 4);
        }

        return results;
    }

    /// <summary>
    ///     Walk the m_listAddTopics BSSimpleList&lt;TESTopic*&gt; on a TESTopicInfo struct.
    ///     Each list node contains a pointer to a TESTopic whose FormID we extract.
    ///     Returns a list of topic FormIDs that this INFO adds to the NPC's topic menu.
    /// </summary>
    internal List<uint> WalkAddTopicsList(long infoStructOffset, int addTopicsOffset)
    {
        var results = new List<uint>();

        // Read the BSSimpleList inline node (8 bytes: m_item + m_pkNext)
        var listOffset = infoStructOffset + addTopicsOffset;
        var listBuf = _context.ReadBytes(listOffset, 8);
        if (listBuf == null)
        {
            return results;
        }

        var firstItem = BinaryUtils.ReadUInt32BE(listBuf); // TESTopic* pointer
        var firstNext = BinaryUtils.ReadUInt32BE(listBuf, 4); // _Node* pointer

        // Process inline first item — follow pointer to TESTopic, read FormID at +12
        var firstFormId = _context.FollowPointerVaToFormId(firstItem);
        if (firstFormId != null)
        {
            results.Add(firstFormId.Value);
        }

        // Follow BSSimpleList chain
        var nextVA = firstNext;
        var visited = new HashSet<uint>();
        while (nextVA != 0 && results.Count < RuntimeMemoryContext.MaxListItems && !visited.Contains(nextVA))
        {
            visited.Add(nextVA);
            var nodeFileOffset = _context.VaToFileOffset(nextVA);
            if (nodeFileOffset == null)
            {
                break;
            }

            var nodeBuf = _context.ReadBytes(nodeFileOffset.Value, 8);
            if (nodeBuf == null)
            {
                break;
            }

            var dataPtr = BinaryUtils.ReadUInt32BE(nodeBuf); // TESTopic*
            var nextPtr = BinaryUtils.ReadUInt32BE(nodeBuf, 4); // _Node*

            var topicFormId = _context.FollowPointerVaToFormId(dataPtr);
            if (topicFormId != null)
            {
                results.Add(topicFormId.Value);
            }

            nextVA = nextPtr;
        }

        return results;
    }

    /// <summary>
    ///     Walk a BSSimpleList of TESForm pointers embedded in a struct buffer,
    ///     extracting FormIDs via pointer resolution.
    /// </summary>
    internal List<uint> WalkFormIdSimpleList(byte[] structBuffer, int listHeadOffset)
    {
        var results = new List<uint>();
        if (listHeadOffset + 8 > structBuffer.Length)
        {
            return results;
        }

        var firstItem = BinaryUtils.ReadUInt32BE(structBuffer, listHeadOffset);
        var firstNext = BinaryUtils.ReadUInt32BE(structBuffer, listHeadOffset + 4);

        var firstFormId = _context.FollowPointerVaToFormId(firstItem);
        if (firstFormId is > 0)
        {
            results.Add(firstFormId.Value);
        }

        var nextVa = firstNext;
        var visited = new HashSet<uint>();
        while (nextVa != 0 && results.Count < RuntimeMemoryContext.MaxListItems && visited.Add(nextVa))
        {
            var nodeBuf = _context.ReadBytesAtVa(nextVa, 8);
            if (nodeBuf == null)
            {
                break;
            }

            var itemPtr = BinaryUtils.ReadUInt32BE(nodeBuf);
            nextVa = BinaryUtils.ReadUInt32BE(nodeBuf, 4);

            var formId = _context.FollowPointerVaToFormId(itemPtr);
            if (formId is > 0)
            {
                results.Add(formId.Value);
            }
        }

        return results;
    }

    private void ReadConditionListItem(uint conditionItemVa, RuntimeConditionData results)
    {
        var condition = ReadCondition(conditionItemVa);
        if (condition == null)
        {
            return;
        }

        results.Functions.Add(condition.FunctionIndex);
        results.Conditions.Add(condition);
        ApplySpeakerConditionHints(
            condition,
            ref results.ConditionSpeakerFormId,
            ref results.SpeakerFactionFormId,
            ref results.SpeakerRaceFormId,
            ref results.SpeakerVoiceTypeFormId);
    }

    private DialogueCondition? ReadCondition(uint conditionItemVa)
    {
        if (!_context.IsValidPointer(conditionItemVa))
        {
            return null;
        }

        // TESConditionItem is a 28-byte wrapper whose first field is CONDITION_ITEM_DATA.
        var buffer = _context.ReadBytesAtVa(conditionItemVa, 28);
        if (buffer == null)
        {
            return null;
        }

        var type = buffer[0];
        var comparisonOperator = (type >> 5) & 0x7;
        if (comparisonOperator > 5)
        {
            return null;
        }

        var functionIndex = BinaryUtils.ReadUInt16BE(buffer, 8);
        var comparisonValue = BinaryUtils.ReadFloatBE(buffer, 4);
        if (!RuntimeMemoryContext.IsNormalFloat(comparisonValue))
        {
            comparisonValue = 0;
        }

        var rawParam1 = BinaryUtils.ReadUInt32BE(buffer, 12);
        var rawParam2 = BinaryUtils.ReadUInt32BE(buffer, 16);
        var runOn = BinaryUtils.ReadUInt32BE(buffer, 20);
        var referencePtr = BinaryUtils.ReadUInt32BE(buffer, 24);

        if (type == 0 &&
            functionIndex == 0 &&
            rawParam1 == 0 &&
            rawParam2 == 0 &&
            runOn == 0 &&
            referencePtr == 0 &&
            MathF.Abs(comparisonValue) < 0.0001f)
        {
            return null;
        }

        return new DialogueCondition
        {
            Type = type,
            ComparisonValue = comparisonValue,
            FunctionIndex = functionIndex,
            Parameter1 = ResolveConditionParameter(functionIndex, 0, rawParam1),
            Parameter2 = ResolveConditionParameter(functionIndex, 1, rawParam2),
            RunOn = runOn,
            Reference = _context.FollowPointerVaToFormId(referencePtr) ?? 0
        };
    }

    private uint ResolveConditionParameter(ushort functionIndex, int parameterIndex, uint rawValue)
    {
        if (rawValue == 0)
        {
            return 0;
        }

        var function = ScriptFunctionTable.Get((ushort)(0x1000 | functionIndex));
        var paramType = function is not null && parameterIndex < function.Params.Length
            ? function.Params[parameterIndex].Type
            : (ScriptParamType?)null;

        if (!ShouldResolveConditionParameterAsForm(paramType))
        {
            return rawValue;
        }

        return _context.FollowPointerVaToFormId(rawValue) ?? rawValue;
    }

    private static bool ShouldResolveConditionParameterAsForm(ScriptParamType? paramType)
    {
        return paramType switch
        {
            null => false,
            ScriptParamType.Char or
                ScriptParamType.Int or
                ScriptParamType.Float or
                ScriptParamType.Axis or
                ScriptParamType.AnimGroup or
                ScriptParamType.Sex or
                ScriptParamType.ScriptVar or
                ScriptParamType.Stage or
                ScriptParamType.CrimeType or
                ScriptParamType.FormType or
                ScriptParamType.MiscStat or
                ScriptParamType.VatsValue or
                ScriptParamType.VatsValueData or
                ScriptParamType.Alignment or
                ScriptParamType.CritStage => false,
            _ => true
        };
    }

    private static void ApplySpeakerConditionHints(
        DialogueCondition condition,
        ref uint? conditionSpeaker,
        ref uint? conditionFaction,
        ref uint? conditionRace,
        ref uint? conditionVoiceType)
    {
        var comparisonOperator = (condition.Type >> 5) & 0x7;
        var isPositive = condition.RunOn == 0 &&
                         ((comparisonOperator is 0 or 3 && condition.ComparisonValue >= 0.99f) ||
                          (comparisonOperator is 1 && condition.ComparisonValue < 0.01f) ||
                          (comparisonOperator is 2 && condition.ComparisonValue < 0.01f));

        if (!isPositive)
        {
            return;
        }

        switch (condition.FunctionIndex)
        {
            case 0x48:
                conditionSpeaker ??= condition.Parameter1;
                break;
            case 0x47:
                conditionFaction ??= condition.Parameter1;
                break;
            case 0x45:
                conditionRace ??= condition.Parameter1;
                break;
            case 0x1AB:
                conditionVoiceType ??= condition.Parameter1;
                break;
        }
    }

    internal sealed class RuntimeConditionData
    {
        public uint? ConditionSpeakerFormId;
        public uint? SpeakerFactionFormId;
        public uint? SpeakerRaceFormId;
        public uint? SpeakerVoiceTypeFormId;
        public List<DialogueCondition> Conditions { get; } = [];
        public List<ushort> Functions { get; } = [];
    }
}
