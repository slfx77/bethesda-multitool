using System.Text.Json.Nodes;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     The checked-in cut-1a cover manifest (design section 7.1; <c>nif_cover.py</c> schema 2): 17 version keys and 231
///     files (219 cover, 1 floor, 11 declined controls), each with its source, entry, SHA-256 and the other sources the
///     census saw it in. It is read from the repository (the file is not copied to the test output), located by probing
///     upward from the test assembly for <c>Directory.Build.props</c>, exactly as the source-contract tests do.
/// </summary>
/// <remarks>
///     <see cref="Rows" /> is the <c>MemberData</c> of every manifest-driven oracle theory: one row per manifest file,
///     identified by its entry (for the test name) and its SHA-256 (the key into the manifest and the expectations);
///     <see cref="BigEndianRows" /> is the same over the 95 big-endian (X360 and PS3) files alone, for the hops that
///     compare a console file against the PC file of the same path. The class is public because xUnit resolves
///     <c>MemberData</c> members reflectively and requires them to be public.
/// </remarks>
public static class Cut1aCoverManifest
{
    /// <summary>The manifest's repository-relative path.</summary>
    public const string RelativePath = "tests/BethesdaMultitool.Tests/Core/Modeling/Samples/cut1a-cover-manifest.json";

    private static readonly Lazy<string?> LazyRepoRoot = new(FindRepoRoot);
    private static readonly Lazy<IReadOnlyList<Cut1aCoverFile>> LazyFiles = new(Load);

    /// <summary>The repository root, or null when the test assembly runs outside a checkout.</summary>
    public static string? RepoRoot => LazyRepoRoot.Value;

    /// <summary>The manifest's absolute path, or null when it cannot be located.</summary>
    public static string? Path => RepoRoot is { } root && File.Exists(System.IO.Path.Combine(root, RelativePath))
        ? System.IO.Path.Combine(root, RelativePath)
        : null;

    /// <summary>Every manifest file in manifest order (empty when the manifest cannot be located).</summary>
    internal static IReadOnlyList<Cut1aCoverFile> Files => LazyFiles.Value;

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

    /// <summary>One theory row per big-endian manifest file: (entry, SHA-256).</summary>
    public static TheoryData<string, string> BigEndianRows()
    {
        var rows = new TheoryData<string, string>();
        foreach (var file in Files)
        {
            if (file.IsBigEndian)
            {
                rows.Add(file.Entry, file.Sha256);
            }
        }

        return rows;
    }

    /// <summary>The manifest file with the given SHA-256.</summary>
    /// <exception cref="InvalidOperationException">No manifest file has that digest.</exception>
    internal static Cut1aCoverFile Require(string sha256)
    {
        return Files.SingleOrDefault(f => string.Equals(f.Sha256, sha256, StringComparison.Ordinal))
               ?? throw new InvalidOperationException(
                   $"The cut-1a cover manifest ({Path ?? RelativePath}) has no file with SHA-256 {sha256}.");
    }

    /// <summary>A file under the repository root, or null when the root is unknown.</summary>
    internal static string? RepoFile(string relativePath)
    {
        return RepoRoot is { } root ? System.IO.Path.Combine(root, relativePath) : null;
    }

    private static IReadOnlyList<Cut1aCoverFile> Load()
    {
        if (Path is not { } path)
        {
            return [];
        }

        var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var files = new List<Cut1aCoverFile>();
        foreach (var key in manifest["keys"]!.AsArray())
        {
            var keyName = key!["key"]!.GetValue<string>();
            foreach (var file in key["files"]!.AsArray())
            {
                var alsoIn = new List<Cut1aCoverSource>();
                foreach (var other in file!["alsoIn"]!.AsArray())
                {
                    alsoIn.Add(new Cut1aCoverSource(other!["source"]!.GetValue<string>(),
                        other["entry"]!.GetValue<string>()));
                }

                files.Add(new Cut1aCoverFile(keyName, file["role"]!.GetValue<string>(),
                    file["source"]!.GetValue<string>(), file["entry"]!.GetValue<string>(),
                    file["sha256"]!.GetValue<string>(), file["size"]!.GetValue<long>(), alsoIn.AsReadOnly()));
            }
        }

        return files.AsReadOnly();
    }

    private static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(System.IO.Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName;
    }
}
