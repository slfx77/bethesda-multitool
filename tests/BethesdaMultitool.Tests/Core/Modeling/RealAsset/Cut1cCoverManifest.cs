using System.Text.Json.Nodes;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     The checked-in cut-1c cover manifest (plan <c>docs/design/cut1c-xngine-reader-plan-20260925.md</c>, section 8
///     slice 1 and section 9 "Cover"; <c>cut1c_cover_manifest.py</c> schema 1): every design member, proposed member,
///     alternate and decline or edge control of the XnGine <c>.3D</c> and Redguard <c>.3DC</c> cover, each pinned by
///     container, entry, directory index, payload size and SHA-256, and for an LZSS entry the stored size and SHA-256
///     as well. It is read from the repository like <see cref="Cut1aCoverManifest" /> (whose repository root it
///     shares).
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Parse" /> refuses a manifest that was not written by the slice-1 generator at scope cut1c with
///         payloads, an unknown role or container, a malformed SHA-256, a negative size, a zero size without a decline
///         reason, a decline reason on a row that is not a decline control (or a decline control without one), a
///         missing index on an archive row or an index on a loose row, stored members that do not come as a pair or
///         sit on a container that never compresses, a segmentType anywhere but on a ROB row or candidate (or a ROB
///         row or candidate without one), an XnGine BSA candidate without stored pins on a row that pins stored
///         bytes (an LZSS entry the resolver could never verify), a repeated (source, entry, index) identity, a
///         repeated row name, and two rows sharing a payload SHA-256 without sharing a control name (the id-5090
///         twins are the one sanctioned pair). A generator that stopped pinning any of that fails the load instead
///         of weakening a Bucket-B check.
///     </para>
///     <para>
///         <see cref="Rows" /> is the <c>MemberData</c> of every manifest-driven theory (row name for the test name,
///         plus the SHA-256); rows are keyed by NAME, not digest, because two rows may legitimately share a digest
///         (ARCH3D indices 5007 and 8903 are byte-identical) while every name is unique. The class is public because
///         xUnit resolves <c>MemberData</c> members reflectively.
///     </para>
/// </remarks>
public static class Cut1cCoverManifest
{
    /// <summary>The manifest's repository-relative path.</summary>
    public const string RelativePath = "tests/BethesdaMultitool.Tests/Core/Modeling/Samples/cut1c-cover-manifest.json";

    /// <summary>The scope the manifest must have been written at.</summary>
    public const string Scope = "cut1c";

    private const int Sha256HexLength = 64;

    private static readonly Lazy<IReadOnlyList<Cut1cCoverFile>> LazyFiles = new(Load);

    /// <summary>The manifest's absolute path, or null when it cannot be located.</summary>
    public static string? Path =>
        Cut1aCoverManifest.RepoFile(RelativePath) is { } path && File.Exists(path) ? path : null;

    /// <summary>Every manifest row in manifest order (empty when the manifest cannot be located).</summary>
    internal static IReadOnlyList<Cut1cCoverFile> Files => LazyFiles.Value;

    /// <summary>One theory row per manifest file: (row name, payload SHA-256).</summary>
    public static TheoryData<string, string> Rows()
    {
        var rows = new TheoryData<string, string>();
        foreach (var file in Files)
        {
            rows.Add(file.Name, file.Sha256);
        }

        return rows;
    }

    /// <summary>The manifest row with the given unique name.</summary>
    /// <exception cref="InvalidOperationException">No manifest row has that name.</exception>
    internal static Cut1cCoverFile Require(string name)
    {
        return Files.SingleOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal))
               ?? throw new InvalidOperationException(
                   $"The cut-1c cover manifest ({Path ?? RelativePath}) has no row named '{name}'.");
    }

    /// <summary>The one manifest row pinned for the named control (the plan's section 9 spells the names).</summary>
    /// <exception cref="InvalidOperationException">No manifest row, or more than one, carries that control.</exception>
    internal static Cut1cCoverFile RequireControl(string name)
    {
        var hits = Files.Where(f => f.Controls.Contains(name, StringComparer.Ordinal)).ToList();
        return hits.Count == 1
            ? hits[0]
            : throw new InvalidOperationException(
                $"The cut-1c cover manifest has {hits.Count} rows for the control '{name}'; expected exactly one.");
    }

    /// <summary>Every manifest row carrying the named group control, in manifest order (the id-5090 group).</summary>
    /// <exception cref="InvalidOperationException">Fewer than two rows carry that control.</exception>
    internal static IReadOnlyList<Cut1cCoverFile> RequireGroup(string name)
    {
        var hits = Files.Where(f => f.Controls.Contains(name, StringComparer.Ordinal)).ToList();
        return hits.Count >= 2
            ? hits
            : throw new InvalidOperationException(
                $"The cut-1c cover manifest has {hits.Count} rows for the group control '{name}'; expected at least two.");
    }

    /// <summary>Parses and validates a cut-1c manifest (see the remarks).</summary>
    /// <exception cref="InvalidDataException">The manifest breaks one of the rules.</exception>
    internal static IReadOnlyList<Cut1cCoverFile> Parse(string json)
    {
        var manifest = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("The manifest is empty.");
        var options = manifest["options"] as JsonObject;
        var schema = manifest["manifestSchema"]?.GetValue<int>();
        var scope = options?["scope"]?.GetValue<string>();
        var payloads = options?["payloads"]?.GetValue<bool>();
        if (schema != 1 || !string.Equals(scope, Scope, StringComparison.Ordinal) || payloads != true)
        {
            throw new InvalidDataException(
                $"The manifest was not written by cut1c_cover_manifest.py schema 1 at scope {Scope} with payloads.");
        }

        List<Cut1cCoverFile> files = [.. manifest["files"]!.AsArray().Select(node => ParseFile(node!.AsObject()))];
        Validate(files);
        return files.AsReadOnly();
    }

    private static Cut1cCoverFile ParseFile(JsonObject file)
    {
        List<Cut1cCoverSource> alsoIn =
        [
            .. file["alsoIn"]!.AsArray().Select(o => new Cut1cCoverSource(
                o!["source"]!.GetValue<string>(), o["entry"]!.GetValue<string>(), o["index"]?.GetValue<int>(),
                o["container"]!.GetValue<string>(), o["storedSize"]?.GetValue<long>(),
                o["storedSha256"]?.GetValue<string>(), o["segmentType"]?.GetValue<int>()))
        ];
        List<string> controls = [.. file["controls"]!.AsArray().Select(o => o!["name"]!.GetValue<string>())];
        return new Cut1cCoverFile(file["role"]!.GetValue<string>(), file["name"]!.GetValue<string>(),
            file["game"]!.GetValue<string>(), file["population"]!.GetValue<string>(),
            file["container"]!.GetValue<string>(), file["source"]!.GetValue<string>(),
            file["entry"]!.GetValue<string>(), file["index"]?.GetValue<int>(), file["tag"]?.GetValue<string>(),
            file["size"]!.GetValue<long>(), file["sha256"]!.GetValue<string>(), file["storedSize"]?.GetValue<long>(),
            file["storedSha256"]?.GetValue<string>(), file["segmentType"]?.GetValue<int>(), alsoIn.AsReadOnly(),
            controls.AsReadOnly(), file["declined"]?.GetValue<string>(), file["note"]!.GetValue<string>());
    }

    private static void Validate(IReadOnlyList<Cut1cCoverFile> files)
    {
        var byName = new HashSet<string>(StringComparer.Ordinal);
        var byIdentity = new HashSet<string>(StringComparer.Ordinal);
        var bySha = new Dictionary<string, List<Cut1cCoverFile>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            ValidateRow(file);
            if (!byName.Add(file.Name))
            {
                throw new InvalidDataException($"{file}: the row name '{file.Name}' appears twice.");
            }

            foreach (var candidate in file.Candidates)
            {
                ValidateCandidate(file, candidate);
                if (!byIdentity.Add($"{candidate.Source}|{candidate.Entry}|{candidate.Index}"))
                {
                    throw new InvalidDataException(
                        $"{file}: the candidate ({candidate.Source}, {candidate.Entry}, {candidate.Index}) appears twice in the manifest.");
                }
            }

            if (!bySha.TryGetValue(file.Sha256, out var list))
            {
                bySha[file.Sha256] = list = [];
            }

            list.Add(file);
        }

        foreach (var (sha, twins) in bySha.Where(pair => pair.Value.Count > 1))
        {
            var shared = twins.Aggregate((IEnumerable<string>)twins[0].Controls,
                (acc, f) => acc.Intersect(f.Controls, StringComparer.Ordinal));
            if (!shared.Any())
            {
                throw new InvalidDataException(
                    $"SHA-256 {sha} is shared by {twins.Count} rows that share no control name; " +
                    "only a pinned duplicate group may repeat a digest.");
            }
        }
    }

    private static void ValidateRow(Cut1cCoverFile file)
    {
        if (!Cut1cCoverFile.Roles.Contains(file.Role, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"{file}: role '{file.Role}' is not one of {string.Join(", ", Cut1cCoverFile.Roles)}.");
        }

        if (file.Sha256.Length != Sha256HexLength || !file.Sha256.All(char.IsAsciiHexDigitLower))
        {
            throw new InvalidDataException($"{file}: malformed SHA-256 '{file.Sha256}'.");
        }

        if (file.Size < 0 || (file.Size == 0 && string.IsNullOrEmpty(file.Declined)))
        {
            throw new InvalidDataException(
                $"{file}: size {file.Size} (an empty payload is legal only on a decline control, the empty ROB segment).");
        }

        if (file.IsDeclinedControl != !string.IsNullOrEmpty(file.Declined))
        {
            throw new InvalidDataException(
                $"{file}: a decline reason appears exactly on a decline control (role '{file.Role}', declined '{file.Declined}').");
        }

        var rob = string.Equals(file.Container, Cut1cCoverFile.RobContainer, StringComparison.Ordinal);
        if (rob != file.SegmentType.HasValue)
        {
            throw new InvalidDataException(
                $"{file}: segmentType is pinned exactly on a ROB row (container '{file.Container}', " +
                $"segmentType {file.SegmentType?.ToString() ?? "null"}).");
        }
    }

    private static void ValidateCandidate(Cut1cCoverFile file, Cut1cCoverSource candidate)
    {
        if (!Cut1cCoverFile.Containers.Contains(candidate.Container, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"{file}: container '{candidate.Container}' is not one of {string.Join(", ", Cut1cCoverFile.Containers)}.");
        }

        var loose = string.Equals(candidate.Container, Cut1cCoverFile.LooseContainer, StringComparison.Ordinal);
        if (loose == candidate.Index.HasValue)
        {
            throw new InvalidDataException(
                $"{file}: candidate {candidate.Source} carries an index exactly when it is inside an archive " +
                $"(container '{candidate.Container}', index {candidate.Index?.ToString() ?? "null"}).");
        }

        if (candidate.StoredSha256 is not null != candidate.StoredSize.HasValue)
        {
            throw new InvalidDataException(
                $"{file}: candidate {candidate.Source} pins storedSize and storedSha256 only as a pair.");
        }

        if (candidate.IsCompressed &&
            !string.Equals(candidate.Container, Cut1cCoverFile.XnGineBsaContainer, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{file}: candidate {candidate.Source} pins stored bytes on container '{candidate.Container}', " +
                "but only a named XnGine BSA entry is LZSS-compressed.");
        }

        if (candidate.SegmentType.HasValue !=
            string.Equals(candidate.Container, Cut1cCoverFile.RobContainer, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{file}: candidate {candidate.Source} pins a segmentType exactly when it is a ROB segment " +
                $"(container '{candidate.Container}', segmentType {candidate.SegmentType?.ToString() ?? "null"}).");
        }

        if (file.IsCompressed && !candidate.IsCompressed &&
            string.Equals(candidate.Container, Cut1cCoverFile.XnGineBsaContainer, StringComparison.Ordinal))
        {
            // A row that pins stored bytes is an LZSS entry, and its bytes live LZSS-compressed in every named-BSA
            // copy on the cover, so a candidate without stored pins cannot be verified before decompression; the
            // resolver refuses exactly that shape with a non-absent reason, which fails the Bucket-B theory instead
            // of skipping it. Refusing here moves a regeneration that drops the pins into the default suite
            // (review finding, 2026-09-28: the first generator draft emitted 10 such candidates on 5 rows).
            throw new InvalidDataException(
                $"{file}: candidate {candidate.Source} names an XnGine BSA entry on a row that pins stored bytes " +
                "but carries no stored pins itself; the resolver cannot verify an LZSS entry without them.");
        }
    }

    private static IReadOnlyList<Cut1cCoverFile> Load()
    {
        return Path is { } path ? Parse(File.ReadAllText(path)) : [];
    }
}
