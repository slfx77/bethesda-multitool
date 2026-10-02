using System.Text.Json.Nodes;
using BethesdaMultitool.Tests.Core.Modeling.RealAsset;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Shadowkey;

/// <summary>
///     The checked-in oracle expectations (<c>cut2-shadowkey-expectations.jsonl</c>): one JSON object per manifest row,
///     keyed by SHA-256 (a slot's bytes, a zone's <c>.zmp</c>, a control's bytes; the eleven empty slots share one
///     record), written by <c>tools/scripts/gate2/shadowkey_cover.py expectations</c> from the independent Python decoder
///     of <c>shadowkey_probe.py</c>, which shares no code with BMT. The first line states every digest rule.
/// </summary>
/// <remarks>
///     A missing record is an unavailable fixture and skips the theory row with the reason; it is never an early return.
/// </remarks>
internal static class Cut2ShadowkeyExpectations
{
    /// <summary>The expectation file's repository-relative path.</summary>
    public const string RelativePath =
        "tests/BethesdaMultitool.Tests/Core/Modeling/Samples/cut2-shadowkey-expectations.jsonl";

    /// <summary>The schema every line declares.</summary>
    public const string Schema = "cut2-shadowkey-expectations/1";

    private static readonly Lazy<IReadOnlyDictionary<string, JsonObject>> LazyRecords = new(Load);

    /// <summary>The expectation file's absolute path, or null when it cannot be located.</summary>
    public static string? Path =>
        Cut1aCoverManifest.RepoFile(RelativePath) is { } path && File.Exists(path) ? path : null;

    /// <summary>Every record, keyed by SHA-256.</summary>
    public static IReadOnlyDictionary<string, JsonObject> Records => LazyRecords.Value;

    /// <summary>The record for a digest, or a skip when there is none.</summary>
    public static JsonObject Require(string sha256, string label)
    {
        Assert.SkipWhen(Path is null,
            $"{RelativePath} is not present; run tools/scripts/gate2/shadowkey_cover.py expectations to generate it.");
        var found = Records.TryGetValue(sha256, out var record);
        Assert.SkipWhen(!found, $"{label}: no expectation record for SHA-256 {sha256} in {RelativePath}.");
        return record!;
    }

    /// <summary>Parses the lines: the header line (no SHA-256) is skipped, every other line is a record.</summary>
    /// <exception cref="InvalidDataException">A line declares another schema or repeats a SHA-256.</exception>
    internal static IReadOnlyDictionary<string, JsonObject> Parse(IEnumerable<string> lines)
    {
        var records = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var record = JsonNode.Parse(line)!.AsObject();
            if (!string.Equals(record["schema"]?.GetValue<string>(), Schema, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"An expectation line is not schema {Schema}.");
            }

            if (record["sha256"]?.GetValue<string>() is not { } sha)
            {
                continue;
            }

            if (!records.TryAdd(sha, record))
            {
                throw new InvalidDataException($"The expectations repeat SHA-256 {sha}.");
            }
        }

        return records;
    }

    private static IReadOnlyDictionary<string, JsonObject> Load()
    {
        return Path is { } path ? Parse(File.ReadLines(path)) : new Dictionary<string, JsonObject>();
    }
}
