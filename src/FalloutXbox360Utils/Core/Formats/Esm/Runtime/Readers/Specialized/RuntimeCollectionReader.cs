using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Reader for runtime FLST and leveled-list structs from Xbox 360 memory dumps.
/// </summary>
internal sealed class RuntimeCollectionReader(RuntimeMemoryContext context)
{
    private readonly RuntimeMemoryContext _context = context;
    private readonly RuntimePdbFieldAccessor _fields = new(context);

    internal FormListRecord? ReadRuntimeFormList(RuntimeEditorIdEntry entry)
    {
        var structData = _fields.ReadStruct(entry);
        if (structData == null || entry.FormType != 0x55)
        {
            return null;
        }

        var (layout, buffer, fileOffset) = structData.Value;
        var listHeadOffset = RuntimePdbFieldAccessor.FindFieldOffset(layout, "ListOfForms", "BGSListForm");
        if (!listHeadOffset.HasValue)
        {
            return null;
        }

        return new FormListRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FormIds = _fields.ReadFormIdSimpleList(buffer, listHeadOffset.Value),
            Offset = fileOffset,
            IsBigEndian = true
        };
    }

    internal LeveledListRecord? ReadRuntimeLeveledList(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType is not (0x2C or 0x2D or 0x34))
        {
            return null;
        }

        var structData = _fields.ReadStruct(entry);
        if (structData == null)
        {
            return null;
        }

        var (layout, buffer, fileOffset) = structData.Value;
        var listHeadOffset = RuntimePdbFieldAccessor.FindFieldOffset(layout, "leveledList", "TESLeveledList");
        var chanceOffset = RuntimePdbFieldAccessor.FindFieldOffset(layout, "cChanceNone", "TESLeveledList");
        var flagsOffset = RuntimePdbFieldAccessor.FindFieldOffset(layout, "cLLFlags", "TESLeveledList");
        var globalOffset = RuntimePdbFieldAccessor.FindFieldOffset(layout, "pChanceGlobal", "TESLeveledList");

        if (!listHeadOffset.HasValue || !chanceOffset.HasValue || !flagsOffset.HasValue)
        {
            return null;
        }

        var entries = _fields.ReadSimpleList(buffer, listHeadOffset.Value, ReadLeveledObject);

        return new LeveledListRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            ListType = RuntimeBuildOffsets.GetRecordTypeCode(entry.FormType) ?? "LVLI",
            ChanceNone = buffer[chanceOffset.Value],
            Flags = buffer[flagsOffset.Value],
            GlobalFormId = globalOffset.HasValue
                ? _fields.ReadPointerToFormId(buffer, globalOffset.Value)
                : null,
            Entries = entries.Select(e => new LeveledEntry(e.Level, e.FormId, e.Count)).ToList(),
            Offset = fileOffset,
            IsBigEndian = true
        };
    }

    private LeveledObjectData? ReadLeveledObject(uint objectVa)
    {
        if (!_context.IsValidPointer(objectVa))
        {
            return null;
        }

        var fileOffset = _context.VaToFileOffset(objectVa);
        if (fileOffset == null)
        {
            return null;
        }

        var buffer = _context.ReadBytes(fileOffset.Value, 12);
        if (buffer == null)
        {
            return null;
        }

        var formId = _context.FollowPointerVaToFormId(BinaryUtils.ReadUInt32BE(buffer));
        if (formId == null || formId == 0)
        {
            return null;
        }

        var count = BinaryUtils.ReadUInt16BE(buffer, 4);
        var level = BinaryUtils.ReadUInt16BE(buffer, 6);

        return new LeveledObjectData(formId.Value, level, count);
    }

    private sealed record LeveledObjectData(uint FormId, ushort Level, ushort Count);
}
