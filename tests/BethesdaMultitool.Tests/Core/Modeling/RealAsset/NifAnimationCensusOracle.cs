using System.Reflection;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using BethesdaMultitool.Core.Modeling.Nif;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Hop A2-anim (plan section 3, slice 9): the header block census of a file against the animation coverage
///     classification (<see cref="NifModelAnimationCoverage.ClassifyFile" />). Every block the probe's header table names
///     with an animation type (<see cref="NifModelAnimationCoverage.IsAnimationType" />) lands in exactly one
///     classification, no other block is classified, the classification's kind is Typed or NativeOnly (the classifier
///     emits no Dropped rows), and its code is one of <see cref="AllowedCodes" />: the plan section 2.1 table rows and the
///     reader's own typed and native decisions.
/// </summary>
internal static class NifAnimationCensusOracle
{
    private static readonly Lazy<IReadOnlySet<string>> LazyAllowedCodes = new(BuildAllowedCodes);

    /// <summary>
    ///     The codes a classification may carry: every <c>*Code</c> constant of <see cref="NifModelAnimationReasons" /> and
    ///     <see cref="NifModelAnimationCoverage" /> (the reader's decisions and the table rows), the code of every
    ///     <see cref="NifModelCurveBlock" />, <see cref="NifModelTextKeyBlock" /> and <see cref="NifModelTargetBlock" />
    ///     refusal, and <see cref="NifModelAnimationMorphs.NoMorphTargetsCode" />.
    /// </summary>
    public static IReadOnlySet<string> AllowedCodes => LazyAllowedCodes.Value;

    /// <summary>Compares one file's classifications with the probe's header block table.</summary>
    /// <param name="schema">The nif.xml definitions.</param>
    /// <param name="stateBlockTypes">The read state's block type names, in block order.</param>
    /// <param name="expectation">The probe record (its <c>blockTypeNames</c> and <c>blockTypeIndices</c>).</param>
    /// <param name="classifications">The classifications under test.</param>
    /// <returns>Every difference (empty when the census holds).</returns>
    public static List<string> Check(NifSchema schema, IReadOnlyList<string> stateBlockTypes, JsonObject expectation,
        IReadOnlyList<NifModelAnimationClassification> classifications)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(stateBlockTypes);
        ArgumentNullException.ThrowIfNull(expectation);
        ArgumentNullException.ThrowIfNull(classifications);
        var diffs = new List<string>();
        if (expectation["blockTypeNames"] is not JsonArray names ||
            expectation["blockTypeIndices"] is not JsonArray indices)
        {
            diffs.Add("the probe record carries no header block table");
            return diffs;
        }

        if (indices.Count != stateBlockTypes.Count)
        {
            diffs.Add($"the probe's header names {indices.Count} blocks, the read state {stateBlockTypes.Count}");
            return diffs;
        }

        var byBlock = new Dictionary<int, List<NifModelAnimationClassification>>();
        foreach (var classification in classifications)
        {
            if (!byBlock.TryGetValue(classification.Block, out var list))
            {
                list = [];
                byBlock.Add(classification.Block, list);
            }

            list.Add(classification);
        }

        for (var block = 0; block < indices.Count; block++)
        {
            var type = names[indices[block]!.GetValue<int>()]!.GetValue<string>();
            if (!string.Equals(type, stateBlockTypes[block], StringComparison.Ordinal))
            {
                diffs.Add($"block {block}: the probe's header says {type}, the read state {stateBlockTypes[block]}");
                continue;
            }

            var count = byBlock.TryGetValue(block, out var found) ? found.Count : 0;
            if (!NifModelAnimationCoverage.IsAnimationType(schema, type))
            {
                if (count != 0)
                {
                    diffs.Add($"block {block} ({type}): not an animation block, classified {count} times");
                }

                continue;
            }

            if (count != 1)
            {
                diffs.Add(count == 0
                    ? $"block {block} ({type}): unclassified"
                    : $"block {block} ({type}): classified {count} times");
                continue;
            }

            var only = found![0];
            if (only.Kind == ModelSourceCoverageKind.Dropped)
            {
                diffs.Add($"block {block} ({type}): Dropped, which the classifier never emits");
            }
            else if (only.IsTyped
                         ? !string.Equals(only.Code, NifModelAnimationReasons.TypedCode, StringComparison.Ordinal)
                         : string.IsNullOrWhiteSpace(only.Reason) || !AllowedCodes.Contains(only.Code))
            {
                diffs.Add($"block {block} ({type}): {only.Kind} with code '{only.Code}' and reason '{only.Reason}', " +
                          "outside the plan section 2.1 list and the reader's decisions");
            }
        }

        foreach (var block in byBlock.Keys.Where(block => block < 0 || block >= indices.Count))
        {
            diffs.Add($"block {block}: classified, but the header has {indices.Count} blocks");
        }

        return diffs;
    }

    /// <summary>
    ///     The table-completeness check: every census type has a table row with a code and a reason, through the given
    ///     table (the classifier's own <see cref="NifModelAnimationCoverage.TableRow" />, or a control's reduced copy).
    /// </summary>
    /// <param name="types">The census types.</param>
    /// <param name="table">The table under test.</param>
    /// <returns>The census types without a row.</returns>
    public static List<string> MissingRows(IEnumerable<string> types, Func<string, (string Reason, string Code)?> table)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(table);
        return types.Where(type => table(type) is not { } row || string.IsNullOrWhiteSpace(row.Code) ||
                                   string.IsNullOrWhiteSpace(row.Reason))
            .ToList();
    }

    private static IReadOnlySet<string> BuildAllowedCodes()
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in new[] { typeof(NifModelAnimationReasons), typeof(NifModelAnimationCoverage) })
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.IsLiteral && field.FieldType == typeof(string) &&
                    field.Name.EndsWith("Code", StringComparison.Ordinal) &&
                    field.GetRawConstantValue() is string code)
                {
                    codes.Add(code);
                }
            }
        }

        foreach (var block in Enum.GetValues<NifModelCurveBlock>().Where(static b => b != NifModelCurveBlock.None))
        {
            codes.Add(NifModelAnimationReasons.Code(block));
        }

        foreach (var block in Enum.GetValues<NifModelTextKeyBlock>().Where(static b => b != NifModelTextKeyBlock.None))
        {
            codes.Add(NifModelAnimationReasons.Code(block));
        }

        foreach (var block in Enum.GetValues<NifModelTargetBlock>().Where(static b => b != NifModelTargetBlock.None))
        {
            codes.Add(NifModelTargetNames.Code(block));
        }

        codes.Add(NifModelAnimationMorphs.NoMorphTargetsCode);
        return codes;
    }
}
