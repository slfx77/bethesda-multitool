using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESIdleForm (IDLE, 92 bytes, FormType 0x48).
///     Reads animation path, IDLE_DATA (loop/timing), parent idle FormID
///     (via pParentIdle pointer at +84), and the embedded
///     <see cref="TesConditionListWalker" /> at +64 to capture each CTDA condition
///     (function index + parameters + comparison operator). Without the CTDAs the
///     idle is unconditional in our emitted plugin and the engine plays it for
///     every standing NPC (proto crucifix-idle bug).
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
    public IdleAnimationRecord? ReadRuntimeIdleAnimation(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != IdleFormType)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + StructSize > context.FileSize)
        {
            return null;
        }

        var buffer = new byte[StructSize];
        try
        {
            context.Accessor.ReadArray(offset, buffer, 0, StructSize);
        }
        catch
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, FormIdOffset);
        if (formId != entry.FormId || formId == 0)
        {
            return null;
        }

        var modelPath = context.ReadBsStringT(offset, ModelOffset);
        var parentIdleFormId = context.FollowPointerToFormId(buffer, ParentIdlePointerOffset) ?? 0;

        // IDLE_DATA at +72: AnimData(1), LoopMin(1), LoopMax(1), pad(1), ReplayDelay(2), FlagsEx(1), pad(1)
        var animData = buffer[DataOffset];
        var loopMin = buffer[DataOffset + 1];
        var loopMax = buffer[DataOffset + 2];
        var replayDelay = BinaryUtils.ReadUInt16BE(buffer, DataOffset + 4);
        var flagsEx = buffer[DataOffset + 6];

        // Walk the embedded TESCondition.BSSimpleList<TESConditionItem*> at +64.
        // Each visited TESConditionItem yields one DialogueCondition.
        var conditions = TesConditionListWalker.Walk(context, buffer, ConditionsOffset);

        return new IdleAnimationRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            ModelPath = modelPath,
            ParentIdleFormId = parentIdleFormId,
            AnimData = animData,
            LoopMin = loopMin,
            LoopMax = loopMax,
            ReplayDelay = replayDelay,
            FlagsEx = flagsEx,
            ConditionCount = conditions.Count,
            Conditions = conditions,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte IdleFormType = 0x48;
    private const int StructSize = 92;
    private const int FormIdOffset = 12;
    private const int ModelOffset = 44;
    private const int ConditionsOffset = 64;   // TESCondition (BSSimpleList) embedded here
    private const int DataOffset = 72;
    private const int ParentIdlePointerOffset = 84;

    #endregion
}
