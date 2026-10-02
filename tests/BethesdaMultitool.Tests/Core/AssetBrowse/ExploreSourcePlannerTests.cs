using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.AssetBrowse;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>Verifies explicit source selection and bounded plugin discovery.</summary>
public sealed class ExploreSourcePlannerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "explore-source-" + Guid.NewGuid().ToString("N"));

    public ExploreSourcePlannerTests() => Directory.CreateDirectory(_directory);

    /// <summary>A sole immediate plugin can populate Data without a second picker.</summary>
    [Fact]
    public void FolderWithOnePlugin_SelectsItAndMountsDataDirectory()
    {
        var plugin = WritePlugin("primary.esm");
        var plan = ExploreSourcePlanner.Create(_directory, TestContext.Current.CancellationToken);
        Assert.Equal(plugin, plan.AnalysisPath);
        Assert.Equal(ExploreAssetSourceKind.DataDirectory, plan.AssetKind);
        Assert.Equal(AnalysisFileType.EsmFile, plan.AnalysisType);
    }

    /// <summary>Multiple candidates remain an explicit choice rather than a guessed load order.</summary>
    [Fact]
    public void MultiplePlugins_DoNotChooseOrMergeCandidates()
    {
        WritePlugin("z.esp");
        var first = WritePlugin("a.esm");
        var plan = ExploreSourcePlanner.Create(_directory, TestContext.Current.CancellationToken);
        Assert.Null(plan.AnalysisPath);
        Assert.Equal(2, plan.RecordCandidates.Count);
        Assert.Equal(first, plan.RecordCandidates[0]);
    }

    /// <summary>An explicitly opened plugin wins even beside other plugins.</summary>
    [Fact]
    public void ExplicitPlugin_RetainsSelection()
    {
        WritePlugin("a.esm");
        var selected = WritePlugin("selected.esp");
        var plan = ExploreSourcePlanner.Create(selected, TestContext.Current.CancellationToken);
        Assert.Equal(selected, plan.AnalysisPath);
        Assert.Equal(_directory, plan.AssetPath);
    }

    /// <summary>A game root resolves its immediate Data directory without scanning unrelated trees.</summary>
    [Fact]
    public void GameRoot_UsesDataChildButIgnoresDeeperPlugins()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "Data"));
        var plugin = WritePlugin(Path.Combine("Data", "primary.ESM"));
        Directory.CreateDirectory(Path.Combine(_directory, "unrelated"));
        WritePlugin(Path.Combine("unrelated", "other.esm"));
        var plan = ExploreSourcePlanner.Create(_directory, TestContext.Current.CancellationToken);
        Assert.Equal(plugin, plan.AnalysisPath);
        Assert.Single(plan.RecordCandidates);
        Assert.Equal(Path.Combine(_directory, "Data"), plan.AssetPath);
    }

    /// <summary>Plugin-looking files without a plugin header cannot become the primary source.</summary>
    [Fact]
    public void UnsupportedPluginHeader_LeavesAssetOnlySource()
    {
        File.WriteAllText(Path.Combine(_directory, "invalid.esm"), "not a plugin");
        var plan = ExploreSourcePlanner.Create(_directory, TestContext.Current.CancellationToken);
        Assert.Null(plan.AnalysisPath);
        Assert.Empty(plan.RecordCandidates);
        Assert.Equal(ExploreAssetSourceKind.Folder, plan.AssetKind);
    }

    /// <summary>Dumps remain record sources and never become implicit recovery inputs.</summary>
    [Fact]
    public void DumpSelection_PreservesTheExplicitFile()
    {
        var path = Path.Combine(_directory, "capture.dmp");
        File.WriteAllBytes(path, "MDMP"u8.ToArray());
        var plan = ExploreSourcePlanner.Create(path, TestContext.Current.CancellationToken);
        Assert.Equal(path, plan.AnalysisPath);
        Assert.Equal(AnalysisFileType.Minidump, plan.AnalysisType);
        Assert.Equal(_directory, plan.AssetPath);
    }

    /// <summary>Canceled requests stop before probing a source.</summary>
    [Fact]
    public void CanceledRequest_DoesNotPublishAPlan()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ExploreSourcePlanner.Create(_directory, cancellation.Token));
    }

    private string WritePlugin(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, "TES4"u8.ToArray());
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
