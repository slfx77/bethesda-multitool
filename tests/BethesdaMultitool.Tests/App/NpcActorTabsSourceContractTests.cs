using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class NpcActorTabsSourceContractTests
{
    [Fact]
    public void ActorsBrowserExposesSeparateNpcAndCreatureTabs()
    {
        var xaml = SourceContract.ReadAppSource("SingleFileTab.xaml");
        var host = SourceContract.ReadAppSource("SingleFileTab.NpcBrowser.cs");

        Assert.Contains("x:Name=\"NpcActorKindTabView\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NpcNpcListTab\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"NPCs\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NpcCreatureListTab\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Creatures\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectionChanged=\"NpcActorKindTabView_SelectionChanged\"", xaml,
            StringComparison.Ordinal);
        Assert.Contains("_npcBrowser.SetActorKind(", host, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Select actor\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanBatchSelect}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactStartupSelectionSwitchesToTheResolvedActorFamilyBeforeLookup()
    {
        var host = SourceContract.ReadAppSource("SingleFileTab.NpcBrowser.cs");
        var selection = SourceContract.Extract(
            host,
            "private void SelectAutoOpenActor(",
            "#endregion");

        SourceContract.AssertOrder(
            selection,
            "var resolvedActor = resolution.Actor!;",
            "ApplyNpcActorKind(resolvedActor.IsCreature ? NpcActorKind.Creature : NpcActorKind.Npc);",
            "_npcBrowser.FindVisible(resolvedActor.FormId)");
    }

    [Fact]
    public void BrowserResolvesCreatureTypeNamesUsingTheDetectedGame()
    {
        var service = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Npc",
            "NpcBrowserService.cs");

        Assert.Contains("creature.GetCreatureTypeName(_game)", service, StringComparison.Ordinal);

        var cliRenderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "CLI", "Rendering", "Npc", "NpcCreatureRenderer.cs");
        var cliExporter = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "CLI", "Rendering", "Npc", "NpcExportPipeline.cs");
        Assert.Contains("creature.GetCreatureTypeName(game)", cliRenderer, StringComparison.Ordinal);
        Assert.Contains("creature.GetCreatureTypeName(resolver.Game)", cliExporter, StringComparison.Ordinal);
    }

    [Fact]
    public void TabSwitchPublishesMatchingRowsBeforeCanceledSceneDrain()
    {
        var host = SourceContract.ReadAppSource("SingleFileTab.NpcBrowser.cs");
        var handler = SourceContract.Extract(
            host,
            "private async void NpcActorKindTabView_SelectionChanged(",
            "private void ApplyNpcActorKind(");

        SourceContract.AssertOrder(
            handler,
            "ApplyNpcSelectionState(NpcSelectionState.Empty);",
            "ApplyNpcActorKind(requestedKind);",
            "await CancelNpcViewerLoadAndDrainAsync();");
    }

    [Fact]
    public void CreatureTabCannotInvokeNpcOnlyBatchOperations()
    {
        var xaml = SourceContract.ReadAppSource("SingleFileTab.xaml");
        var host = SourceContract.ReadAppSource("SingleFileTab.NpcBrowser.cs");

        Assert.Contains("x:Name=\"NpcBatchHelpText\"", xaml, StringComparison.Ordinal);
        Assert.Equal(2, SourceContract.CountOccurrences(xaml, "Click=\"NpcBatch"));
        Assert.Contains("_npcBrowser.ActorKind != NpcActorKind.Npc", host, StringComparison.Ordinal);
        Assert.Contains("_npcBrowser.ActorKind == NpcActorKind.Npc", host, StringComparison.Ordinal);
        Assert.Contains("Batch operations are currently available for NPCs only", host, StringComparison.Ordinal);
    }

    [Fact]
    public void SupersededActorSelectionStopsBeforeStartingAnotherComposition()
    {
        var host = SourceContract.ReadAppSource("SingleFileTab.NpcBrowser.cs");
        var load = SourceContract.Extract(
            host,
            "private async Task LoadNpcIntoViewerAsync(",
            "private async void NpcRenderOption_Changed(");

        SourceContract.AssertOrder(
            load,
            "await CancelNpcViewerLoadAndDrainAsync();",
            "if (!IsCurrentNpcSelection(npc)) return;",
            "var options = BuildNpcRenderOptions();",
            "BuildViewerSceneAsync(");
    }

    [Fact]
    public void AsyncFileActionsRetainTheirActorAndRestoreCurrentUiPolicy()
    {
        var host = SourceContract.ReadAppSource("SingleFileTab.NpcBrowser.cs");
        var render = SourceContract.Extract(
            host,
            "private async void NpcRenderPng_Click(",
            "#endregion");

        SourceContract.AssertOrder(
            render,
            "NpcListView.SelectedItem is not NpcListItem npc",
            "!TryBeginNpcFileOperation()",
            "npc.FormId",
            "EndNpcFileOperation();");
        Assert.DoesNotContain("_npcBrowser.SelectedFormId.Value", render, StringComparison.Ordinal);

        var interaction = SourceContract.Extract(
            host,
            "private void RefreshNpcInteractionState()",
            "private bool IsActorInCurrentFamily(");
        Assert.Contains("!_npcFileOperationInProgress", interaction, StringComparison.Ordinal);
        Assert.Contains("_npcSelectionState.CanCaptureNative", interaction, StringComparison.Ordinal);
        Assert.Contains("_npcSelectionState.CanRenderPng", interaction, StringComparison.Ordinal);
    }
}
