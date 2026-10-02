using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>Shared semantic/ownership reader for the PDB-described message button allocation.</summary>
internal static class RuntimeMessageDataReader
{
    private const int MaxNodes = 256;

    internal sealed record Result(List<string> Buttons, List<List<DialogueCondition>> Conditions,
        RuntimeMessageEvidence Evidence);

    internal static Result Read(RuntimeMemoryContext context, PdbStructView view)
    {
        var buttons = new List<string>();
        var conditions = new List<List<DialogueCondition>>();
        var evidence = new List<RuntimeMessageButtonEvidence>();
        var descriptionOffset = view.Offset("lFileOffset", "TESDescription") is { } offset
            ? BinaryUtils.ReadUInt32BE(view.Buffer, offset) : (uint?)null;
        Result Finish(string status) => new(buttons, conditions, new(descriptionOffset,
            descriptionOffset.HasValue ? "file-offset-only; description text not recovered" : "layout-unavailable",
            status, evidence));

        if (view.Offset("ButtonList", "BGSMessage") is not { } listOffset ||
            !PdbStructLayouts.TryGetAuxStruct("MESSAGEBOX_BUTTON", out var layout) ||
            layout.OffsetOf("Text") is not { } textOffset ||
            layout.OffsetOf("Conditions") is not { } conditionOffset)
        {
            return Finish("layout-unavailable");
        }

        var items = Walk(context, view.Buffer, listOffset, out var status);
        var conditionReader = new RuntimeDialogueConditionReader(context);
        foreach (var itemVa in items)
        {
            var item = context.ReadBytesAtVa(itemVa, layout.StructSize);
            var index = buttons.Count;
            var parsedConditions = new List<DialogueCondition>();
            if (item == null)
            {
                buttons.Add("");
                conditions.Add(parsedConditions);
                evidence.Add(new(index, itemVa, context.VaToFileOffset(itemVa), null, null,
                    "item-not-captured", "item-not-captured"));
                continue;
            }

            var textPointer = BinaryUtils.ReadUInt32BE(item, textOffset);
            var textLength = BinaryUtils.ReadUInt16BE(item, textOffset + 4);
            var text = context.ReadBSStringTDiag(item, textOffset, out var failure);
            var textStatus = textLength == 0 ? "empty" : text != null ? "captured" : failure.ToString();
            buttons.Add(text ?? "");
            var conditionItems = Walk(context, item, conditionOffset, out var conditionStatus);
            foreach (var conditionVa in conditionItems)
            {
                var condition = conditionReader.ReadCondition(conditionVa);
                if (condition != null) parsedConditions.Add(condition);
                else conditionStatus = "partial: condition unavailable or invalid";
            }
            conditions.Add(parsedConditions);
            evidence.Add(new(index, itemVa, context.VaToFileOffset(itemVa), textPointer,
                context.VaToFileOffset(textPointer), textStatus, conditionStatus));
        }
        return Finish(status);
    }

    /// <summary>Retains unreadable items and detects partial, cyclic and bounded lists.</summary>
    private static List<uint> Walk(RuntimeMemoryContext context, byte[] owner, int offset, out string status)
    {
        var items = new List<uint>();
        status = "complete";
        if (offset < 0 || offset > owner.Length - 8)
        {
            status = "head-not-captured";
            return items;
        }
        var item = BinaryUtils.ReadUInt32BE(owner, offset);
        var next = BinaryUtils.ReadUInt32BE(owner, offset + 4);
        var visited = new HashSet<uint>();
        for (var count = 0; count < MaxNodes; count++)
        {
            // A null inline head is an empty list. A null interior item is an explicit hole.
            if (item != 0 || next != 0 || count > 0) items.Add(item);
            if (next == 0) return items;
            if (!visited.Add(next)) { status = "partial: cycle"; return items; }
            var node = context.ReadBytesAtVa(next, 8);
            if (node == null) { status = "partial: node-not-captured"; return items; }
            item = BinaryUtils.ReadUInt32BE(node);
            next = BinaryUtils.ReadUInt32BE(node, 4);
        }
        status = "partial: node-limit";
        return items;
    }
}
