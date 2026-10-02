using System.Text.Json.Nodes;
using BethesdaMultitool.Tests.Core.Modeling.RealAsset;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Starfield;

/// <summary>
///     The checked-in cut-2 Starfield <c>.mesh</c> cover manifest (plan
///     <c>docs/design/cut2-starfield-mesh-reader-plan-20260928.md</c>, section 8): the pairwise MILP joint cover, the edge
///     files and the retail decline controls, each pinned by archive, entry, directory index, payload size and SHA-256,
///     written by <c>tools/scripts/gate2/starfield_mesh_cover.py manifest</c>. It is read from the repository like the
///     cut-1a manifest (whose repository root it shares).
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Parse" /> refuses a manifest of another schema, one whose cover left an item uncovered, an unknown
///         role, a blank or repeated row name, a malformed SHA-256, a non-positive size, a source that is not a Starfield
///         Data archive or disagrees with the archive it names, a negative index, a repeated (archive, entry) candidate, a
///         repeated payload digest, and a decline control that does not expect NotAModel. A generator that stopped pinning
///         any of that fails the load instead of weakening a Bucket-B check.
///     </para>
///     <para>
///         <see cref="Rows" /> is the <c>MemberData</c> of every manifest-driven theory (row name, SHA-256). The class is
///         public because xUnit resolves <c>MemberData</c> members reflectively.
///     </para>
/// </remarks>
public static class Cut2MeshCoverManifest
{
    /// <summary>The manifest's repository-relative path.</summary>
    public const string RelativePath =
        "tests/BethesdaMultitool.Tests/Core/Modeling/Samples/cut2-starfield-mesh-cover-manifest.json";

    /// <summary>The schema the manifest must declare.</summary>
    public const string Schema = "cut2-starfield-mesh-cover/2";

    /// <summary>The prefix every source carries.</summary>
    public const string SourcePrefix = "<SteamLibrary>/Starfield/Data/";

    private const int Sha256HexLength = 64;

    private static readonly Lazy<IReadOnlyList<Cut2MeshCoverFile>> LazyFiles = new(Load);

    /// <summary>The manifest's absolute path, or null when it cannot be located.</summary>
    public static string? Path =>
        Cut1aCoverManifest.RepoFile(RelativePath) is { } path && File.Exists(path) ? path : null;

    /// <summary>Every manifest row in manifest order, files then decline controls (empty when the manifest is absent).</summary>
    internal static IReadOnlyList<Cut2MeshCoverFile> Files => LazyFiles.Value;

    /// <summary>One theory row per manifest row: (row name, payload SHA-256).</summary>
    public static TheoryData<string, string> Rows()
    {
        return RowsWhere(static _ => true);
    }

    /// <summary>One theory row per cover or edge file.</summary>
    public static TheoryData<string, string> MeshRows()
    {
        return RowsWhere(static file => file.IsMesh);
    }

    /// <summary>One theory row per retail decline control.</summary>
    public static TheoryData<string, string> DeclineRows()
    {
        return RowsWhere(static file => !file.IsMesh);
    }

    /// <summary>The manifest row with the given unique name.</summary>
    /// <exception cref="InvalidOperationException">No manifest row has that name.</exception>
    internal static Cut2MeshCoverFile Require(string name)
    {
        return Files.SingleOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal))
               ?? throw new InvalidOperationException(
                   $"The cut-2 cover manifest ({Path ?? RelativePath}) has no row named '{name}'.");
    }

    /// <summary>Parses and validates a manifest (see the remarks).</summary>
    /// <exception cref="InvalidDataException">The manifest breaks one of the rules.</exception>
    internal static IReadOnlyList<Cut2MeshCoverFile> Parse(string json)
    {
        var manifest = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("The manifest is empty.");
        if (!string.Equals(manifest["schema"]?.GetValue<string>(), Schema, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The manifest is not schema {Schema}.");
        }

        if (manifest["uncoveredItems"]?.AsArray() is not { Count: 0 })
        {
            throw new InvalidDataException("The manifest's cover left items uncovered (or does not say).");
        }

        List<Cut2MeshCoverFile> files =
        [
            .. manifest["files"]!.AsArray().Select(node => ParseRow(node!.AsObject())),
            .. manifest["declineControls"]!.AsArray().Select(node => ParseRow(node!.AsObject()))
        ];
        Validate(files);
        return files.AsReadOnly();
    }

    private static TheoryData<string, string> RowsWhere(Func<Cut2MeshCoverFile, bool> predicate)
    {
        var rows = new TheoryData<string, string>();
        foreach (var file in Files.Where(predicate))
        {
            rows.Add(file.Name, file.Sha256);
        }

        return rows;
    }

    private static Cut2MeshCoverFile ParseRow(JsonObject row)
    {
        var role = row["role"]!.GetValue<string>();
        if (string.Equals(role, Cut2MeshCoverFile.DeclineRole, StringComparison.Ordinal) &&
            !string.Equals(row["expect"]?.GetValue<string>(), "NotAModel", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Decline control '{row["name"]}' does not expect NotAModel.");
        }

        List<Cut2MeshCoverSource> alsoIn =
        [
            .. (row["alsoIn"]?.AsArray() ?? []).Select(static o => new Cut2MeshCoverSource(
                o!["source"]!.GetValue<string>(), o["archive"]!.GetValue<string>(), o["entry"]!.GetValue<string>(),
                o["index"]!.GetValue<int>()))
        ];
        var primary = new Cut2MeshCoverSource(row["source"]!.GetValue<string>(), row["archive"]!.GetValue<string>(),
            row["entry"]!.GetValue<string>(), row["index"]!.GetValue<int>());
        List<string> tags = [.. (row["tags"]?.AsArray() ?? []).Select(static t => t!.GetValue<string>())];
        return new Cut2MeshCoverFile(role, row["name"]!.GetValue<string>(), primary, row["size"]!.GetValue<long>(),
            row["sha256"]!.GetValue<string>(), alsoIn.AsReadOnly(), row["cell"]?.GetValue<string>(), tags.AsReadOnly(),
            row["tail"]?.GetValue<bool>(), row["lodCount"]?.GetValue<int>(), row["kind"]?.GetValue<string>(),
            row["passesVersion"]?.GetValue<bool>() ?? false);
    }

    private static void Validate(IReadOnlyList<Cut2MeshCoverFile> files)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var shas = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!Cut2MeshCoverFile.Roles.Contains(file.Role, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"{file}: role '{file.Role}' is not one of {string.Join(", ", Cut2MeshCoverFile.Roles)}.");
            }

            if (string.IsNullOrWhiteSpace(file.Name) || !names.Add(file.Name))
            {
                throw new InvalidDataException($"{file}: the row name is blank or appears twice.");
            }

            if (file.Sha256.Length != Sha256HexLength || !file.Sha256.All(char.IsAsciiHexDigitLower))
            {
                throw new InvalidDataException($"{file}: malformed SHA-256 '{file.Sha256}'.");
            }

            if (!shas.Add(file.Sha256))
            {
                throw new InvalidDataException($"{file}: SHA-256 {file.Sha256} appears on another row.");
            }

            if (file.Size <= 0)
            {
                throw new InvalidDataException($"{file}: size {file.Size} is not positive.");
            }

            if (file.IsMesh != (file.Cell is not null))
            {
                throw new InvalidDataException($"{file}: a cover cell appears exactly on a file row.");
            }

            foreach (var candidate in file.Candidates)
            {
                if (!string.Equals(candidate.Source, SourcePrefix + candidate.Archive, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"{file}: source '{candidate.Source}' is not {SourcePrefix}{candidate.Archive}.");
                }

                if (candidate.Index < 0 || string.IsNullOrWhiteSpace(candidate.Entry))
                {
                    throw new InvalidDataException($"{file}: candidate {candidate} has no entry or a negative index.");
                }

                if (!candidates.Add(candidate.Archive + "|" + candidate.Entry))
                {
                    throw new InvalidDataException(
                        $"{file}: the candidate ({candidate.Archive}, {candidate.Entry}) appears twice in the manifest.");
                }
            }
        }
    }

    private static IReadOnlyList<Cut2MeshCoverFile> Load()
    {
        return Path is { } path ? Parse(File.ReadAllText(path)) : [];
    }
}
