using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESIdleForm (IDLE, FormType 0x48).
///     Reads animation path, IDLE_DATA (loop/timing), parent idle FormID, and walks
///     the embedded TESCondition (BSSimpleList) at the PDB-resolved Conditions offset
///     via <see cref="TesConditionListWalker" />. Without the CTDAs the idle is
///     unconditional in our emitted plugin and the engine plays it for every standing
///     NPC (proto crucifix-idle bug).
///     <para>
///     Build drift on TESIdleForm:
///     <list type="bullet">
///         <item>Fallout_Release_Beta (xex*.dmp): 92 bytes, Conditions @ +64.</item>
///         <item>Fallout_Debug_Final (PC final debug): 92 bytes, Conditions @ +64.</item>
///         <item>Fallout_Debug (early debug): 84 bytes, Conditions @ +48. Not currently supported.</item>
///     </list>
///     </para>
/// </summary>
internal sealed class RuntimeIdleAnimationReader(RuntimeMemoryContext context)
{
    private const byte IdleFormType = 0x48;

    private readonly RuntimePdbFieldAccessor _fields = new(context);
    private readonly RuntimeMemoryContext _context = context;

    public IdleAnimationRecord? ReadRuntimeIdleAnimation(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != IdleFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry);
        if (view == null)
        {
            return null;
        }

        // IDLE_DATA at view.Offset("data", "TESIdleForm"): AnimData(1), LoopMin(1),
        // LoopMax(1), pad(1), ReplayDelay(2), FlagsEx(1), pad(1).
        var dataOff = view.Offset("data", "TESIdleForm");
        if (dataOff is not { } d || d + 7 > view.Buffer.Length)
        {
            return null;
        }

        var animData = view.Buffer[d];
        var loopMin = view.Buffer[d + 1];
        var loopMax = view.Buffer[d + 2];
        var replayDelay = BinaryUtils.ReadUInt16BE(view.Buffer, d + 4);
        var flagsEx = view.Buffer[d + 6];

        // Walk the embedded TESCondition.BSSimpleList<TESConditionItem*> at Conditions.
        var conditionsOff = view.Offset("Conditions", "TESIdleForm");
        var conditions = conditionsOff is { } c
            ? TesConditionListWalker.Walk(_context, view.Buffer, c)
            : new List<DialogueCondition>();

        return new IdleAnimationRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            ModelPath = view.BsString("cModel", "TESModel"),
            ParentIdleFormId = view.FormIdPointer("pParentIdle", "TESIdleForm") ?? 0,
            AnimData = animData,
            LoopMin = loopMin,
            LoopMax = loopMax,
            ReplayDelay = replayDelay,
            FlagsEx = flagsEx,
            ConditionCount = conditions.Count,
            Conditions = conditions,
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
