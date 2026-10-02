using System.Collections.Concurrent;
using Xunit;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Shared harness for source-contract tests — tests that read production source files
///     (renderers, shaders) off disk and pin decompile-derived constants, orderings, and
///     occurrence counts. Centralizes the repo-root probe and string helpers that were
///     previously copy-pasted per test file.
/// </summary>
internal static class SourceContract
{
    private static readonly Lazy<string> LazyRepoRoot = new(FindRepoRoot);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    // A test process evaluates one checkout. Keep its source snapshot and flat-name indexes
    // for the run instead of repeatedly reading and traversing the same trees for every case.
    private static readonly ConcurrentDictionary<string, Lazy<string>> Sources = new(PathComparer);
    private static readonly Lazy<ILookup<string, string>> ShaderFiles = new(() => IndexFiles(ShadersRoot));
    private static readonly Lazy<ILookup<string, string>> AppFiles = new(() => IndexFiles(AppRoot));
    private static readonly Lazy<string[]> ProductionFiles = new(() => Directory.EnumerateFiles(
        Path.Combine(RepoRoot, "src"), "*", SearchOption.AllDirectories).ToArray());

    /// <summary>Snapshot of the same production-tree paths used by cross-file architecture checks.</summary>
    public static IReadOnlyList<string> ProductionSourcePaths => ProductionFiles.Value;

    /// <summary>Read a resolved source path from the current test process's immutable checkout snapshot.</summary>
    public static string ReadSourceFile(string path) => ReadNormalized(path);

    /// <summary>Repo root, located by probing upward for Directory.Build.props.</summary>
    public static string RepoRoot => LazyRepoRoot.Value;

    /// <summary>The embedded-shader source tree (Gpu/Shaders).</summary>
    public static string ShadersRoot => Path.Combine(
        RepoRoot, "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "Shaders");

    /// <summary>The WinUI application source tree (src/BethesdaMultitool/App).</summary>
    public static string AppRoot => Path.Combine(RepoRoot, "src", "BethesdaMultitool", "App");

    /// <summary>Read a source file addressed by path segments relative to the repo root.</summary>
    public static string ReadSource(params string[] relativePath)
    {
        return ReadNormalized(Path.Combine(RepoRoot, Path.Combine(relativePath)));
    }

    /// <summary>
    ///     Read reference material that lives OUTSIDE the repository — a Ghidra decompile under
    ///     <c>tools/GhidraProject/</c>, a harness script under <c>scratchpad/</c>, vendored source
    ///     under <c>Sample/Reference_Code/</c> — all of which <c>.gitignore</c> keeps out of every
    ///     clone. A pin against such a file cannot run where the file is absent (CI, a worktree,
    ///     another machine), so it SKIPS there naming the path, rather than failing with a
    ///     DirectoryNotFoundException or passing without having read anything.
    /// </summary>
    public static string ReadLocalReference(params string[] relativePath)
    {
        var relative = Path.Combine(relativePath);
        var path = Path.Combine(RepoRoot, relative);
        Assert.SkipWhen(!File.Exists(path),
            $"Local-only reference material not present in this checkout (gitignored): {relative}.");
        return ReadNormalized(path);
    }

    /// <summary>
    ///     Read a source file with line endings normalized to LF. Markers in these tests are C#
    ///     string literals containing bare <c>\n</c>, but the checked-out line endings are not
    ///     fixed: this repo's working tree holds LF while the GitHub Windows runner image sets
    ///     <c>core.autocrlf=true</c> and checks the same files out as CRLF. Comparing raw file text
    ///     against an LF marker therefore passes locally and fails only in CI. Normalizing on read
    ///     makes every multi-line marker platform-stable; single-line markers are unaffected.
    /// </summary>
    private static string ReadNormalized(string path)
    {
        return Sources.GetOrAdd(Path.GetFullPath(path), static fullPath => new Lazy<string>(() =>
            File.ReadAllText(fullPath).Replace("\r\n", "\n", StringComparison.Ordinal))).Value;
    }

    private static ILookup<string, string> IndexFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToLookup(path => Path.GetFileName(path), PathComparer);

    /// <summary>
    ///     Resolve a shader source file by bare file name, searching every Shaders subdirectory.
    ///     Mirrors the runtime's flat LogicalName lookup (names are globally unique by build-time
    ///     guarantee), so shader pins stay valid when a file moves between subdirectories.
    /// </summary>
    public static string ShaderPath(string fileName)
    {
        return ShaderFiles.Value[fileName].Single();
    }

    /// <summary>Read a shader's source text by bare file name.</summary>
    public static string ReadShaderSource(string fileName)
    {
        return ReadNormalized(ShaderPath(fileName));
    }

    /// <summary>
    ///     Read an App-layer source file by bare file name, searching every App subdirectory.
    ///     App uses a single flat namespace, so files move freely between folders; resolving by
    ///     unique bare file name keeps source pins valid across moves (mirrors
    ///     <see cref="ReadShaderSource" />).
    /// </summary>
    public static string ReadAppSource(string fileName)
    {
        return ReadNormalized(AppFiles.Value[fileName].Single());
    }

    /// <summary>Assert each value appears in <paramref name="source" /> after the previous one.</summary>
    public static void AssertOrder(string source, params string[] values)
    {
        var previous = -1;
        foreach (var value in values)
        {
            var current = source.IndexOf(value, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected `{value}` after source offset {previous}.");
            previous = current;
        }
    }

    /// <summary>Assert source fragments without pinning indentation or line wrapping.</summary>
    public static void AssertContainsIgnoringWhitespace(string value, string source)
    {
        Assert.Contains(RemoveWhitespace(value), RemoveWhitespace(source), StringComparison.Ordinal);
    }

    /// <summary>Assert source ordering without pinning indentation or line wrapping.</summary>
    public static void AssertOrderIgnoringWhitespace(string source, params string[] values)
    {
        AssertOrder(RemoveWhitespace(source), values.Select(RemoveWhitespace).ToArray());
    }

    private static string RemoveWhitespace(string source)
    {
        return string.Concat(source.Where(character => !char.IsWhiteSpace(character)));
    }

    /// <summary>Count non-overlapping occurrences of <paramref name="value" />.</summary>
    public static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    /// <summary>Extract the substring from <paramref name="startMarker" /> up to <paramref name="endMarker" />.</summary>
    public static string Extract(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker `{startMarker}`.");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker `{endMarker}` after `{startMarker}`.");
        return source[start..end];
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
