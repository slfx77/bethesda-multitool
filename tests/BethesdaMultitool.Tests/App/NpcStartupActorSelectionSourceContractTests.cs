using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class NpcStartupActorSelectionSourceContractTests
{
    [Fact]
    public void Gui_arguments_capture_actor_selector_without_changing_default_selection()
    {
        var program = SourceContract.ReadSource("src", "BethesdaMultitool", "Program.cs");

        Assert.Contains("public static string? AutoOpenActor { get; internal set; }", program,
            StringComparison.Ordinal);
        Assert.Contains("AutoOpenActor = GetFlagValue(args, \"--actor\");", program,
            StringComparison.Ordinal);
        Assert.Contains("Null leaves the actor", program, StringComparison.Ordinal);

        var host = SourceContract.ReadAppSource("SingleFileTab.xaml.cs");
        SourceContract.AssertOrder(
            host,
            "!string.IsNullOrWhiteSpace(Program.AutoOpenActor)",
            "targetTab != AnalysisSubTab.Actors",
            "--actor requires --view actors");
    }

    [Fact]
    public void Actor_auto_open_joins_structural_population_before_exact_selection()
    {
        var host = SourceContract.ReadAppSource("SingleFileTab.xaml.cs");
        var autoOpen = SourceContract.Extract(
            host,
            "private async Task AutoOpenRequestedViewAsync(",
            "private async Task ReproLayerToggleAsync(");

        SourceContract.AssertOrder(
            autoOpen,
            "if (targetTab == AnalysisSubTab.Actors)",
            "if (string.IsNullOrEmpty(actorSelector))",
            "_session.NpcBrowserPopulated",
            "AnalysisPipelinePhase.Parsing",
            "await _tasks.RunExclusiveAsync(\"populate-npcs\", PopulateNpcBrowserAsync)",
            "if (!_session.NpcBrowserPopulated || _npcBrowserService is null)",
            "SelectAutoOpenActor(actorSelector, log)");
        Assert.Contains("opening Actors never picks an arbitrary row", autoOpen, StringComparison.Ordinal);
    }

    [Fact]
    public void Selected_esm_actors_populate_from_structural_index_before_semantic_parse()
    {
        var host = SourceContract.ReadAppSource("SingleFileTab.xaml.cs");
        var analyze = SourceContract.Extract(
            host,
            "private async void AnalyzeButton_Click(",
            "private async void OpenMinidumpButton_Click(");

        SourceContract.AssertOrder(
            analyze,
            "_session.Open(",
            "fileType == AnalysisFileType.EsmFile",
            "ReferenceEquals(selectedTabForAutoPopulate, NpcBrowserTab)",
            "SetPipelinePhase(AnalysisPipelinePhase.Parsing)",
            "await _tasks.RunExclusiveAsync(\"populate-npcs\", PopulateNpcBrowserAsync)",
            "await RunSemanticParsePipelineAsync(");
        Assert.Contains("AdoptSemanticSession replaces the session accessor", analyze, StringComparison.Ordinal);
        Assert.Contains("lets --actor join the exact", analyze, StringComparison.Ordinal);
    }

    [Fact]
    public void Actor_auto_open_resolves_against_complete_list_and_logs_every_outcome()
    {
        var browser = SourceContract.ReadAppSource("SingleFileTab.NpcBrowser.cs");
        var selection = SourceContract.Extract(
            browser,
            "private void SelectAutoOpenActor(",
            "#endregion");

        SourceContract.AssertOrder(
            selection,
            "NpcStartupActorSelector.Resolve(_npcBrowser.FullList, selector)",
            "if (!resolution.IsResolved)",
            "outcome={1}",
            "_npcBrowser.FindVisible(resolvedActor.FormId)",
            "NpcListView.SelectedItem = visibleActor",
            "outcome=selected");
        Assert.DoesNotContain("FilteredList[0]", selection, StringComparison.Ordinal);
        Assert.DoesNotContain("FullList[0]", selection, StringComparison.Ordinal);
    }

    [Fact]
    public void Npc_load_logs_list_publication_and_cpu_scene_assembly_boundaries()
    {
        var browser = SourceContract.ReadAppSource("SingleFileTab.NpcBrowser.cs");
        var workflow = SourceContract.ReadAppSource("NpcBrowserWorkflowService.cs");

        Assert.Contains("NPC Browser list publication completed", browser, StringComparison.Ordinal);
        Assert.Contains("materialize={2:N2} ms filter={3:N2} ms uiBind={4:N2} ms", browser,
            StringComparison.Ordinal);
        Assert.Contains("NPC Browser actor-scene assembly started", workflow, StringComparison.Ordinal);
        Assert.Contains("NPC Browser actor-scene assembly completed", workflow, StringComparison.Ordinal);
        Assert.Contains("NPC Browser actor-scene assembly failed", workflow, StringComparison.Ordinal);
    }
}
