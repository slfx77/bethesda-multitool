using System.Text.Json.Nodes;
using BethesdaMultitool.Tests.Core.Modeling.RealAsset;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Starfield;

/// <summary>
///     The checked-in oracle expectations (<c>cut2-starfield-mesh-expectations.jsonl</c>): one JSON object per manifest
///     row, keyed by the payload SHA-256, written by <c>tools/scripts/gate2/starfield_mesh_cover.py expectations</c> from
///     the independent Python decoder and probe (<c>tools/scripts/gate2/starfield_mesh_probe.py</c>), which share no code
///     with BMT. Each record holds what hops A1, A2 and A3 compare: the probe verdict under BMT's bounds, the section
///     list, the coverage census, the header facts, the attribute stream names, and the SHA-256 of every typed array under
///     the reader's numeric routes (the file's first line states each digest's rule), plus the legacy-route, swapped-W and
///     stored-color control digests.
/// </summary>
/// <remarks>
///     The file is read from the repository like the manifest. A missing record, or one the generator marked
///     <c>unresolved</c>, is an unavailable fixture and skips the theory row with the reason; it is never an early return.
/// </remarks>
internal static class Cut2MeshExpectations
{
    /// <summary>The expectation file's repository-relative path.</summary>
    public const string RelativePath =
        "tests/BethesdaMultitool.Tests/Core/Modeling/Samples/cut2-starfield-mesh-expectations.jsonl";

    /// <summary>The schema every record declares.</summary>
    public const string Schema = "cut2-starfield-mesh-expectations/1";

    private static readonly Lazy<IReadOnlyDictionary<string, JsonObject>> LazyRecords = new(Load);

    /// <summary>The expectation file's absolute path, or null when it cannot be located.</summary>
    public static string? Path =>
        Cut1aCoverManifest.RepoFile(RelativePath) is { } path && File.Exists(path) ? path : null;

    /// <summary>Every record, keyed by payload SHA-256.</summary>
    public static IReadOnlyDictionary<string, JsonObject> Records => LazyRecords.Value;

    /// <summary>The record for one manifest row, or a skip when there is none or the generator could not resolve it.</summary>
    public static JsonObject Require(Cut2MeshCoverFile file)
    {
        Assert.SkipWhen(Path is null,
            $"{RelativePath} is not present; run tools/scripts/gate2/starfield_mesh_cover.py expectations to generate it.");
        var found = Records.TryGetValue(file.Sha256, out var record);
        Assert.SkipWhen(!found, $"{file}: no expectation record for SHA-256 {file.Sha256} in {RelativePath}.");
        var unresolved = record!["unresolved"];
        Assert.SkipWhen(unresolved is not null,
            $"{file}: the expectation generator could not resolve the file ({unresolved}).");
        return record;
    }

    /// <summary>Parses the expectation lines: the header line (no SHA-256) is skipped, every other line is a record.</summary>
    /// <exception cref="InvalidDataException">A record declares another schema or repeats a SHA-256.</exception>
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
                throw new InvalidDataException($"An expectation line is not schema {Schema}: {line[..Math.Min(line.Length, 120)]}");
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
