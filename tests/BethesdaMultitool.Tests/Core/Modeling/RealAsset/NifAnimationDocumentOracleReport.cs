using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     What hop A1-anim (<see cref="NifAnimationDocumentOracle" />) found for one file: every difference between the
///     reader's typed tracks and the probe-derived expectations, and what was compared, per track kind and interpolation.
/// </summary>
internal sealed class NifAnimationDocumentOracleReport
{
    /// <summary>The number of differences a receipt row keeps.</summary>
    public const int ReceiptDiffLimit = 40;

    /// <summary>Every difference, each naming the clip, track, origin (interpolator and data block) and field.</summary>
    public List<string> Diffs { get; } = [];

    /// <summary>The clips compared.</summary>
    public int Clips { get; set; }

    /// <summary>Tracks compared, by kind and interpolation (for example <c>transform/Hermite</c>).</summary>
    public SortedDictionary<string, int> Compared { get; } = new(StringComparer.Ordinal);

    /// <summary>Typed tracks the probe carries no expectation for, by reason (expected to stay empty on the manifest).</summary>
    public SortedDictionary<string, int> NotCompared { get; } = new(StringComparer.Ordinal);

    /// <summary>Events compared bit for bit.</summary>
    public int Events { get; set; }

    /// <summary>Clip and track clocks compared bit for bit (null clocks included).</summary>
    public int Clocks { get; set; }

    /// <summary>The total number of tracks compared.</summary>
    public int ComparedTracks => Compared.Values.Sum();

    /// <summary>Counts one compared track.</summary>
    /// <param name="kind">The track kind.</param>
    /// <param name="interpolation">Its interpolation or state.</param>
    public void Count(string kind, string interpolation)
    {
        var key = kind + "/" + interpolation;
        Compared[key] = Compared.TryGetValue(key, out var seen) ? seen + 1 : 1;
    }

    /// <summary>Counts one typed track that could not be compared.</summary>
    /// <param name="reason">Why.</param>
    public void Skip(string reason)
    {
        NotCompared[reason] = NotCompared.TryGetValue(reason, out var seen) ? seen + 1 : 1;
    }

    /// <summary>A multi-line summary with the first differences, for assertion messages.</summary>
    /// <param name="subject">The file (or other subject) the report is about.</param>
    /// <returns>The text.</returns>
    public string Describe(object subject)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture,
            $"{subject}: {Clips} clips, {ComparedTracks} tracks, {Events} events, {Clocks} clocks compared; " +
            $"{Diffs.Count} differences");
        foreach (var (reason, count) in NotCompared)
        {
            builder.Append(CultureInfo.InvariantCulture, $"\n  not compared: {reason} x{count}");
        }

        foreach (var diff in Diffs.Take(ReceiptDiffLimit))
        {
            builder.Append("\n  ").Append(diff);
        }

        return builder.ToString();
    }

    /// <summary>The report as a receipt object (the first <see cref="ReceiptDiffLimit" /> differences).</summary>
    /// <returns>A new object.</returns>
    public JsonObject ToJson()
    {
        var compared = new JsonObject();
        foreach (var (key, count) in Compared)
        {
            compared[key] = count;
        }

        var notCompared = new JsonObject();
        foreach (var (key, count) in NotCompared)
        {
            notCompared[key] = count;
        }

        return new JsonObject
        {
            ["clips"] = Clips,
            ["comparedTracks"] = ComparedTracks,
            ["compared"] = compared,
            ["notCompared"] = notCompared,
            ["events"] = Events,
            ["clocks"] = Clocks,
            ["diffCount"] = Diffs.Count,
            ["diffs"] = new JsonArray(Diffs.Take(ReceiptDiffLimit).Select(static d => (JsonNode?)JsonValue.Create(d))
                .ToArray())
        };
    }
}
