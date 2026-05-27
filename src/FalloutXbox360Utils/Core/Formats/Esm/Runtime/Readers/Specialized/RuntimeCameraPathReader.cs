using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSCameraPath (CPTH, FormType 0x5C).
///     Reads PATH_DATA flag + parent/previous path pointers + the embedded TESCondition
///     (BSSimpleList) at the PDB-resolved <c>Conditions</c> offset (+40 in BGSCameraPath).
///     Mirrors <see cref="RuntimeIdleAnimationReader" />'s condition walk.
/// </summary>
internal sealed class RuntimeCameraPathReader(RuntimeMemoryContext context)
{
    private const byte CpthFormType = 0x5C;

    private readonly RuntimePdbFieldAccessor _fields = new(context);
    private readonly RuntimeMemoryContext _context = context;

    public CameraPathRecord? ReadRuntimeCameraPath(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != CpthFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, CpthFormType);
        if (view == null)
        {
            return null;
        }

        // Walk the embedded TESCondition.BSSimpleList<TESConditionItem*> at Conditions.
        var conditionsOff = view.Offset("Conditions", "BGSCameraPath");
        var conditions = conditionsOff is { } c
            ? TesConditionListWalker.Walk(_context, view.Buffer, c)
            : new List<DialogueCondition>();

        return new CameraPathRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            Flags = view.Byte("data", "BGSCameraPath"),
            ParentPathFormId = view.FormIdPointer("pParentPath", "BGSCameraPath") ?? 0,
            PreviousPathFormId = view.FormIdPointer("pPrevPath", "BGSCameraPath") ?? 0,
            ConditionCount = conditions.Count,
            Conditions = conditions,
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
