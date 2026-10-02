using System.Text.Json.Nodes;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     The checked-in oracle expectations (<c>cut1a-probe-expectations.jsonl</c>): one JSON object per manifest file,
///     keyed by the manifest SHA-256, written by <c>tools/scripts/nif_cover_expectations.py</c> from the independent
///     Python probe (<c>tools/scripts/nif_feature_probe.py</c>, oracle A1) and its BSA reader (oracle A4). Each record
///     holds only what the hops compare: the header block table and the probe's decline reason (A2), the probe's parsed
///     field values per block (A1) and every authored texture's resolved path and SHA-256 in the build's Data folder (A4).
/// </summary>
/// <remarks>
///     The file is read from the repository like the manifest. A missing record, or a record the generator marked
///     <c>unresolved</c> (it could not reproduce the manifest digest on the machine that generated the file), is an
///     unavailable fixture and skips the theory row with the reason; it is never an early return.
/// </remarks>
internal static class Cut1aProbeExpectations
{
    /// <summary>The expectation file's repository-relative path.</summary>
    public const string RelativePath =
        "tests/BethesdaMultitool.Tests/Core/Modeling/Samples/cut1a-probe-expectations.jsonl";

    private static readonly Lazy<IReadOnlyDictionary<string, JsonObject>> LazyRecords = new(Load);

    /// <summary>The record for one manifest file, or a skip when there is none or the generator could not resolve it.</summary>
    public static JsonObject Require(Cut1aCoverFile file)
    {
        var path = Cut1aCoverManifest.RepoFile(RelativePath);
        Assert.SkipWhen(path is null || !File.Exists(path),
            $"{RelativePath} is not present; run tools/scripts/nif_cover_expectations.py to generate it.");
        var found = LazyRecords.Value.TryGetValue(file.Sha256, out var record);
        Assert.SkipWhen(!found, $"{file}: no expectation record for SHA-256 {file.Sha256} in {RelativePath}.");
        var unresolved = record!["unresolved"];
        Assert.SkipWhen(unresolved is not null,
            $"{file}: the expectation generator could not resolve the file, so there is nothing to compare against " +
            $"({unresolved}).");
        return record;
    }

    /// <summary>A deep copy of a record for a control mutation.</summary>
    public static JsonObject Clone(JsonObject record)
    {
        return record.DeepClone().AsObject();
    }

    private static IReadOnlyDictionary<string, JsonObject> Load()
    {
        var records = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (Cut1aCoverManifest.RepoFile(RelativePath) is not { } path || !File.Exists(path))
        {
            return records;
        }

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var record = JsonNode.Parse(line)!.AsObject();
            records[record["sha256"]!.GetValue<string>()] = record;
        }

        return records;
    }
}
