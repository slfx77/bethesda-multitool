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

    /// <summary>Repo root, located by probing upward for Directory.Build.props.</summary>
    public static string RepoRoot => LazyRepoRoot.Value;

    /// <summary>Read a source file addressed by path segments relative to the repo root.</summary>
    public static string ReadSource(params string[] relativePath)
    {
        return ReadNormalized(Path.Combine(RepoRoot, Path.Combine(relativePath)));
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
        return File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>The embedded-shader source tree (Gpu/Shaders).</summary>
    public static string ShadersRoot => Path.Combine(
        RepoRoot, "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "Shaders");

    /// <summary>
    ///     Resolve a shader source file by bare file name, searching every Shaders subdirectory.
    ///     Mirrors the runtime's flat LogicalName lookup (names are globally unique by build-time
    ///     guarantee), so shader pins stay valid when a file moves between subdirectories.
    /// </summary>
    public static string ShaderPath(string fileName) =>
        Directory.EnumerateFiles(ShadersRoot, fileName, SearchOption.AllDirectories).Single();

    /// <summary>Read a shader's source text by bare file name.</summary>
    public static string ReadShaderSource(string fileName) => ReadNormalized(ShaderPath(fileName));

    /// <summary>The WinUI application source tree (src/BethesdaMultitool/App).</summary>
    public static string AppRoot => Path.Combine(RepoRoot, "src", "BethesdaMultitool", "App");

    /// <summary>
    ///     Read an App-layer source file by bare file name, searching every App subdirectory.
    ///     App uses a single flat namespace, so files move freely between folders; resolving by
    ///     unique bare file name keeps source pins valid across moves (mirrors
    ///     <see cref="ReadShaderSource" />).
    /// </summary>
    public static string ReadAppSource(string fileName) =>
        ReadNormalized(Directory.EnumerateFiles(AppRoot, fileName, SearchOption.AllDirectories).Single());

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