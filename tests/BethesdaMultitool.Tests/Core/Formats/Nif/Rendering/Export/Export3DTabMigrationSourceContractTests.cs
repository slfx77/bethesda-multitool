using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Source contracts locking the 3D-export overhaul: the options moved from the modal
///     <c>MapExport3DDialog</c> (+ toolbar button) into a persistent right-panel "Export" tab whose
///     controls live in <c>WorldView3DExportPanel</c>, owned + wired by the viewer. These pin the tab
///     hosting, the viewer wiring, the picker-free folder/name output path, and the framing overlay
///     gate so a refactor can't silently regress the migration.
/// </summary>
public sealed class Export3DTabMigrationSourceContractTests
{
    [Fact]
    public void HostAddsAThirdExportTabAndHostBesideSettingsAndInspection()
    {
        var xaml = ReadTab("SingleFileTab.xaml");
        Assert.Contains("x:Name=\"WorldPanelExportItem\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WorldExportHost\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WorldExportPresenter\"", xaml, StringComparison.Ordinal);

        var switching = SourceContract.ReadAppSource("SingleFileTab.WorldMap.cs");
        // The 3-way visibility switch + the framing gate. The tab itself is shared with the 2D map's
        // export panel now (see Export2DTileTraversalSourceContractTests), so the framing overlay — a
        // 3D-scene wireframe — is gated on the 3D viewer being active as well as the tab being up.
        Assert.Contains("var export = sender.SelectedItem == WorldPanelExportItem;", switching,
            StringComparison.Ordinal);
        Assert.Contains("WorldExportHost.Visibility = export ?", switching, StringComparison.Ordinal);
        Assert.Contains("WorldView3DControl.SetExportFramingActive(export && show3D);", switching,
            StringComparison.Ordinal);
        Assert.Contains("(UIElement)WorldView3DControl.ExportPanel", switching, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewerConstructsAndWiresTheExportPanelAndDropsTheToolbarButton()
    {
        var ctor = SourceContract.ReadAppSource("WorldView3DControl.xaml.cs");
        SourceContract.AssertOrder(
            ctor,
            "ExportPanel = new WorldView3DExportPanel();",
            "WireExportPanel();");

        // The old toolbar Export button and its click handler are gone (the tab is the entry point).
        var viewXaml = SourceContract.ReadAppSource("WorldView3DControl.xaml");
        Assert.DoesNotContain("x:Name=\"ExportButton\"", viewXaml, StringComparison.Ordinal);

        var run = SourceContract.ReadAppSource("WorldView3DControl.ExportButton.cs");
        Assert.DoesNotContain("new MapExport3DDialog", run, StringComparison.Ordinal);
        Assert.DoesNotContain("ExportButton_Click", run, StringComparison.Ordinal);
    }

    [Fact]
    public void RunReadsThePanelAndWritesToTheFolderNameFieldsInsteadOfASavePicker()
    {
        var panel = SourceContract.ReadAppSource("WorldView3DControl.ExportPanel.cs");

        // Output path comes from the folder + name fields, not a FileSavePicker.
        Assert.Contains("Path.Combine(folder, name + \".png\")", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("FileSavePicker", panel, StringComparison.Ordinal);
        Assert.Contains("new FolderPicker", panel, StringComparison.Ordinal);

        // Options are read from the panel's own (independent) include checkboxes.
        Assert.Contains("ShowTerrain: ExportPanel.TerrainCheckBox.IsChecked == true,", panel, StringComparison.Ordinal);
        Assert.Contains("RunProjectionExportAsync(", panel, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportPanelUsesTheFormatAgnosticCollisionLabel()
    {
        var xaml = SourceContract.ReadAppSource("WorldView3DExportPanel.xaml");

        Assert.Contains("Content=\"Collision mesh\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Collision mesh\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Havok collision", xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TiledExportOverlapsOneSaveWithTheNextRenderAndDrainsOnEveryExit()
    {
        var source = SourceContract.ReadAppSource("WorldView3DControl.ExportButton.cs");
        var tileLoop = SourceContract.Extract(
            source,
            "for (var visitRow = 0; visitRow < plan.Rows; visitRow++)",
            "if (tiledSavePipeline is { HasPending: true })");
        var coordinator = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Export",
            "SingleOutstandingOperation.cs");

        // Each iteration renders before EnqueueAsync. EnqueueAsync is the only place that drains the
        // preceding save, so iteration N+1 reaches its render before waiting for save N.
        SourceContract.AssertOrder(
            tileLoop,
            "var result = await RenderProjectionTileAsync(",
            "await tiledSavePipeline!.EnqueueAsync(");
        Assert.DoesNotContain("DrainAsync(", tileLoop, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            coordinator,
            "await DrainAsync(commit);",
            "admissionToken.ThrowIfCancellationRequested();",
            "_pending = Task.Run(operation, CancellationToken.None);");

        Assert.Contains("ExportTilePixelAnalysis.IsTransparentClear(bgra)", tileLoop, StringComparison.Ordinal);
        Assert.Equal(2, SourceContract.CountOccurrences(
            source, "ExportTilePixelAnalysis.IsTransparentClear(bgra)"));
        Assert.DoesNotContain("IsUniformTile", source, StringComparison.Ordinal);
        Assert.Contains("PngWriter.SaveRgba(BgraToRgba(bgra)", tileLoop, StringComparison.Ordinal);

        SourceContract.AssertOrder(
            source,
            "await tiledSavePipeline!.EnqueueAsync(",
            "await tiledSavePipeline.DrainAsync(CommitTiledSave);",
            "progress.Report(\"Writing manifest\"");
        Assert.Contains("await tiledSavePipeline.DrainAndRethrowAsync(", source, StringComparison.Ordinal);
        Assert.Contains("primaryException,", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete(basePath)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SerpentineTraversalChangesOnlyVisitOrderAndKeepsPhysicalTileIdentity()
    {
        var source = SourceContract.ReadAppSource("WorldView3DControl.ExportButton.cs");

        SourceContract.AssertOrder(
            source,
            "var coordinate = ExportTileTraversal.GetSerpentineCoordinate(",
            "var tj = coordinate.Row;",
            "var ti = coordinate.Column;",
            "var tileIndex = visitOrdinal + 1;");
        Assert.Contains(
            "progress.Report(totalTiles > 1 ? \"Rendering tile\" : \"Rendering\", tileIndex, totalTiles);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("ti, tj, plan.Cols, plan.Rows);", source, StringComparison.Ordinal);
        Assert.Contains("var col = ti;", source, StringComparison.Ordinal);
        Assert.Contains("var row = tj;", source, StringComparison.Ordinal);
        Assert.Contains("$\"{name}_r{tj}_c{ti}{ext}\"", source, StringComparison.Ordinal);
        Assert.Contains("var physicalIndex = (tj * plan.Cols) + ti;", source, StringComparison.Ordinal);
        Assert.Contains("manifestTiles.Add((result.PhysicalIndex, new", source, StringComparison.Ordinal);
        Assert.Contains(
            ".OrderBy(static entry => entry.PhysicalIndex)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            ".Select(static entry => entry.Tile)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FramingOverlayDrawsOnlyWhileTheExportTabIsUpAndTheToggleIsOn()
    {
        var panel = SourceContract.ReadAppSource("WorldView3DControl.ExportPanel.cs");
        Assert.Contains(
            "_exportTabSelected && _exportBoundsValid && ExportPanel.ShowFramingToggle.IsOn",
            panel, StringComparison.Ordinal);

        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        SourceContract.AssertOrder(
            frame,
            "var exportFramingOverlay = ExportFramingVisible ? _exportFraming : null;",
            "if (exportFramingOverlay is not null)",
            "EnsureExportFramingLines();",
            "exportFramingOverlay.Render(");
    }

    private static string ReadTab(string fileName)
    {
        return SourceContract.ReadAppSource(fileName);
    }
}