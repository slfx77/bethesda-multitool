using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Localization;
using Microsoft.UI.Xaml.Controls;
using Slfx77.Multitool.Core.Browsing;
using Slfx77.Multitool.WinUI.Localization;

namespace BethesdaMultitool;

/// <summary>
///     Owns the asset-type filter: which leaf kinds the tree and gallery admit, and the tree re-projection
///     that hides a folder with no visible leaf.
///     <para>
///         The checkboxes are the Shared <see cref="Slfx77.Multitool.WinUI.Browsing.BrowserTypeFilterControl" />
///         over a borrowed <see cref="BrowserTypeFilterSelection" />; BMT supplies the kinds present with their
///         counts (<see cref="AssetKindCensus" />, <see cref="AssetKindLabels" />) and derives the predicate,
///         <see cref="AssetKindFilter" />, from the selection on every change. Following the Shared selection, a
///         kind the next source also contains keeps its decision and a kind new to it arrives checked.
///     </para>
///     <para>
///         Checks live in the source selection, not in the tree, so hiding and re-showing a node never loses
///         its export check.
///     </para>
/// </summary>
public sealed partial class AssetBrowserTab
{
    private readonly BrowserTypeFilterSelection _assetTypes = new();
    private readonly Dictionary<AssetNode, TreeViewNode> _treeNodes = new(ReferenceEqualityComparer.Instance);
    private AssetNodeKind[] _presentKinds = [];
    private AssetKindFilter _kindFilter = AssetKindFilter.All;
    private AssetTreeVisibility? _treeVisibility;
    private bool _replacingKindOptions;

    /// <summary>Connects the Shared control to the retained selection once; the selection outlives every source.</summary>
    /// <param name="localization">The window's localization owner, which captions the control.</param>
    private void InitializeTypeFilter(LocalizationController localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        _assetTypes.Changed += AssetTypes_Changed;
        AssetTypeFilters.Initialize(_assetTypes, localization, "Assets.Types");
    }

    /// <summary>Lists the kinds present in the opened source and derives the predicate without re-projecting yet.</summary>
    /// <param name="root">The opened source's tree root.</param>
    /// <remarks>The caller applies the result: the tree is not attached yet, so a re-projection here would be wasted.</remarks>
    private void RefreshKindOptions(AssetNode root)
    {
        var census = AssetKindCensus.Present(root);
        _presentKinds = census.Select(entry => entry.Kind).ToArray();
        _replacingKindOptions = true;
        try { _assetTypes.ReplaceOptions(AssetKindLabels.CreateFilterOptions(census)); }
        finally { _replacingKindOptions = false; }
        _kindFilter = DeriveKindFilter();
    }

    /// <summary>Applies a user's checkbox change to the tree and gallery.</summary>
    /// <param name="sender">The retained selection.</param>
    /// <param name="args">The committed-state notification.</param>
    private void AssetTypes_Changed(object? sender, EventArgs args)
    {
        if (_disposed || _replacingKindOptions) return;
        SetKindFilter(DeriveKindFilter());
    }

    /// <summary>The predicate the current selection describes: a present kind that is not selected is excluded.</summary>
    /// <returns>A filter over the kinds the current source lists.</returns>
    private AssetKindFilter DeriveKindFilter() =>
        AssetKindFilter.All.WithIncluded(_presentKinds, AssetKindLabels.SelectedKinds(_assetTypes.SelectedIds));

    /// <summary>Detaches the selection and retires the Shared control's bindings.</summary>
    private void DisposeTypeFilter()
    {
        _assetTypes.Changed -= AssetTypes_Changed;
        AssetTypeFilters.Dispose();
    }

    /// <summary>Replaces the retained filter and re-projects tree and gallery.</summary>
    /// <param name="next">The replacement filter; an equal filter is a no-op.</param>
    private void SetKindFilter(AssetKindFilter next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (next.Equals(_kindFilter)) return;
        _kindFilter = next;
        ApplyKindFilter();
    }

    /// <summary>Re-projects the tree, then the gallery, under the retained filter.</summary>
    private void ApplyKindFilter()
    {
        ApplyTreeFilter();
        if (_galleryProjection?.TrySetKinds(_kindFilter) == true)
        {
            SynchronizeGallery();
            UpdateGalleryStatus();
        }
    }

    /// <summary>
    ///     Rebuilds only the folders whose visible child sequence changed, reusing the retained
    ///     <see cref="TreeViewNode" /> instances so expansion survives a hide-and-show round trip, then
    ///     re-selects the previously selected node if it is still visible.
    /// </summary>
    private void ApplyTreeFilter()
    {
        if (_session is not { } session || _treeNodes.Count == 0)
        {
            _treeVisibility = null;
            UpdateTreeStatus();
            return;
        }

        var visibility = AssetTreeVisibility.Compute(session.Root, _kindFilter);
        _treeVisibility = visibility;
        var selected = AssetTreeView.SelectedNode?.Content as AssetNode;
        foreach (var (node, treeNode) in _treeNodes)
        {
            if (node.Kind != AssetNodeKind.Folder) continue;
            var visibleChildren = visibility.VisibleChildren(node);
            if (SameChildren(treeNode.Children, visibleChildren)) continue;
            // A node whose children are cleared collapses itself; put its expansion back once it has children again.
            var wasExpanded = treeNode.IsExpanded;
            treeNode.Children.Clear();
            foreach (var child in visibleChildren) treeNode.Children.Add(_treeNodes[child]);
            if (wasExpanded && treeNode.Children.Count > 0) treeNode.IsExpanded = true;
        }

        // Clearing a folder's children drops a selection inside it; restore it silently so the
        // preview, which is independent of the tree, is not re-run for the same node.
        if (selected is not null && visibility.IsVisible(selected) &&
            !ReferenceEquals(AssetTreeView.SelectedNode?.Content, selected) &&
            _treeNodes.TryGetValue(selected, out var selectedTreeNode))
        {
            _mirroringTreeSelection = true;
            try { AssetTreeView.SelectedNode = selectedTreeNode; }
            finally { _mirroringTreeSelection = false; }
        }

        UpdateTreeStatus();
    }

    /// <summary>Whether a folder's presented children already are exactly the visible ones, by reference.</summary>
    /// <param name="children">The tree node's current children.</param>
    /// <param name="visible">The computed visible children in builder order.</param>
    private static bool SameChildren(IList<TreeViewNode> children, IReadOnlyList<AssetNode> visible)
    {
        if (children.Count != visible.Count) return false;
        for (var i = 0; i < visible.Count; i++)
        {
            if (!ReferenceEquals(children[i].Content, visible[i])) return false;
        }

        return true;
    }

    /// <summary>Shows the source label with its file count, and the filtered count while the filter is active.</summary>
    private void UpdateTreeStatus()
    {
        if (_session is not { } session || _selection is not { } selection) return;
        // An exclusion retained for a kind this source does not contain hides nothing, so the plain count shows.
        if (_treeVisibility is { } visibility && visibility.VisibleFileCount != visibility.TotalFileCount)
        {
            RuntimeLocalization.SetText(AssetTreeStatusText, "AssetTypeFilter_TreeStatus", session.SourceLabel,
                visibility.VisibleFileCount, visibility.TotalFileCount);
            return;
        }

        RuntimeLocalization.SetRaw(AssetTreeStatusText, TextBlock.TextProperty,
            $"{session.SourceLabel} - {selection.FileCount:N0} file(s)");
    }

    /// <summary>
    ///     Drops the per-source tree registry. The Shared selection keeps its options so the next source's
    ///     refresh retains the decisions for the kinds it shares with this one.
    /// </summary>
    private void ClearTypeFilterSource()
    {
        _treeNodes.Clear();
        _treeVisibility = null;
        _presentKinds = [];
    }
}
