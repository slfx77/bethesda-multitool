using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Slfx77.Multitool.Core.Browsing;

namespace BethesdaMultitool;

/// <summary>Runs selected extraction and texture conversion independently of browsing source changes.</summary>
public sealed partial class AssetBrowserTab
{
    private CancellationTokenSource? _exportCancellation;

    /// <summary>Updates feedback and its layout together, including before the text box is realized.</summary>
    /// <param name="message">The current result, progress, or failure; empty text removes the feedback row.</param>
    private void SetAssetExportStatus(string message)
    {
        RuntimeLocalization.SetRaw(AssetExportStatus, TextBox.TextProperty, message);
        AssetExportStatus.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Retains an export message independently of source and conversion lifetimes.</summary>
    /// <param name="key">The export message resource key.</param>
    private void SetLocalizedAssetExportStatus(string key)
    {
        RuntimeLocalization.Set(AssetExportStatus, TextBox.TextProperty, key);
        AssetExportStatus.Visibility = Visibility.Visible;
    }

    /// <summary>Checks every current-source entry without changing the preview focus.</summary>
    private void SelectAllAssets_Click(object sender, RoutedEventArgs args)
    {
        if (CurrentAssetSelection is { } selection)
            selection.SetChecked(selection.Checks.Snapshot, selection.Session.Root, true);
    }

    /// <summary>Clears checked assets while leaving the current preview intact.</summary>
    private void ClearAssetSelection_Click(object sender, RoutedEventArgs args)
    {
        if (CurrentAssetSelection is { } selection) selection.Checks.Clear(selection.Checks.Snapshot);
    }

    /// <summary>Starts original-payload extraction for the captured selection.</summary>
    private async void ExtractSelected_Click(object sender, RoutedEventArgs args) => await ExportSelectedAsync(AssetExportMode.Original);

    /// <summary>Starts DDX conversion for the captured checked texture selection.</summary>
    private async void ConvertSelectedDdx_Click(object sender, RoutedEventArgs args) => await ExportSelectedAsync(AssetExportMode.DdxToDds);

    /// <summary>Cancels the active export without discarding completed outputs.</summary>
    private void CancelAssetExport_Click(object sender, RoutedEventArgs args) => _exportCancellation?.Cancel();

    /// <summary>Retains the source and selected identities through picking and conversion.</summary>
    private async Task ExportSelectedAsync(AssetExportMode mode)
    {
        if (_exportCancellation is not null || CurrentAssetSelection is not { } selection) return;
        var snapshot = selection.Checks.Snapshot;
        var source = selection.Session;
        BrowserLease? lease = null;
        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        ExtractSelectedButton.IsEnabled = ConvertSelectedDdxButton.IsEnabled = false;
        CancelAssetExportButton.IsEnabled = true;
        var overwrite = AssetExportOverwrite.IsChecked == true;
        try
        {
            lease = snapshot.AcquireLease();
            var paths = selection.CaptureSelected(snapshot).Select(node => node.VirtualPath)
                .Where(path => mode != AssetExportMode.DdxToDds || path.EndsWith(".ddx", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (paths.Length == 0)
            {
                SetLocalizedAssetExportStatus("AssetExport_Empty");
                return;
            }
            var filesystem = source.FileSystem;
            var output = await PickFolderAsync();
            if (output is null || _disposed) { return; }
            var succeeded = 0;
            var failed = 0;
            var sourceLabel = source.SourceLabel;
            var errors = new List<string>();
            var progress = new Progress<AssetExportResult>(result =>
            {
                if (_disposed) { return; }
                if (result.Success) { succeeded++; }
                else
                {
                    failed++;
                    if (errors.Count < 50) { errors.Add(result.VirtualPath + ": " + result.Error); }
                }
                var completedCount = succeeded;
                var failedCount = failed;
                var errorText = errors.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, errors) : string.Empty;
                RuntimeLocalization.Bind(AssetExportStatus, TextBox.TextProperty,
                    strings => strings.Format("AssetExport_Progress", completedCount, failedCount, sourceLabel) + errorText);
                AssetExportStatus.Visibility = Visibility.Visible;
            });
            await AssetExportService.ExportAsync(filesystem, paths, output, mode, overwrite, progress,
                cancellationToken: cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (!_disposed) { SetLocalizedAssetExportStatus("AssetExport_Canceled"); }
        }
        catch (Exception exception)
        {
            if (!_disposed) { SetAssetExportStatus(exception.Message); }
        }
        finally
        {
            if (lease is not null) { await lease.DisposeAsync(); }
            _exportCancellation = null;
            if (!_disposed)
            {
                ExtractSelectedButton.IsEnabled = ConvertSelectedDdxButton.IsEnabled = true;
                CancelAssetExportButton.IsEnabled = false;
            }
        }
    }
}
