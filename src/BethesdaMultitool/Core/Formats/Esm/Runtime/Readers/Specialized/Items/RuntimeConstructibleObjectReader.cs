using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Generic;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized.Items;

/// <summary>
///     Forensic FNV runtime reader for BGSConstructibleObject (COBJ, FormType 0x32).
///     Reads the CreatedItem FormID by following the pCreatedItem pointer at +192.
///     The adjacent pRequiredItems pointer is runtime-layout evidence only: it does not
///     establish CNTO quantities or any safe FNV on-disk recipe serialization.
/// </summary>
internal sealed class RuntimeConstructibleObjectReader(RuntimeMemoryContext context)
{
    private const byte CobjFormType = 0x32;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    /// <summary>Reads the runtime constructible-object probe for the given DMP entry, or null if it can't be read.</summary>
    public ConstructibleObjectRecord? ReadRuntimeConstructibleObject(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != CobjFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, CobjFormType);
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
