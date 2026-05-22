using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSDebris (DEBR, FormType 0x52).
///     Walks the DataList BSSimpleList via the PDB layout to count debris variants.
///     Doesn't unpack the per-variant BGSDebrisData (opaque struct).
/// </summary>
internal sealed class RuntimeDebrisReader(RuntimeMemoryContext context)
{
    private const byte DebrFormType = 0x52;

    private readonly RuntimePdbFieldAccessor _fields = new(context);
    private readonly RuntimeMemoryContext _context = context;

    public DebrisRecord? ReadRuntimeDebris(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != DebrFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry);
        if (view == null)
        {
            return null;
        }

        var dataListOff = view.Offset("DataList", "BGSDebris");
        var variantCount = 0;
        if (dataListOff is { } o)
        {
            foreach (var _ in _context.WalkInlineBSSimpleListItemPointers(view.Buffer, o))
            {
                variantCount++;
            }
        }

        return new DebrisRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            VariantCount = variantCount,
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
