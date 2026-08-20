using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for <c>BGSStaticCollection</c> (SCOL, FormType 0x21).
///     <para>
///         The PDB layout carries NO part-list field — the last member ends exactly at
///         structSize 96 — so a runtime read can never recover the editor-side ONAM/DATA part
///         array. What it CAN recover is what the engine actually renders: the baked collection
///         model (<c>TESModel.cModel</c> @68) plus bounds (@52) and identity. That is enough for a
///         placed reference to resolve and draw, which is the whole reason this reader exists:
///         without it, 27 captured sidewalk/parking-lot refs in the proto Strip dropped as
///         <c>refr.dangling-base</c> because their proto-only SCOL bases never emitted
///         (USER RULING 2026-08-05, playtest finding 3, option A-with-B-fallback).
///     </para>
/// </summary>
internal sealed class RuntimeStaticCollectionReader(RuntimeMemoryContext context)
{
    private const byte ScolFormType = 0x21;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    /// <summary>Reads one runtime static collection, or null when the struct can't be read.</summary>
    public StaticCollectionRecord? ReadRuntimeStaticCollection(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != ScolFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, ScolFormType);
        if (view == null)
        {
            return null;
        }

        return new StaticCollectionRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            ModelPath = view.BsString("cModel", "TESModel"),
            Bounds = view.Bounds(),
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
