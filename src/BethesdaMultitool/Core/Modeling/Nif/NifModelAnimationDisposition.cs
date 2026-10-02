using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     One decision <see cref="NifModelAnimationReader" /> made about one block: Typed, or NativeOnly with a reason, with
///     where the decision was made (the sequence or controller, the controlled block, the channel). A block can receive
///     several decisions (one per channel, or per sequence that uses it);
///     <see cref="NifModelAnimationResult.Dispositions" /> merges them per block, Typed winning.
/// </summary>
/// <param name="Block">The block the decision is about.</param>
/// <param name="Disposition">Typed, or NativeOnly with its reason.</param>
/// <param name="Code">
///     A stable machine code: <see cref="NifModelAnimationReasons.TypedCode" /> for Typed, otherwise a
///     <see cref="NifModelAnimationReasons" /> code, the code of a curve or text-key refusal
///     (<see cref="NifModelAnimationReasons.Code(NifModelCurveBlock)" />), or of a target refusal
///     (<see cref="NifModelTargetNames.Code" />).
/// </param>
/// <param name="SourceBlock">
///     The NiControllerSequence the decision belongs to, or the NiTransformController of the <c>(controllers)</c> clip; -1
///     for file-level manager-side state.
/// </param>
/// <param name="ControlledBlock">The controlled-block ordinal within the sequence; -1 when not about a controlled block.</param>
/// <param name="Property">The channel a channel-level decision is about; null for a whole-block decision.</param>
internal readonly record struct NifModelAnimationDisposition(
    int Block,
    NifModelBlockDisposition Disposition,
    string Code,
    int SourceBlock,
    int ControlledBlock,
    SceneTransformProperty? Property)
{
    /// <summary>True when the decision is Typed.</summary>
    public bool IsTyped => Disposition.IsTyped;

    /// <summary>The NativeOnly reason; null for Typed.</summary>
    public string? Reason => Disposition.Reason;
}
