using System.Collections.ObjectModel;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FalloutXbox360Utils;

/// <summary>
///     Tree building: ESM browser tree events, filtering, search, tree UI helpers
/// </summary>
public sealed partial class SingleFileTab
{
    private const int TreeNodeBatchSize = 200;

    /// <summary>Tracks expanded nodes that have more children to load on scroll.</summary>
    private readonly Dictionary<TreeViewNode, (ObservableCollection<EsmBrowserNode> AllChildren, int LoadedCount)>
        _pendingTreeLoads = new();

    private ScrollViewer? _treeScrollViewer;

    #region TreeView Events

    private void EsmTreeView_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.Content is not EsmBrowserNode browserNode) return;
        if (args.Node.Children.Count > 0) return; // Already has TreeViewNode children

        // Load data children if needed
        if (browserNode.NodeType == "Category" && browserNode.Children.Count == 0)
        {
            EsmBrowserTreeBuilder.LoadCategoryChildren(browserNode);
        }
        else if (browserNode.NodeType == "RecordType" && browserNode.Children.Count == 0)
        {
            EsmBrowserTreeBuilder.LoadRecordTypeChildren(
                browserNode,
                _session.Resolver);
        }

        // Add child TreeViewNodes with progressive loading for large sets
        AddChildNodesProgressively(args.Node, browserNode.Children);
        EnsureTreeScrollViewerHooked();
    }

    private void EsmTreeView_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode treeNode) return;
        if (treeNode.Content is not EsmBrowserNode browserNode) return;

        // For Category/RecordType/expandable Record nodes, expand on click (not just chevron)
        if (browserNode.NodeType is "Category" or "RecordType"
            || (browserNode.NodeType == "Record" && browserNode.HasUnrealizedChildren))
        {
            if (!treeNode.IsExpanded)
            {
                // Load children if not yet loaded
                if (treeNode.Children.Count == 0)
                {
                    if (browserNode.NodeType == "Category" && browserNode.Children.Count == 0)
                        EsmBrowserTreeBuilder.LoadCategoryChildren(browserNode);
                    else if (browserNode.NodeType == "RecordType" && browserNode.Children.Count == 0)
                        EsmBrowserTreeBuilder.LoadRecordTypeChildren(
                            browserNode,
                            _session.Resolver);
                    AddChildNodesProgressively(treeNode, browserNode.Children);
                    EnsureTreeScrollViewerHooked();
                }

                treeNode.IsExpanded = true;
            }
            else
            {
                treeNode.IsExpanded = false;
            }
        }

        SelectBrowserNode(browserNode);
    }

    #endregion

    #region Progressive Loading (scroll-based)

    /// <summary>
    ///     Adds child nodes to the tree with progressive loading for large sets.
    ///     Loads the first batch immediately; remaining items load automatically as the user scrolls.
    /// </summary>
    private void AddChildNodesProgressively(
        TreeViewNode parentNode,
        ObservableCollection<EsmBrowserNode> children)
    {
        var total = children.Count;
        var batchEnd = Math.Min(TreeNodeBatchSize, total);

        for (var i = 0; i < batchEnd; i++)
        {
            var child = children[i];
            var childNode = new TreeViewNode
            {
                Content = child,
                HasUnrealizedChildren = child.HasUnrealizedChildren ||
                                        child.NodeType is "Category" or "RecordType"
            };
            parentNode.Children.Add(childNode);
        }

        // Track remaining items for scroll-based loading
        if (batchEnd < total)
        {
            _pendingTreeLoads[parentNode] = (children, batchEnd);
        }
    }

    /// <summary>
    ///     Finds and hooks the TreeView's internal ScrollViewer to detect scroll position.
    /// </summary>
    private void EnsureTreeScrollViewerHooked()
    {
        if (_treeScrollViewer is not null) return;

        _treeScrollViewer = FindDescendant<ScrollViewer>(EsmTreeView);
        if (_treeScrollViewer is not null)
        {
            _treeScrollViewer.ViewChanged += TreeScrollViewer_ViewChanged;
        }
    }

    private void TreeScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_pendingTreeLoads.Count == 0 || _treeScrollViewer is null) return;

        // Load more when within 2x viewport height of the bottom
        var remaining = _treeScrollViewer.ExtentHeight
                        - _treeScrollViewer.VerticalOffset
                        - _treeScrollViewer.ViewportHeight;
        var threshold = _treeScrollViewer.ViewportHeight * 2;

        if (remaining <= threshold)
        {
            LoadNextPendingBatches();
        }
    }

    private void LoadNextPendingBatches()
    {
        var completed = new List<TreeViewNode>();

        foreach (var (parentNode, (allChildren, loadedCount)) in _pendingTreeLoads)
        {
            // Skip collapsed nodes — they aren't contributing to scroll extent
            if (!parentNode.IsExpanded)
            {
                continue;
            }

            var batchEnd = Math.Min(loadedCount + TreeNodeBatchSize, allChildren.Count);

            for (var i = loadedCount; i < batchEnd; i++)
            {
                var child = allChildren[i];
                var childNode = new TreeViewNode
                {
                    Content = child,
                    HasUnrealizedChildren = child.HasUnrealizedChildren ||
                                            child.NodeType is "Category" or "RecordType"
                };
                parentNode.Children.Add(childNode);
            }

            if (batchEnd >= allChildren.Count)
            {
                completed.Add(parentNode);
            }
            else
            {
                _pendingTreeLoads[parentNode] = (allChildren, batchEnd);
            }
        }

        foreach (var key in completed)
        {
            _pendingTreeLoads.Remove(key);
        }
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var descendant = FindDescendant<T>(child);
            if (descendant is not null) return descendant;
        }

        return null;
    }

    #endregion

    #region Search

    private async void EsmSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = EsmSearchBox.Text?.Trim() ?? "";

        if (_esmBrowserTree == null || _esmBrowserTree.Count == 0)
        {
            return;
        }

        _currentSearchQuery = query;

        // Cancel and dispose any pending search
#pragma warning disable S6966 // Awaiting CancelAsync not feasible in synchronous event handler
        _searchDebounceToken?.Cancel();
#pragma warning restore S6966
        _searchDebounceToken?.Dispose();
        _searchDebounceToken = new CancellationTokenSource();
        var token = _searchDebounceToken.Token;

        if (string.IsNullOrEmpty(query))
        {
            // Restore full tree view immediately (no debounce for clearing)
            RebuildTreeViewFromSource();
            StatusTextBlock.Text = "";
            return;
        }

        // Debounce: wait 250ms after user stops typing before searching
        try
        {
            await Task.Delay(250, token);
        }
        catch (TaskCanceledException)
        {
            return; // User typed more, abort this search
        }

        // Ensure all children are loaded before filtering (lazy loading)
        if (!_flatListBuilt)
        {
            StatusTextBlock.Text = "Building search index...";
            var resolver = _session.Resolver;
            var tree = _esmBrowserTree;
            await Task.Run(() => EnsureAllChildrenLoaded(tree, resolver), token);
            _flatListBuilt = true;
        }

        // Filter tree and rebuild with only matching records
        var matchCount = FilterAndRebuildTreeView(query);
        StatusTextBlock.Text = matchCount > 0
            ? $"Found {matchCount:N0} matching records"
            : "No matches found";
    }

    private void EsmSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_esmBrowserTree == null || _esmBrowserTree.Count == 0) return;

        var mode = EsmSortComboBox.SelectedIndex switch
        {
            1 => EsmBrowserTreeBuilder.RecordSortMode.EditorId,
            2 => EsmBrowserTreeBuilder.RecordSortMode.FormId,
            _ => EsmBrowserTreeBuilder.RecordSortMode.Name
        };

        EsmBrowserTreeBuilder.SortRecordChildren(_esmBrowserTree, mode);

        // Rebuild tree view with new sort order, respecting any active filter
        if (!string.IsNullOrEmpty(_currentSearchQuery))
        {
            // Re-apply filter with new sort order
            FilterAndRebuildTreeView(_currentSearchQuery);
        }
        else
        {
            // No filter - rebuild full tree
            RebuildTreeViewFromSource();
        }
    }

    #endregion

    #region Tree Filtering

    private static void EnsureAllChildrenLoaded(
        ObservableCollection<EsmBrowserNode> tree,
        FormIdResolver? resolver)
    {
        // Snapshot collections before iterating — UI thread may modify Children
        // concurrently through tree expansion (EsmTreeView_Expanding).
        // Null-check nodes defensively since concurrent modification can produce nulls.
        var categories = tree.ToList();
        foreach (var categoryNode in categories)
        {
            if (categoryNode?.Children == null) continue;

            if (categoryNode.HasUnrealizedChildren && categoryNode.Children.Count == 0)
            {
                EsmBrowserTreeBuilder.LoadCategoryChildren(categoryNode);
            }

            var typeNodes = categoryNode.Children.ToList();
            foreach (var typeNode in typeNodes)
            {
                if (typeNode?.Children == null) continue;

                if (typeNode.HasUnrealizedChildren && typeNode.Children.Count == 0)
                {
                    EsmBrowserTreeBuilder.LoadRecordTypeChildren(typeNode, resolver);
                }
            }
        }
    }

    private int FilterAndRebuildTreeView(string query, int maxResults = 200)
    {
        if (_esmBrowserTree == null) return 0;

        _pendingTreeLoads.Clear(); // Stale refs after rebuild

        var totalMatches = 0;
        EsmTreeView.RootNodes.Clear();

        foreach (var categoryNode in _esmBrowserTree)
        {
            if (totalMatches >= maxResults) break; // Stop if limit reached

            var filteredCategoryNode = FilterCategoryNode(categoryNode, query, ref totalMatches, maxResults);
            if (filteredCategoryNode != null)
            {
                EsmTreeView.RootNodes.Add(filteredCategoryNode);
            }
        }

        return totalMatches;
    }

    private static TreeViewNode? FilterCategoryNode(
        EsmBrowserNode category, string query, ref int totalMatches, int maxResults)
    {
        var matchingTypeNodes = new List<TreeViewNode>();
        var categoryMatchCount = 0;

        foreach (var typeNode in category.Children)
        {
            if (totalMatches >= maxResults) break; // Stop if limit reached

            // Filter records within this type (preserves existing sort order)
            // Only take up to remaining limit to avoid processing extra records
            var matchingRecords = typeNode.Children
                .Where(r => MatchesSearchQuery(r, query))
                .Take(maxResults - totalMatches)
                .ToList();

            if (matchingRecords.Count > 0)
            {
                totalMatches += matchingRecords.Count;
                categoryMatchCount += matchingRecords.Count;

                // Create type wrapper with filtered count in display name
                var baseName = typeNode.DisplayName.Contains('(')
                    ? typeNode.DisplayName[..typeNode.DisplayName.LastIndexOf('(')].TrimEnd()
                    : typeNode.DisplayName;
                var filteredTypeNode = new EsmBrowserNode
                {
                    DisplayName = $"{baseName} ({matchingRecords.Count:N0})",
                    IconGlyph = typeNode.IconGlyph,
                    NodeType = typeNode.NodeType
                };

                var typeTreeNode = new TreeViewNode
                {
                    Content = filteredTypeNode,
                    HasUnrealizedChildren = false,
                    IsExpanded = true // Auto-expand to show matches
                };

                foreach (var record in matchingRecords)
                {
                    typeTreeNode.Children.Add(new TreeViewNode { Content = record });
                }

                matchingTypeNodes.Add(typeTreeNode);
            }
        }

        if (matchingTypeNodes.Count > 0)
        {
            // Create category wrapper with filtered count in display name
            var baseName = category.DisplayName.Contains('(')
                ? category.DisplayName[..category.DisplayName.LastIndexOf('(')].TrimEnd()
                : category.DisplayName;
            var filteredCategory = new EsmBrowserNode
            {
                DisplayName = $"{baseName} ({categoryMatchCount:N0})",
                IconGlyph = category.IconGlyph,
                NodeType = category.NodeType
            };

            var categoryTreeNode = new TreeViewNode
            {
                Content = filteredCategory,
                HasUnrealizedChildren = false,
                IsExpanded = true // Auto-expand to show matches
            };

            foreach (var typeNode in matchingTypeNodes)
            {
                categoryTreeNode.Children.Add(typeNode);
            }

            return categoryTreeNode;
        }

        return null;
    }

    private static bool MatchesSearchQuery(EsmBrowserNode node, string query)
    {
        return (node.DisplayName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
               (node.EditorId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
               (node.FormIdHex?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
               (node.Detail?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void RebuildTreeViewFromSource()
    {
        if (_esmBrowserTree == null) return;

        _pendingTreeLoads.Clear(); // Stale refs after rebuild

        EsmTreeView.RootNodes.Clear();
        foreach (var node in _esmBrowserTree)
        {
            var treeNode = new TreeViewNode { Content = node, HasUnrealizedChildren = node.HasUnrealizedChildren };
            EsmTreeView.RootNodes.Add(treeNode);
        }
    }

    #endregion
}
