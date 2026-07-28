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
        // The 3-way visibility switch + framing gate + 3D-only seeding.
        Assert.Contains("var export = sender.SelectedItem == WorldPanelExportItem;", switching,
            StringComparison.Ordinal);
        Assert.Contains("WorldExportHost.Visibility = export ?", switching, StringComparison.Ordinal);
        Assert.Contains("WorldView3DControl.SetExportFramingActive(export);", switching, StringComparison.Ordinal);
        Assert.Contains("WorldExportPresenter.Content = show3D ? WorldView3DControl.ExportPanel : null;",
            switching, StringComparison.Ordinal);
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
    public void FramingOverlayDrawsOnlyWhileTheExportTabIsUpAndTheToggleIsOn()
    {
        var panel = SourceContract.ReadAppSource("WorldView3DControl.ExportPanel.cs");
        Assert.Contains(
            "_exportTabSelected && _exportBoundsValid && ExportPanel.ShowFramingToggle.IsOn",
            panel, StringComparison.Ordinal);

        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        SourceContract.AssertOrder(
            frame,
            "if (ExportFramingVisible && _exportFraming is not null)",
            "EnsureExportFramingLines();",
            "_exportFraming.Render(");
    }

    private static string ReadTab(string fileName)
    {
        return SourceContract.ReadAppSource(fileName);
    }
}