using System.Collections.ObjectModel;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FalloutXbox360Utils;

/// <summary>
/// Tree building: ESM browser tree events, filtering, search, tree UI helpers
/// </summary>
public sealed partial class SingleFileTab
{
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
                _session.AnalysisResult?.FormIdMap,
                _session.SemanticResult?.FormIdToDisplayName);
        }

        // Add child TreeViewNodes
        foreach (var child in browserNode.Children)
        {
            var childNode = new TreeViewNode { Content = child, HasUnrealizedChildren = child.HasUnrealizedChildren };
            args.Node.Children.Add(childNode);
        }
    }

    private void EsmTreeView_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode treeNode) return;
        if (treeNode.Content is not EsmBrowserNode browserNode) return;

        // For Category/RecordType nodes, expand on click (not just chevron)
        if (browserNode.NodeType is "Category" or "RecordType")
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
                            _session.AnalysisResult?.FormIdMap,
                            _session.SemanticResult?.FormIdToDisplayName);

                    foreach (var child in browserNode.Children)
                    {
                        var childNode = new TreeViewNode
                        {
                            Content = child,
                            HasUnrealizedChildren = child.HasUnrealizedChildren ||
                                                    child.NodeType is "Category" or "RecordType"
                        };
                        treeNode.Children.Add(childNode);
                    }
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
            var lookup = _session.AnalysisResult?.FormIdMap;
            var displayNameLookup = _session.SemanticResult?.FormIdToDisplayName;
            var tree = _esmBrowserTree;
            await Task.Run(() => EnsureAllChildrenLoaded(tree, lookup, displayNameLookup), token);
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
        Dictionary<uint, string>? lookup,
        Dictionary<uint, string>? displayNameLookup)
    {
        // Snapshot collections before iterating — UI thread may modify Children
        // concurrently through tree expansion (EsmTreeView_Expanding)
        var categories = tree.ToList();
        foreach (var categoryNode in categories)
        {
            if (categoryNode.HasUnrealizedChildren && categoryNode.Children.Count == 0)
            {
                EsmBrowserTreeBuilder.LoadCategoryChildren(categoryNode);
            }

            var typeNodes = categoryNode.Children.ToList();
            foreach (var typeNode in typeNodes)
            {
                if (typeNode.HasUnrealizedChildren && typeNode.Children.Count == 0)
                {
                    EsmBrowserTreeBuilder.LoadRecordTypeChildren(typeNode, lookup, displayNameLookup);
                }
            }
        }
    }

    private int FilterAndRebuildTreeView(string query, int maxResults = 200)
    {
        if (_esmBrowserTree == null) return 0;

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

                // Create type node with only matching children
                var typeTreeNode = new TreeViewNode
                {
                    Content = typeNode,
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
            // Create category node with only types that have matches
            var categoryTreeNode = new TreeViewNode
            {
                Content = category,
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
               (node.FormIdHex?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void RebuildTreeViewFromSource()
    {
        if (_esmBrowserTree == null) return;

        EsmTreeView.RootNodes.Clear();
        foreach (var node in _esmBrowserTree)
        {
            var treeNode = new TreeViewNode { Content = node, HasUnrealizedChildren = node.HasUnrealizedChildren };
            EsmTreeView.RootNodes.Add(treeNode);
        }
    }

    #endregion
}
