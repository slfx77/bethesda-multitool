using BethesdaMultitool.Core.AssetBrowse;
using Microsoft.UI.Xaml;

namespace BethesdaMultitool;

/// <summary>Opens physical archive leaves through the workspace's existing source replacement workflow.</summary>
public sealed partial class AssetBrowserTab
{
    /// <summary>Lets the workspace retain ownership of cancellation, record state and source retirement.</summary>
    internal event Action<AssetArchiveOpenRequest>? ArchiveOpenRequested;

    /// <summary>Admits only the exact selected node from the currently attached source.</summary>
    /// <returns>The physical request, or null when no supported current archive is selected.</returns>
    private AssetArchiveOpenRequest? CurrentArchiveOpenRequest() =>
        CurrentAssetSelection is { } selection && _sourceSnapshot is { } snapshot &&
        AssetTreeView.SelectedNode?.Content is AssetNode node
            ? AssetArchiveOpenRequest.TryCreate(selection, snapshot, node)
            : null;

    /// <summary>Refreshes the explicit command without opening or copying an archive payload.</summary>
    private void UpdateArchiveOpenCommand() => OpenSelectedArchiveButton.IsEnabled = CurrentArchiveOpenRequest() is not null;

    /// <summary>Revalidates the source at invocation and delegates replacement to its existing owner.</summary>
    /// <param name="sender">The explicit archive-open button.</param>
    /// <param name="args">The native invocation.</param>
    private async void OpenSelectedArchive_Click(object sender, RoutedEventArgs args)
    {
        if (CurrentArchiveOpenRequest() is not { } request) return;
        if (ArchiveOpenRequested is { } handler) handler(request);
        else await OpenFromLaunchArgumentAsync(request.Path);
    }
}
