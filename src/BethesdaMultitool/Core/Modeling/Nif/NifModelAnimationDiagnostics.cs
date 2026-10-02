using System.Globalization;
using System.Text;
using Slfx77.Multitool.Core.Documents;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 10: the document diagnostics of the animation stage (the info diagnostic plan section 4 row 8 names),
///     bounded rows of raw text: one row summarizing the animation blocks kept as native state, grouped by reason, and,
///     for a <c>.kf</c>, one row naming the target names its resolved skeleton does not bind. Nothing is emitted when
///     nothing was kept native, so a file without animation or with fully typed animation carries no new row. Blocks the
///     plan 2.1 table gives to another later cut (Havok, particles, cameras and lights) are not animation-stage losses and
///     are left to the coverage rows.
/// </summary>
internal static class NifModelAnimationDiagnostics
{
    /// <summary>The code of the native-blocks summary.</summary>
    public const string NativeBlocksDiagnostic = "bmt.nif.animation.native";

    /// <summary>The code of the unbound-target-names row of a <c>.kf</c>.</summary>
    public const string SkeletonDiagnostic = "bmt.nif.animation.skeleton";

    /// <summary>The most reasons the summary names; the rest are counted.</summary>
    public const int MaximumListedReasons = 8;

    /// <summary>The most block indices named per reason; the rest are counted.</summary>
    public const int MaximumListedBlocks = 4;

    /// <summary>The most unbound names the skeleton row names; the rest are counted.</summary>
    public const int MaximumListedNames = 8;

    /// <summary>The table-row codes of other later cuts, which the summary leaves to the coverage rows.</summary>
    private static readonly HashSet<string> OtherCategoryCodes = new(StringComparer.Ordinal)
    {
        NifModelAnimationCoverage.HavokCode, NifModelAnimationCoverage.ParticlesCode,
        NifModelAnimationCoverage.CameraLightCode
    };

    /// <summary>
    ///     The native-blocks summary of one file: the clip count and every NativeOnly animation classification outside the
    ///     other later cuts, grouped by reason (largest group first, then first block); empty when there is none.
    /// </summary>
    /// <param name="clipCount">The number of clips the stage typed.</param>
    /// <param name="classifications">The file's animation classifications (<see cref="NifModelAnimationCoverage.ClassifyFile" />).</param>
    /// <returns>Zero or one diagnostic.</returns>
    public static IReadOnlyList<SceneDiagnostic> ForClassifications(int clipCount,
        IReadOnlyList<NifModelAnimationClassification> classifications)
    {
        ArgumentNullException.ThrowIfNull(classifications);
        ArgumentOutOfRangeException.ThrowIfNegative(clipCount);
        var native = classifications.Where(static classification =>
            !classification.IsTyped && !OtherCategoryCodes.Contains(classification.Code)).ToList();
        if (native.Count == 0)
        {
            return [];
        }

        var groups = native
            .GroupBy(static classification => classification.Reason!, StringComparer.Ordinal)
            .Select(static group => (Reason: group.Key, Blocks: group.Select(static c => c.Block).Order().ToList()))
            .OrderByDescending(static group => group.Blocks.Count)
            .ThenBy(static group => group.Blocks[0])
            .ToList();
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"The animation stage typed {clipCount} clip(s) and kept {native.Count} animation block(s) as native state: ");
        for (var index = 0; index < Math.Min(groups.Count, MaximumListedReasons); index++)
        {
            var (reason, blocks) = groups[index];
            if (index > 0)
            {
                text.Append("; ");
            }

            text.Append(CultureInfo.InvariantCulture, $"'{reason}' on {blocks.Count} block(s) (");
            text.AppendJoin(", ", blocks.Take(MaximumListedBlocks).Select(static block =>
                block.ToString(CultureInfo.InvariantCulture)));
            if (blocks.Count > MaximumListedBlocks)
            {
                text.Append(CultureInfo.InvariantCulture, $", {blocks.Count - MaximumListedBlocks} more");
            }

            text.Append(')');
        }

        if (groups.Count > MaximumListedReasons)
        {
            text.Append(CultureInfo.InvariantCulture, $"; {groups.Count - MaximumListedReasons} more reason(s)");
        }

        text.Append(". See the bmt.nif.animation.clip and bmt.nif.block native state.");
        return [new SceneDiagnostic(NativeBlocksDiagnostic, DocumentText.Raw(text.ToString()))];
    }

    /// <summary>
    ///     The unbound-target-names row of a <c>.kf</c>: which names the resolved skeleton does not bind, each with its
    ///     typed reason; empty when every name bound.
    /// </summary>
    /// <param name="provenance">The skeleton provenance of the read.</param>
    /// <returns>Zero or one diagnostic.</returns>
    public static IReadOnlyList<SceneDiagnostic> ForSkeleton(NifModelSkeletonProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        if (provenance.Unmatched.Count == 0)
        {
            return [];
        }

        var total = provenance.MatchedNames.Count + provenance.Unmatched.Count;
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"The skeleton '{provenance.Path}' ({provenance.RuleName}) binds {provenance.MatchedNames.Count} of " +
            $"{total} target name(s); the tracks of the others stay native: ");
        text.AppendJoin(", ", provenance.Unmatched.Take(MaximumListedNames)
            .Select(static match => $"'{match.Name}' ({match.Reason})"));
        if (provenance.Unmatched.Count > MaximumListedNames)
        {
            text.Append(CultureInfo.InvariantCulture, $", {provenance.Unmatched.Count - MaximumListedNames} more");
        }

        text.Append('.');
        return [new SceneDiagnostic(SkeletonDiagnostic, DocumentText.Raw(text.ToString()))];
    }
}
