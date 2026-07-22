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
    public static string ReadSource(params string[] relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, Path.Combine(relativePath)));

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
