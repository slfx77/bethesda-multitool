using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 2, SA7 (owner ruling D11: typed fields, not extras that writers drop): the source policies a NIF
///     animation declares, onto Shared's <see cref="SceneAnimationTrackSourcePolicy" /> (a controlled block's Priority)
///     and <see cref="SceneAnimationSourcePolicy" /> (a sequence's Weight and Accum Root Name). Pure. Every value is one
///     the file stores, with provenance <see cref="SceneValueProvenance.Authored" />; an absent declaration stays absent.
/// </summary>
internal static class NifModelSourcePolicyMapping
{
    /// <summary>The evidence recorded with a controlled block's priority.</summary>
    public const string PriorityEvidence = "NiControllerSequence controlled block Priority byte";

    /// <summary>The evidence recorded with a sequence's weight.</summary>
    public const string SequenceEvidence = "NiControllerSequence Weight (Float32 as stored)";

    /// <summary>The evidence recorded with a sequence's accumulation root.</summary>
    public const string AccumulationRootEvidence = "NiControllerSequence Accum Root Name (Latin-1 of the stored bytes)";

    /// <summary>The per-track policy of one controlled block: its Priority byte, or null when the stream stores none.</summary>
    /// <param name="block">The controlled block view.</param>
    /// <returns>The policy, or null (no priority is invented).</returns>
    public static SceneAnimationTrackSourcePolicy? MapTrack(NifControlledBlockView block)
    {
        return block.Priority is { } priority
            ? new SceneAnimationTrackSourcePolicy(priority, SceneValueProvenance.Authored, PriorityEvidence)
            : null;
    }

    /// <summary>
    ///     The per-sequence policy: the stored Weight and, when the sequence names one, its Accum Root Name as the
    ///     Latin-1 text of the stored bytes (no trim, no case folding).
    /// </summary>
    /// <param name="sequence">The sequence view.</param>
    /// <param name="strings">The file's raw header string table.</param>
    /// <param name="accumulationRootNodeIndex">
    ///     The exact document node the name resolves to, when the caller has resolved it; null leaves the root unbound.
    /// </param>
    /// <returns>The sequence's source policy.</returns>
    /// <exception cref="InvalidDataException">The weight is not finite, or the name index is outside the string table.</exception>
    /// <exception cref="ArgumentException">A node index is supplied for a sequence that names no accumulation root.</exception>
    public static SceneAnimationSourcePolicy MapSequence(
        NifControllerSequenceView sequence, NifHeaderStringTable strings, int? accumulationRootNodeIndex = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(strings);
        var weight = BitConverter.UInt32BitsToSingle(sequence.WeightBits);
        if (!float.IsFinite(weight))
        {
            throw new InvalidDataException($"The sequence weight 0x{sequence.WeightBits:X8} is not finite.");
        }

        if (!NifAnimationStrings.TryGetLatin1(strings, sequence.AccumRootNameIndex, out var name))
        {
            throw new InvalidDataException(
                $"The Accum Root Name index {sequence.AccumRootNameIndex} is outside the {strings.Count}-entry string table.");
        }

        if (name is null && accumulationRootNodeIndex is not null)
        {
            throw new ArgumentException(
                "A resolved accumulation-root node needs the stored name it was resolved from.",
                nameof(accumulationRootNodeIndex));
        }

        var root = name is null
            ? null
            : new SceneAnimationAccumulationRoot(accumulationRootNodeIndex, name, SceneValueProvenance.Authored,
                AccumulationRootEvidence);
        return new SceneAnimationSourcePolicy(weight, root, SceneValueProvenance.Authored, SequenceEvidence);
    }
}
