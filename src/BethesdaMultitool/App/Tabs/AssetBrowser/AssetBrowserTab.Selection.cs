using BethesdaMultitool.Core.AssetBrowse;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BethesdaMultitool;

/// <summary>Routes native tree and thumbnail checks to the current source's shared selection owner.</summary>
public sealed partial class AssetBrowserTab
{
    /// <summary>The exact attached, nonretired source selection; clearing/remounting never substitutes a reused path.</summary>
    private AssetTreeSelection? CurrentAssetSelection =>
        !_disposed && !_shadowkeySourceClosing && _selection is { } selection && ReferenceEquals(selection.Checks.Snapshot, _sourceSnapshot) &&
        ReferenceEquals(selection.Session, _session) && !selection.Checks.Snapshot.CancellationToken.IsCancellationRequested
            ? selection : null;

    /// <summary>Rejects delayed preview and thumbnail events before they can use current-source bytes.</summary>
    /// <param name="node">The original object carried by the tree or gallery.</param>
    /// <returns>Whether this node belongs to the currently attached source and fixed topology.</returns>
    private bool IsCurrentAssetNode(AssetNode node) => CurrentAssetSelection is { } selection &&
        selection.ContainsCurrentNode(selection.Checks.Snapshot, node);

    /// <summary>Initializes a realized row, including mixed values equal to the attached property's null default.</summary>
    private void AssetCheckBox_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is CheckBox { Tag: AssetNode node } checkBox && IsCurrentAssetNode(node))
            checkBox.IsChecked = node.IsChecked;
    }

    /// <summary>Commits one native command and restores its final shared value even for an idempotent command.</summary>
    private void AssetCheckBox_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not CheckBox { Tag: AssetNode node } checkBox || CurrentAssetSelection is not { } selection ||
            !selection.ContainsCurrentNode(selection.Checks.Snapshot, node)) return;
        selection.SetChecked(selection.Checks.Snapshot, node, checkBox.IsChecked);
        checkBox.IsChecked = node.IsChecked;
    }
}
