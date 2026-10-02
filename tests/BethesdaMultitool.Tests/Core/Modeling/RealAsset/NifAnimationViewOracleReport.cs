using System.Globalization;
using System.Text;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     What <see cref="NifAnimationViewOracle" /> compared in one file and every difference it found. The counts let a test
///     prove the comparison covered the payloads it claims to (a comparison that compared nothing must not pass).
/// </summary>
internal sealed class NifAnimationViewOracleReport
{
    /// <summary>Every difference, with the block, member path, expected and actual value.</summary>
    public List<string> Diffs { get; } = [];

    /// <summary>The number of payloads the expectation record carries.</summary>
    public int ExpectedPayloads { get; set; }

    /// <summary>The number of payloads a view rendered and compared.</summary>
    public int ComparedPayloads { get; set; }

    /// <summary>The number of animation-fact blocks the expectation record carries.</summary>
    public int ExpectedAnimationBlocks { get; set; }

    /// <summary>The number of animation-fact blocks a view rendered and compared.</summary>
    public int ComparedAnimationBlocks { get; set; }

    /// <summary>The number of TBC keys among the compared payloads.</summary>
    public int TbcKeys { get; set; }

    /// <summary>The number of those TBC keys whose continuity and bias bits differ (the keys a TBC order swap can reach).</summary>
    public int TbcKeysWithContinuityNotBias { get; set; }

    /// <summary>Animation-fact block types outside slice 1, by type, with their counts (reported, not compared).</summary>
    public SortedDictionary<string, int> NotCompared { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     Controller type-specific members outside slice 1 (only the NiTimeController header is compared), keyed
    ///     type.member, with the number of controllers carrying each (reported, not compared).
    /// </summary>
    public SortedDictionary<string, int> NotComparedControllerMembers { get; } = new(StringComparer.Ordinal);

    /// <summary>A failure message: the counts, the types not compared, and the first differences.</summary>
    public string Describe(object file)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"{file}: {Diffs.Count} differences; payloads {ComparedPayloads}/{ExpectedPayloads}, animation blocks {ComparedAnimationBlocks}/{ExpectedAnimationBlocks}, TBC keys {TbcKeys} ({TbcKeysWithContinuityNotBias} with continuity != bias)");
        if (NotCompared.Count > 0)
        {
            text.Append("; not compared: ")
                .Append(string.Join(", ", NotCompared.Select(static pair =>
                    pair.Key + " x" + pair.Value.ToString(CultureInfo.InvariantCulture))));
        }

        if (NotComparedControllerMembers.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"; controller type-specific members not compared: {NotComparedControllerMembers.Values.Sum()} ({NotComparedControllerMembers.Count} distinct type.member)");
        }

        foreach (var diff in Diffs.Take(25))
        {
            text.Append('\n').Append(diff);
        }

        return text.ToString();
    }
}
