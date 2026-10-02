using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     The checked-in cut-1b oracle expectations (<c>cut1b-probe-expectations.jsonl.gz</c>): one JSON object per
///     cut-1b manifest file, keyed by the manifest SHA-256, written by
///     <c>tools/scripts/nif_cover_expectations.py --scope cut1b --payloads</c> from the independent Python probe
///     (<c>tools/scripts/nif_feature_probe.py</c>). Besides the cut-1a record members (header block table, decline
///     reason, parsed field values, textures) each record holds <c>payloads</c> (every decoded key group, B-spline array
///     and interpolator payload) and <c>animation</c> (every controller, interpolator, sequence, palette, text-key and
///     key-data block's fields): what hop A0 compares. Every decoded float is carried ONLY as its IEEE-754 binary32
///     pattern (<c>&lt;name&gt;Bits</c>); the generator drops the redundant float value, which the bits give exactly.
/// </summary>
/// <remarks>
///     <para>
///         The file is gzip-compressed (about 5 MB on disk, about 30 MB of JSON lines), so it is decompressed once into
///         memory and indexed by the SHA-256 the generator writes as each line's first member; <see cref="Require" />
///         and <see cref="Headers" /> parse one record at a time from that buffer. A record not generated at scope cut1b with payloads
///         (<c>probeScope</c>, <c>probePayloads</c>) is refused with <see cref="InvalidDataException" />, so
///         expectations generated for another scope fail loudly instead of comparing against the wrong walk.
///     </para>
///     <para>
///         A missing record, or one the generator marked <c>unresolved</c>, is an unavailable fixture and skips the
///         theory row with the reason; it is never an early return.
///     </para>
/// </remarks>
internal static class Cut1bProbeExpectations
{
    /// <summary>The expectation file's repository-relative path.</summary>
    public const string RelativePath =
        "tests/BethesdaMultitool.Tests/Core/Modeling/Samples/cut1b-probe-expectations.jsonl.gz";

    /// <summary>The probe scope every record must have been generated at.</summary>
    public const string Scope = "cut1b";

    private const int Sha256HexLength = 64;

    private static readonly Lazy<LineIndex?> LazyIndex = new(LoadIndex);

    /// <summary>True when the expectation file is present in the checkout.</summary>
    public static bool IsPresent => LazyIndex.Value is not null;

    /// <summary>The SHA-256 of every record, in file order (empty when the file is not present).</summary>
    public static IReadOnlyList<string> Digests => LazyIndex.Value?.Order ?? [];

    /// <summary>The record prefix every generated line starts with, ahead of the 64 hex digits.</summary>
    private static ReadOnlySpan<byte> ShaPrefix => "{\"sha256\":\""u8;

    /// <summary>The record for one manifest file, or a skip when there is none or the generator could not resolve it.</summary>
    public static JsonObject Require(Cut1bCoverFile file)
    {
        var index = LazyIndex.Value;
        Assert.SkipWhen(index is null,
            $"{RelativePath} is not present; run tools/scripts/nif_cover_expectations.py --scope cut1b --payloads " +
            "to generate it.");
        var found = index!.Lines.TryGetValue(file.Sha256, out var line);
        Assert.SkipWhen(!found, $"{file}: no expectation record for SHA-256 {file.Sha256} in {RelativePath}.");
        var record = Parse(index.Bytes.AsSpan(line.Start, line.Length));
        var unresolved = record["unresolved"];
        Assert.SkipWhen(unresolved is not null,
            $"{file}: the expectation generator could not resolve the file, so there is nothing to compare against " +
            $"({unresolved}).");
        return record;
    }

    /// <summary>
    ///     The scope members of every record, one record parsed at a time: (SHA-256, <c>probeScope</c>,
    ///     <c>probePayloads</c>).
    /// </summary>
    public static IEnumerable<(string Sha256, string? Scope, bool? Payloads)> Headers()
    {
        if (LazyIndex.Value is not { } index)
        {
            yield break;
        }

        foreach (var sha in index.Order)
        {
            var (start, length) = index.Lines[sha];
            using var document = JsonDocument.Parse(index.Bytes.AsMemory(start, length));
            var root = document.RootElement;
            string? scope = root.TryGetProperty("probeScope", out var s) ? s.GetString() : null;
            bool? payloads = root.TryGetProperty("probePayloads", out var p) ? p.GetBoolean() : null;
            yield return (sha, scope, payloads);
        }
    }

    /// <summary>A deep copy of a record for a control mutation.</summary>
    public static JsonObject Clone(JsonObject record)
    {
        return record.DeepClone().AsObject();
    }

    /// <summary>One record, refused unless it was generated at scope cut1b with payloads.</summary>
    /// <exception cref="InvalidDataException">The record names another scope or no payloads.</exception>
    internal static JsonObject Parse(ReadOnlySpan<byte> utf8Line)
    {
        var record = JsonNode.Parse(utf8Line)!.AsObject();
        var scope = record["probeScope"]?.GetValue<string>();
        var payloads = record["probePayloads"]?.GetValue<bool>();
        if (!string.Equals(scope, Scope, StringComparison.Ordinal) || payloads != true)
        {
            throw new InvalidDataException(
                $"{RelativePath}: record {record["sha256"]} was generated at scope '{scope ?? "cut1a"}' with payloads " +
                $"{payloads?.ToString() ?? "off"}; the cut-1b oracle needs --scope {Scope} --payloads.");
        }

        return record;
    }

    private static LineIndex? LoadIndex()
    {
        if (Cut1aCoverManifest.RepoFile(RelativePath) is not { } path || !File.Exists(path))
        {
            return null;
        }

        // Decompress once and keep the JSON lines (see the remarks); records are parsed from this buffer on demand.
        byte[] bytes;
        using (var file = File.OpenRead(path))
        using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        using (var buffer = new MemoryStream())
        {
            gzip.CopyTo(buffer);
            bytes = buffer.ToArray();
        }

        var lines = new Dictionary<string, (int Start, int Length)>(StringComparer.Ordinal);
        var order = new List<string>();
        var start = 0;
        while (start < bytes.Length)
        {
            var end = Array.IndexOf(bytes, (byte)'\n', start);
            if (end < 0)
            {
                end = bytes.Length;
            }

            var length = end - start;
            if (length > 0)
            {
                var sha = ReadSha(bytes.AsSpan(start, length));
                if (!lines.TryAdd(sha, (start, length)))
                {
                    throw new InvalidDataException($"{RelativePath}: two records for SHA-256 {sha}.");
                }

                order.Add(sha);
            }

            start = end + 1;
        }

        return new LineIndex(bytes, lines, order.AsReadOnly());
    }

    /// <summary>
    ///     The record's SHA-256 without parsing the (up to megabytes long) record: the generator writes it as the first
    ///     member. Any other layout falls back to a full parse of that one line.
    /// </summary>
    private static string ReadSha(ReadOnlySpan<byte> line)
    {
        var prefix = ShaPrefix;
        if (line.StartsWith(prefix) && line.Length > prefix.Length + Sha256HexLength &&
            line[prefix.Length + Sha256HexLength] == (byte)'"')
        {
            return Encoding.ASCII.GetString(line.Slice(prefix.Length, Sha256HexLength));
        }

        return JsonNode.Parse(line)!["sha256"]!.GetValue<string>();
    }

    private sealed record LineIndex(
        byte[] Bytes,
        IReadOnlyDictionary<string, (int Start, int Length)> Lines,
        IReadOnlyList<string> Order);
}
