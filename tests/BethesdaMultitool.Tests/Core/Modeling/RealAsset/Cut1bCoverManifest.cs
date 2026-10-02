using System.Text.Json.Nodes;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     The checked-in cut-1b cover manifest (plan <c>docs/design/cut1b-nif-animation-reader-plan-20260925.md</c>, slice
///     0; <c>nif_cover.py cover --scope cut1b --payloads</c>, schema 2): the proved-minimum cover of the animation
///     vocabulary per version key and file kind, the decline controls, the skeleton companion of every manifest
///     <c>.kf</c> and every named control of the plan, each file pinned by SHA-256. It is read from the repository
///     like <see cref="Cut1aCoverManifest" /> (whose repository root it shares).
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Parse" /> refuses a manifest that was not written at scope cut1b with payloads, a file whose role
///         is not one of <see cref="Cut1bCoverFile.Roles" />, a malformed or repeated SHA-256, and a <c>.kf</c> that
///         needs a skeleton but names none (and says nothing about why) or names one that is not a manifest file listing
///         it back in <c>skeletonFor</c>. A generator that stopped pinning skeletons therefore fails the load instead of
///         leaving the skeleton hop without fixtures.
///     </para>
///     <para>
///         It also refuses a provisional skeleton pin with no alternative (or alternatives on a pin that is not
///         provisional), an alternative that is not a skeleton-role manifest file listing the <c>.kf</c> in
///         <c>skeletonCandidateFor</c>, a skeleton-role file that no <c>.kf</c> lists, and a decline control without the
///         probe's decline reason, so both ways of adding a decline control keep one shape.
///     </para>
///     <para>
///         <see cref="Rows" /> is the <c>MemberData</c> of every manifest-driven theory (entry for the test name,
///         SHA-256 as the key); <see cref="AnimationStreamRows" /> is the same over the <c>.kf</c> that need a skeleton.
///         The class is public because xUnit resolves <c>MemberData</c> members reflectively.
///     </para>
/// </remarks>
public static class Cut1bCoverManifest
{
    /// <summary>The manifest's repository-relative path.</summary>
    public const string RelativePath = "tests/BethesdaMultitool.Tests/Core/Modeling/Samples/cut1b-cover-manifest.json";

    /// <summary>The scope the manifest must have been written at.</summary>
    public const string Scope = "cut1b";

    private const int Sha256HexLength = 64;

    private static readonly Lazy<IReadOnlyList<Cut1bCoverFile>> LazyFiles = new(Load);

    /// <summary>The manifest's absolute path, or null when it cannot be located.</summary>
    public static string? Path =>
        Cut1aCoverManifest.RepoFile(RelativePath) is { } path && File.Exists(path) ? path : null;

    /// <summary>Every manifest file in manifest order (empty when the manifest cannot be located).</summary>
    internal static IReadOnlyList<Cut1bCoverFile> Files => LazyFiles.Value;

    /// <summary>One theory row per manifest file: (entry, SHA-256).</summary>
    public static TheoryData<string, string> Rows()
    {
        var rows = new TheoryData<string, string>();
        foreach (var file in Files)
        {
            rows.Add(file.Entry, file.Sha256);
        }

        return rows;
    }

    /// <summary>One theory row per manifest <c>.kf</c> that needs a skeleton: (entry, SHA-256).</summary>
    public static TheoryData<string, string> AnimationStreamRows()
    {
        var rows = new TheoryData<string, string>();
        foreach (var file in Files)
        {
            if (file.NeedsSkeleton)
            {
                rows.Add(file.Entry, file.Sha256);
            }
        }

        return rows;
    }

    /// <summary>The manifest file with the given SHA-256.</summary>
    /// <exception cref="InvalidOperationException">No manifest file has that digest.</exception>
    internal static Cut1bCoverFile Require(string sha256)
    {
        return Files.SingleOrDefault(f => string.Equals(f.Sha256, sha256, StringComparison.Ordinal))
               ?? throw new InvalidOperationException(
                   $"The cut-1b cover manifest ({Path ?? RelativePath}) has no file with SHA-256 {sha256}.");
    }

    /// <summary>The manifest file pinned for the named control (the plan's control table spells the names).</summary>
    /// <exception cref="InvalidOperationException">No manifest file, or more than one, carries that control.</exception>
    internal static Cut1bCoverFile RequireControl(string name)
    {
        var hits = Files.Where(f => f.Controls.Contains(name, StringComparer.Ordinal)).ToList();
        return hits.Count == 1
            ? hits[0]
            : throw new InvalidOperationException(
                $"The cut-1b cover manifest has {hits.Count} files for the control '{name}'; expected exactly one.");
    }

    /// <summary>Parses and validates a cut-1b manifest (see the remarks).</summary>
    /// <exception cref="InvalidDataException">The manifest breaks one of the rules.</exception>
    internal static IReadOnlyList<Cut1bCoverFile> Parse(string json)
    {
        var manifest = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("The manifest is empty.");
        var options = manifest["options"] as JsonObject;
        var schema = manifest["manifestSchema"]?.GetValue<int>();
        var scope = options?["scope"]?.GetValue<string>();
        var payloads = options?["payloads"]?.GetValue<bool>();
        if (schema != 2 || !string.Equals(scope, Scope, StringComparison.Ordinal) || payloads != true)
        {
            throw new InvalidDataException(
                $"The manifest was not written by nif_cover.py schema 2 at --scope {Scope} --payloads.");
        }

        var files = new List<Cut1bCoverFile>();
        foreach (var key in manifest["keys"]!.AsArray())
        {
            var keyName = key!["key"]!.GetValue<string>();
            foreach (var node in key["files"]!.AsArray())
            {
                files.Add(ParseFile(keyName, node!.AsObject()));
            }
        }

        Validate(files);
        return files.AsReadOnly();
    }

    private static Cut1bCoverFile ParseFile(string keyName, JsonObject file)
    {
        List<Cut1bCoverSource> alsoIn =
        [
            .. file["alsoIn"]!.AsArray()
                .Select(o => new Cut1bCoverSource(o!["source"]!.GetValue<string>(), o["entry"]!.GetValue<string>()))
        ];
        List<string> controls = file["controls"] is JsonArray named
            ? [.. named.Select(o => o!["name"]!.GetValue<string>())]
            : [];
        Cut1bSkeletonCompanion? skeleton = null;
        var hasSkeletonMember = file.ContainsKey("skeleton");
        if (file["skeleton"] is JsonObject companion)
        {
            List<Cut1bSkeletonAlternative> alternatives = companion["alternatives"] is JsonArray others
                ?
                [
                    .. others.Select(o => new Cut1bSkeletonAlternative(o!["sha256"]!.GetValue<string>(),
                        o["source"]!.GetValue<string>(), o["entry"]!.GetValue<string>(),
                        o["gamePlatform"]!.GetValue<string>()))
                ]
                : [];
            skeleton = new Cut1bSkeletonCompanion(companion["sha256"]!.GetValue<string>(),
                companion["source"]!.GetValue<string>(), companion["entry"]!.GetValue<string>(),
                companion["gamePlatform"]!.GetValue<string>(), companion["rule"]!.GetValue<string>(),
                companion["provisional"]?.GetValue<string>(), alternatives.AsReadOnly());
        }

        var parsed = new Cut1bCoverFile(keyName, file["role"]!.GetValue<string>(), file["source"]!.GetValue<string>(),
            file["entry"]!.GetValue<string>(), file["sha256"]!.GetValue<string>(), file["size"]!.GetValue<long>(),
            alsoIn.AsReadOnly(), controls.AsReadOnly(), skeleton, file["skeletonMissing"]?.GetValue<string>(),
            Digests(file["skeletonFor"]), Digests(file["skeletonCandidateFor"]), file["declined"]?.GetValue<string>());
        if (parsed.NeedsSkeleton && !hasSkeletonMember)
        {
            throw new InvalidDataException($"{parsed}: a .kf the reader reads carries no skeleton member.");
        }

        return parsed;
    }

    private static IReadOnlyList<string> Digests(JsonNode? node)
    {
        List<string> digests = node is JsonArray array ? [.. array.Select(o => o!.GetValue<string>())] : [];
        return digests.AsReadOnly();
    }

    private static void Validate(IReadOnlyList<Cut1bCoverFile> files)
    {
        var bySha = new Dictionary<string, Cut1bCoverFile>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!Cut1bCoverFile.Roles.Contains(file.Role, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"{file}: role '{file.Role}' is not one of {string.Join(", ", Cut1bCoverFile.Roles)}.");
            }

            if (file.Sha256.Length != Sha256HexLength || !file.Sha256.All(char.IsAsciiHexDigitLower) ||
                file.Size <= 0)
            {
                throw new InvalidDataException($"{file}: malformed SHA-256 '{file.Sha256}' or size {file.Size}.");
            }

            if (!bySha.TryAdd(file.Sha256, file))
            {
                throw new InvalidDataException($"{file}: SHA-256 {file.Sha256} appears twice in the manifest.");
            }

            if (file.IsDeclinedControl && string.IsNullOrEmpty(file.Declined))
            {
                throw new InvalidDataException($"{file}: a decline control without the probe's decline reason.");
            }
        }

        foreach (var file in files.Where(f => f.NeedsSkeleton))
        {
            if (file.Skeleton is null)
            {
                if (string.IsNullOrEmpty(file.SkeletonMissing))
                {
                    throw new InvalidDataException($"{file}: no skeleton and no skeletonMissing reason.");
                }

                continue;
            }

            if (!bySha.TryGetValue(file.Skeleton.Sha256, out var skeleton) ||
                !skeleton.SkeletonFor.Contains(file.Sha256, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"{file}: its skeleton {file.Skeleton.Sha256} is not a manifest file listing it in skeletonFor.");
            }

            ValidateAlternatives(file, file.Skeleton, bySha);
        }

        var orphan = files.FirstOrDefault(f =>
            string.Equals(f.Role, Cut1bCoverFile.SkeletonRole, StringComparison.Ordinal) &&
            f.SkeletonFor.Count == 0 && f.SkeletonCandidateFor.Count == 0);
        if (orphan is not null)
        {
            throw new InvalidDataException($"{orphan}: a skeleton that no manifest .kf lists.");
        }
    }

    private static void ValidateAlternatives(Cut1bCoverFile kf, Cut1bSkeletonCompanion skeleton,
        Dictionary<string, Cut1bCoverFile> bySha)
    {
        if (skeleton.IsProvisional != (skeleton.Alternatives.Count > 0))
        {
            throw new InvalidDataException(
                $"{kf}: a skeleton pin is provisional exactly when it names alternatives (provisional: " +
                $"{skeleton.IsProvisional}, alternatives: {skeleton.Alternatives.Count}).");
        }

        foreach (var alternative in skeleton.Alternatives)
        {
            if (string.Equals(alternative.Sha256, skeleton.Sha256, StringComparison.Ordinal) ||
                !bySha.TryGetValue(alternative.Sha256, out var copy) ||
                !string.Equals(copy.Role, Cut1bCoverFile.SkeletonRole, StringComparison.Ordinal) ||
                !copy.SkeletonCandidateFor.Contains(kf.Sha256, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"{kf}: its alternative skeleton {alternative.Sha256} is not another skeleton-role manifest file " +
                    "listing it in skeletonCandidateFor.");
            }
        }
    }

    private static IReadOnlyList<Cut1bCoverFile> Load()
    {
        return Path is { } path ? Parse(File.ReadAllText(path)) : [];
    }
}
