using System.Text.RegularExpressions;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Parity;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Parity;

/// <summary>
///     The C# <see cref="MigrationDeltaRegistry" /> is authoritative for parity tests;
///     <c>docs/planner/migration-deltas.md</c> is the human-readable approval log. This
///     test pins them in sync by parsing the markdown's <c>## DELTA-NNN:</c> headers and
///     asserting the id set matches <see cref="MigrationDeltaRegistry.Default" />.
/// </summary>
public sealed class MigrationDeltaMarkdownSyncTests
{
    private static readonly Regex DeltaHeaderRegex =
        new(@"^##\s+(DELTA-\d{3,})\b", RegexOptions.Multiline | RegexOptions.Compiled);

    [Fact]
    public void Registry_And_Markdown_Have_Same_Delta_Ids()
    {
        var markdownPath = Path.Combine(FindRepoRoot(), "docs", "planner", "migration-deltas.md");
        Assert.True(File.Exists(markdownPath),
            $"Expected migration-deltas.md at {markdownPath}.");

        var markdownText = File.ReadAllText(markdownPath);
        var markdownIds = DeltaHeaderRegex
            .Matches(markdownText)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var registryIds = MigrationDeltaRegistry.Default.Deltas
            .Select(d => d.Id)
            .ToHashSet(StringComparer.Ordinal);

        var onlyInMarkdown = markdownIds.Except(registryIds).ToList();
        var onlyInRegistry = registryIds.Except(markdownIds).ToList();

        Assert.True(onlyInMarkdown.Count == 0,
            $"Markdown documents delta ids the registry doesn't know about: {string.Join(", ", onlyInMarkdown)}");
        Assert.True(onlyInRegistry.Count == 0,
            $"Registry contains delta ids missing from the markdown log: {string.Join(", ", onlyInRegistry)}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
