using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.RuntimeBuffer;

/// <summary>
///     Claims strings owned by runtime-only nested structs that are not TESForm-derived.
///     These include quest objectives, script variables/ref aliases, message buttons, and
///     terminal menu items. The semantic readers already walk many of these lists, but
///     ownership reporting needs the exact BSStringT string offsets.
/// </summary>
internal static class RuntimeNestedStringClaimExtractor
{
    internal static List<RuntimeStringOwnershipClaim> ExtractClaims(
        IReadOnlyList<RuntimeEditorIdEntry> runtimeEditorIds,
        RuntimeMemoryContext memCtx)
    {
        var claims = new List<RuntimeStringOwnershipClaim>();
        var claimedOffsets = new HashSet<long>();
        var shift = RuntimeBuildOffsets.GetPdbShift(MinidumpAnalyzer.DetectBuildType(memCtx.MinidumpInfo));

        foreach (var entry in runtimeEditorIds)
        {
            if (entry.TesFormOffset == null)
            {
                continue;
            }

            switch (entry.FormType)
            {
                case ScriptFormType:
                    ExtractScriptNestedClaims(entry, memCtx, shift, claims, claimedOffsets);
                    break;
                case TerminalFormType:
                    ExtractTerminalMenuClaims(entry, memCtx, shift, claims, claimedOffsets);
                    break;
                case QuestFormType:
                    ExtractQuestObjectiveClaims(entry, memCtx, shift, claims, claimedOffsets);
                    break;
                case MessageFormType:
                    ExtractMessageButtonClaims(entry, memCtx, claims, claimedOffsets);
                    break;
            }
        }

        return claims;
    }

    private static void ExtractScriptNestedClaims(
        RuntimeEditorIdEntry entry,
        RuntimeMemoryContext memCtx,
        int shift,
        List<RuntimeStringOwnershipClaim> claims,
        HashSet<long> claimedOffsets)
    {
        var offset = entry.TesFormOffset!.Value;
        var structSize = ScriptStructPdbSize + shift;
        var buffer = ReadValidatedTesFormBuffer(entry, memCtx, offset, structSize);
        if (buffer == null)
        {
            return;
        }

        var variableCount = BinaryUtils.ReadUInt32BE(buffer, ScriptVariableCountOffset + shift);
        var refObjectCount = BinaryUtils.ReadUInt32BE(buffer, ScriptRefObjectCountOffset + shift);

        var ownerName = FormatOwner("SCPT", entry);
        var maxVariables = ComputeListLimit(variableCount);
        foreach (var itemVa in memCtx.WalkInlineBSSimpleListItemPointers(
                     buffer, ScriptVariablesListOffset + shift, maxVariables))
        {
            var itemOffset = memCtx.VaToFileOffset(itemVa);
            if (itemOffset == null)
            {
                continue;
            }

            var item = memCtx.ReadBytes(itemOffset.Value, ScriptVariableStructSize);
            if (item == null || BinaryUtils.ReadUInt32BE(item) > MaxScriptVariableIndex)
            {
                continue;
            }

            AddClaim(
                claims,
                claimedOffsets,
                memCtx,
                itemOffset.Value,
                ScriptVariableNameOffset,
                "RuntimeStruct",
                ownerName,
                entry.FormId,
                offset,
                "SCPT",
                "ScriptVariable.cName");
        }

        var maxRefObjects = ComputeListLimit(refObjectCount);
        foreach (var itemVa in memCtx.WalkInlineBSSimpleListItemPointers(
                     buffer, ScriptRefObjectsListOffset + shift, maxRefObjects))
        {
            var itemOffset = memCtx.VaToFileOffset(itemVa);
            if (itemOffset == null)
            {
                continue;
            }

            var item = memCtx.ReadBytes(itemOffset.Value, ScriptReferencedObjectStructSize);
            if (item == null)
            {
                continue;
            }

            AddClaim(
                claims,
                claimedOffsets,
                memCtx,
                itemOffset.Value,
                ScriptReferencedObjectEditorIdOffset,
                "RuntimeStruct",
                ownerName,
                entry.FormId,
                offset,
                "SCPT",
                "SCRIPT_REFERENCED_OBJECT.cEditorID");
        }
    }

    private static void ExtractTerminalMenuClaims(
        RuntimeEditorIdEntry entry,
        RuntimeMemoryContext memCtx,
        int shift,
        List<RuntimeStringOwnershipClaim> claims,
        HashSet<long> claimedOffsets)
    {
        var offset = entry.TesFormOffset!.Value;
        var structSize = TerminalStructPdbSize + shift;
        var buffer = ReadValidatedTesFormBuffer(entry, memCtx, offset, structSize);
        if (buffer == null)
        {
            return;
        }

        var ownerName = FormatOwner("TERM", entry);
        foreach (var itemVa in memCtx.WalkInlineBSSimpleListItemPointers(
                     buffer, TerminalMenuItemListOffset + shift))
        {
            var itemOffset = memCtx.VaToFileOffset(itemVa);
            if (itemOffset == null)
            {
                continue;
            }

            AddClaim(
                claims,
                claimedOffsets,
                memCtx,
                itemOffset.Value,
                TerminalMenuItemResponseTextOffset,
                "RuntimeStruct",
                ownerName,
                entry.FormId,
                offset,
                "TERM",
                "TERMINAL_MENU_ITEM.ResponseText");
        }
    }

    private static void ExtractQuestObjectiveClaims(
        RuntimeEditorIdEntry entry,
        RuntimeMemoryContext memCtx,
        int shift,
        List<RuntimeStringOwnershipClaim> claims,
        HashSet<long> claimedOffsets)
    {
        var offset = entry.TesFormOffset!.Value;
        var structSize = QuestStructPdbSize + shift;
        var buffer = ReadValidatedTesFormBuffer(entry, memCtx, offset, structSize);
        if (buffer == null)
        {
            return;
        }

        var ownerName = FormatOwner("QUST", entry);
        foreach (var objectiveVa in memCtx.WalkInlineBSSimpleListItemPointers(
                     buffer, QuestObjectiveListOffset + shift))
        {
            var objectiveOffset = memCtx.VaToFileOffset(objectiveVa);
            if (objectiveOffset == null)
            {
                continue;
            }

            var objective = memCtx.ReadBytes(objectiveOffset.Value, QuestObjectiveStructSize);
            if (objective == null)
            {
                continue;
            }

            var index = unchecked((int)BinaryUtils.ReadUInt32BE(objective, QuestObjectiveIndexOffset));
            if (index < 0 || index > MaxQuestObjectiveIndex)
            {
                continue;
            }

            var ownerQuestPtr = BinaryUtils.ReadUInt32BE(objective, QuestObjectiveOwnerQuestPtrOffset);
            var ownerQuestFormId = memCtx.FollowPointerVaToFormId(ownerQuestPtr);
            if (ownerQuestFormId.HasValue && ownerQuestFormId.Value != entry.FormId)
            {
                continue;
            }

            var state = BinaryUtils.ReadUInt32BE(objective, QuestObjectiveStateOffset);
            if (state > MaxQuestObjectiveState)
            {
                continue;
            }

            AddClaim(
                claims,
                claimedOffsets,
                memCtx,
                objectiveOffset.Value,
                QuestObjectiveDisplayTextOffset,
                "RuntimeStruct",
                ownerName,
                entry.FormId,
                offset,
                "QUST",
                "BGSQuestObjective.displayText");
        }
    }

    private static void ExtractMessageButtonClaims(
        RuntimeEditorIdEntry entry,
        RuntimeMemoryContext memCtx,
        List<RuntimeStringOwnershipClaim> claims,
        HashSet<long> claimedOffsets)
    {
        var offset = entry.TesFormOffset!.Value;
        var buffer = ReadValidatedTesFormBuffer(entry, memCtx, offset, MessageStructSize);
        if (buffer == null)
        {
            return;
        }

        var ownerName = FormatOwner("MESG", entry);
        foreach (var itemVa in memCtx.WalkInlineBSSimpleListItemPointers(buffer, MessageButtonListOffset))
        {
            var itemOffset = memCtx.VaToFileOffset(itemVa);
            if (itemOffset == null)
            {
                continue;
            }

            AddClaim(
                claims,
                claimedOffsets,
                memCtx,
                itemOffset.Value,
                MessageButtonTextOffset,
                "RuntimeStruct",
                ownerName,
                entry.FormId,
                offset,
                "MESG",
                "MESSAGEBOX_BUTTON.text");
        }
    }

    private static byte[]? ReadValidatedTesFormBuffer(
        RuntimeEditorIdEntry entry,
        RuntimeMemoryContext memCtx,
        long offset,
        int structSize)
    {
        if (offset + structSize > memCtx.FileSize)
        {
            return null;
        }

        var buffer = memCtx.ReadBytes(offset, structSize);
        if (buffer == null)
        {
            return null;
        }

        return BinaryUtils.ReadUInt32BE(buffer, TesFormIdOffset) == entry.FormId ? buffer : null;
    }

    private static void AddClaim(
        List<RuntimeStringOwnershipClaim> claims,
        HashSet<long> claimedOffsets,
        RuntimeMemoryContext memCtx,
        long structOffset,
        int fieldOffset,
        string ownerKind,
        string ownerName,
        uint ownerFormId,
        long ownerFileOffset,
        string recordType,
        string fieldLabel)
    {
        var info = memCtx.ReadBSStringTInfo(structOffset, fieldOffset);
        if (info == null || !claimedOffsets.Add(info.Value.StringFileOffset))
        {
            return;
        }

        claims.Add(new RuntimeStringOwnershipClaim(
            info.Value.StringFileOffset,
            memCtx.MinidumpInfo.FileOffsetToVirtualAddress(info.Value.StringFileOffset),
            ownerKind,
            ownerName,
            ownerFormId != 0 ? ownerFormId : null,
            ownerFileOffset,
            ClaimSource.RuntimeStructField,
            recordType,
            fieldLabel));
    }

    private static string FormatOwner(string recordCode, RuntimeEditorIdEntry entry)
    {
        return string.IsNullOrWhiteSpace(entry.EditorId)
            ? $"{recordCode} [{entry.FormId:X8}]"
            : $"{recordCode} {entry.EditorId}";
    }

    private static int ComputeListLimit(uint expectedCount)
    {
        return (int)Math.Min(Math.Max(expectedCount + 10, RuntimeMemoryContext.MaxListItems), 200);
    }

    private const byte ScriptFormType = 0x11;
    private const byte TerminalFormType = 0x17;
    private const byte QuestFormType = 0x47;
    private const byte MessageFormType = 0x62;

    private const int TesFormIdOffset = 12;

    private const int ScriptStructPdbSize = 84;
    private const int ScriptVariableCountOffset = 24;
    private const int ScriptRefObjectCountOffset = 28;
    private const int ScriptRefObjectsListOffset = 68;
    private const int ScriptVariablesListOffset = 76;
    private const int ScriptVariableStructSize = 32;
    private const int ScriptVariableNameOffset = 24;
    private const int ScriptReferencedObjectStructSize = 16;
    private const int ScriptReferencedObjectEditorIdOffset = 0;
    private const uint MaxScriptVariableIndex = 10000;

    private const int TerminalStructPdbSize = 168;
    private const int TerminalMenuItemListOffset = 136;
    private const int TerminalMenuItemResponseTextOffset = 0;

    private const int QuestStructPdbSize = 108;
    private const int QuestObjectiveListOffset = 76;
    private const int QuestObjectiveStructSize = 36;
    private const int QuestObjectiveIndexOffset = 4;
    private const int QuestObjectiveDisplayTextOffset = 8;
    private const int QuestObjectiveOwnerQuestPtrOffset = 16;
    private const int QuestObjectiveStateOffset = 32;
    private const int MaxQuestObjectiveIndex = 4096;
    private const int MaxQuestObjectiveState = 8;

    private const int MessageStructSize = 80;
    private const int MessageButtonListOffset = 64;
    private const int MessageButtonTextOffset = 8;
}
