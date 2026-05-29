using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Architecture;

/// <summary>
///     Enforces the 500-LOC-per-file invariant on the new planner / planned-writer
///     namespaces. The legacy <c>Plugin/Pipeline/PluginBuilder.cs</c> at ~5,200 LOC is the
///     pattern the planner exists to kill — splitting concerns into small focused files is
///     a load-bearing architectural choice, not a stylistic one.
/// </summary>
public sealed class LineCountInvariantTests
{
    private const int MaxLinesPerFile = 500;

    [Fact]
    public void Every_Planner_And_PlannedWriter_File_Is_Within_The_Line_Ceiling()
    {
        var repoRoot = FindRepoRoot();
        var directories = new[]
        {
            Path.Combine(repoRoot, "src", "FalloutXbox360Utils",
                "Core", "Formats", "Esm", "Planner"),
            Path.Combine(repoRoot, "src", "FalloutXbox360Utils",
                "Core", "Formats", "Esm", "PlannedWriter"),
        };

        var offenders = new List<string>();

        foreach (var dir in directories)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var lineCount = File.ReadAllLines(file).Length;
                if (lineCount > MaxLinesPerFile)
                {
                    offenders.Add(
                        $"{Path.GetRelativePath(repoRoot, file)} — {lineCount} lines (> {MaxLinesPerFile}).");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "The following Planner / PlannedWriter files exceed the 500-LOC ceiling:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
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
