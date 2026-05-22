using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSConstructibleObject (COBJ, FormType 0x32).
///     Reads the CreatedItem FormID by following the pCreatedItem pointer at +192.
///     pRequiredItems (+188, BGSListForm) is the materialized inline ingredient list —
///     typically anonymous (FormID 0), so we don't expose it.
/// </summary>
internal sealed class RuntimeConstructibleObjectReader(RuntimeMemoryContext context)
{
    private const byte CobjFormType = 0x32;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    public ConstructibleObjectRecord? ReadRuntimeConstructibleObject(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != CobjFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry);
        if (view == null)
        {
            return null;
        }

        return new ConstructibleObjectRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            CreatedItemFormId = view.FormIdPointer("pCreatedItem", "BGSConstructibleObject"),
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
