using System.Collections.ObjectModel;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BethesdaMultitool;

/// <summary>
///     Tree building: ESM browser tree events, filtering, search, tree UI helpers
/// </summary>
public sealed partial class SingleFileTab
{
    private const int TreeNodeBatchSize = 200;

    /// <summary>Max batches loaded per scroll settle (look-ahead), one batch per dispatcher tick.
    /// A FIXED cap — never driven by the post-insert <c>ExtentHeight</c>, which is stale until the
    /// next layout pass and would otherwise cascade through an entire huge section in back-to-back
    /// ticks, freezing the UI thread (and starving container realization → stale rows).</summary>
    private const int MaxBatchesPerScroll = 6;

    /// <summary>Tracks expanded nodes that have more children to load on scroll.</summary>
    private readonly Dictionary<TreeViewNode, (ObservableCollection<EsmBrowserNode> AllChildren, int LoadedCount)>
        _pendingTreeLoads = new();

    private ScrollViewer? _treeScrollViewer;

    /// <summary>
    ///     In-flight guard for the coalesced, dispatcher-deferred tree-node load. Multiple
    ///     <see cref="TreeScrollViewer_ViewChanged" /> triggers collapse into a single enqueued
    ///     load so node insertion never runs synchronously inside the scroll/layout pass.
    /// </summary>
    private bool _treeLoadScheduled;

    /// <summary>RecordType nodes whose (expensive) data-model children are being built on a background
    /// thread right now. Guards against starting a second build if the user re-expands mid-load.
    /// UI-thread-only access (expand handlers + the post-await continuation), so no lock needed.</summary>
    private readonly HashSet<EsmBrowserNode> _recordTypeLoadsInFlight = [];

    /// <summary>Remaining look-ahead batches for the current scroll settle (see
    /// <see cref="MaxBatchesPerScroll" />). Decremented per loaded batch; a fresh settle refills it.</summary>
    private int _treeLoadBudget;

    #region TreeView Events

    private void EsmTreeView_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.Content is not EsmBrowserNode browserNode) return;
        if (args.Node.Children.Count > 0) return; // Already has TreeViewNode children

        // RecordType nodes can hold tens of thousands of records; building their data-model children +
        // sort on the UI thread froze the tree on expand. Realize them WITHOUT blocking (synchronously
        // when a background pre-load already built them, otherwise on a worker thread). See
        // RealizeRecordTypeChildren.
        if (browserNode.NodeType == "RecordType")
        {
            _ = RealizeRecordTypeChildren(args.Node, browserNode);
            return;
        }

        // Categories are cheap (a handful of type sub-nodes) — build inline.
        if (browserNode.NodeType == "Category" && browserNode.Children.Count == 0)
        {
            EsmBrowserTreeBuilder.LoadCategoryChildren(browserNode);
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
                    if (browserNode.NodeType == "RecordType")
                    {
                        // Off-thread build (or sync realize if already built) — never freezes the UI.
                        _ = RealizeRecordTypeChildren(treeNode, browserNode);
                    }
                    else
                    {
                        if (browserNode.NodeType == "Category" && browserNode.Children.Count == 0)
                            EsmBrowserTreeBuilder.LoadCategoryChildren(browserNode);
                        AddChildNodesProgressively(treeNode, browserNode.Children);
                        EnsureTreeScrollViewerHooked();
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

    /// <summary>
    ///     Realizes a RecordType node's child records into the tree WITHOUT freezing the UI. The
    ///     data-model build (<see cref="EsmBrowserTreeBuilder.LoadRecordTypeChildren" />) walks every
    ///     record — tens of thousands for a large DMP — extracting identity/name and sorting, which froze
    ///     the UI thread when run inline from the expand handlers. Here: if the background pre-load
    ///     (<c>BuildFormIdNodeIndex → EnsureAllChildrenLoaded</c>) already built the children, the first
    ///     batch realizes synchronously (cheap); otherwise a "Loading…" placeholder shows while the build
    ///     runs on a worker thread, then the real children realize on completion. Re-entry is guarded so a
    ///     collapse/re-expand mid-load can't start a second build.
    /// </summary>
    private async Task RealizeRecordTypeChildren(TreeViewNode treeNode, EsmBrowserNode browserNode)
    {
        if (treeNode.Children.Count > 0) return; // already realized (real children or a placeholder)

        // Fast path: the background pre-load already built the data-model children — realizing the first
        // batch of TreeViewNodes is cheap, so do it inline (no worker hop, no placeholder flicker).
        bool alreadyBuilt;
        lock (browserNode.Children)
        {
            alreadyBuilt = browserNode.Children.Count > 0;
        }

        if (alreadyBuilt)
        {
            AddChildNodesProgressively(treeNode, browserNode.Children);
            EnsureTreeScrollViewerHooked();
            return;
        }

        // Slow path: build off the UI thread. Guard against a second concurrent build for this node.
        if (!_recordTypeLoadsInFlight.Add(browserNode)) return;

        var placeholder = new TreeViewNode
        {
            Content = new EsmBrowserNode { DisplayName = "Loading…", NodeType = "Loading", IconGlyph = "" }
        };
        treeNode.Children.Add(placeholder);

        // Capture UI-thread state for the worker (instance fields can change on reload/load-order edits).
        var semanticResult = _session.SemanticResult;
        var resolver = _session.EffectiveResolver;
        var placementIndex = _placementIndex;
        var usageIndex = _usageIndex;
        var raceLookup = _raceLookup;
        var factionMembersIndex = _factionMembersIndex;

        try
        {
            await Task.Run(() => EsmBrowserTreeBuilder.LoadRecordTypeChildren(
                browserNode, semanticResult, resolver, placementIndex, usageIndex, raceLookup, factionMembersIndex));
        }
        catch
        {
            // Build failed — leave the node empty rather than fault the background load.
        }
        finally
        {
            _recordTypeLoadsInFlight.Remove(browserNode);
        }

        // Back on the UI thread (await captured the WinUI dispatcher context). Swap the placeholder for
        // the real children, unless another path already realized them.
        treeNode.Children.Remove(placeholder);
        if (treeNode.Children.Count == 0)
        {
            AddChildNodesProgressively(treeNode, browserNode.Children);
            EnsureTreeScrollViewerHooked();
        }
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
        // Snapshot under the collection's lock: a background pre-load task may still be populating
        // this same ObservableCollection. Indexing it mid-mutation is a torn read (GC hole).
        List<EsmBrowserNode> snapshot;
        lock (children)
        {
            snapshot = children.ToList();
        }

        var total = snapshot.Count;
        var batchEnd = Math.Min(TreeNodeBatchSize, total);

        for (var i = 0; i < batchEnd; i++)
        {
            var child = snapshot[i];
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

        // Skip intermediate (inertial/momentum) scroll events: inserting nodes mid-scroll mutates
        // the flattened tree + forces a layout pass against a shifting ExtentHeight, so the visible
        // rows mismatch the thumb until the scroll settles, and any UI-thread work during momentum
        // starves container realization. Act only on the settled frame.
        if (e.IsIntermediate) return;

        if (IsNearTreeBottom())
        {
            _treeLoadBudget = MaxBatchesPerScroll; // refresh look-ahead for this settle
            ScheduleLoadNextPendingBatches();
        }
    }

    /// <summary>Within 2× viewport height of the bottom (and there is more to load).</summary>
    private bool IsNearTreeBottom()
    {
        if (_treeScrollViewer is null || _pendingTreeLoads.Count == 0) return false;
        var remaining = _treeScrollViewer.ExtentHeight
                        - _treeScrollViewer.VerticalOffset
                        - _treeScrollViewer.ViewportHeight;
        return remaining <= _treeScrollViewer.ViewportHeight * 2;
    }

    /// <summary>
    ///     Coalesces load requests and runs the actual node insertion OFF the synchronous
    ///     scroll/layout pass via the dispatcher, ONE batch per tick, so a fast flick can't thrash
    ///     layout and a huge section can't freeze the UI. The in-flight flag collapses multiple
    ///     triggers into one enqueued load; the per-settle budget (<see cref="_treeLoadBudget" />)
    ///     bounds the look-ahead. Continuation is gated on the budget counter — NOT on the
    ///     post-insert <c>ExtentHeight</c>, which is stale until the next layout pass and would
    ///     cascade through the whole section in back-to-back ticks (the freeze + stale-rows bug).
    /// </summary>
    private void ScheduleLoadNextPendingBatches()
    {
        if (_treeLoadScheduled) return;
        _treeLoadScheduled = true;

        // DispatcherQueue goes null once the tab content subtree unloads (TabView swaps content);
        // fall back to a single direct load so pending batches are never stranded.
        if (DispatcherQueue is null)
        {
            _treeLoadScheduled = false;
            LoadNextPendingBatches();
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            _treeLoadScheduled = false;
            var added = LoadNextPendingBatches();
            // Continue ONLY while the fixed per-settle budget remains and there was something to
            // load. One batch per tick keeps the UI responsive; the budget (not the stale extent)
            // prevents the runaway cascade that loaded an entire section and froze the app.
            if (added > 0 && --_treeLoadBudget > 0)
            {
                ScheduleLoadNextPendingBatches();
            }
        });
    }

    /// <summary>Loads one batch of child nodes into each expanded pending parent. Returns the number
    /// of nodes added this call (0 = nothing left to load / all pending parents collapsed).</summary>
    private int LoadNextPendingBatches()
    {
        var completed = new List<TreeViewNode>();
        var added = 0;

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
                added++;
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

        return added;
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
            StatusTextBlock.Text = Strings.Status_BuildingSearchIndex;
            var resolver = _session.EffectiveResolver;
            var tree = _esmBrowserTree;
            var allRecords = _session.SemanticResult;
            var placements = _placementIndex;
            var usageIndex = _usageIndex;
            await Task.Run(() => EnsureAllChildrenLoaded(
                    tree,
                    allRecords,
                    resolver,
                    placements,
                    usageIndex,
                    _raceLookup,
                    _factionMembersIndex),
                token);
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
        RecordCollection? allRecords,
        FormIdResolver? resolver,
        Dictionary<uint, List<WorldPlacement>>? placementIndex = null,
        FormUsageIndex? usageIndex = null,
        IReadOnlyDictionary<uint, RaceRecord>? raceLookup = null,
        Dictionary<uint, List<(uint FormId, string? Name)>>? factionMembersIndex = null)
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

            List<EsmBrowserNode> typeNodes;
            lock (categoryNode.Children)
            {
                typeNodes = categoryNode.Children.ToList();
            }
            foreach (var typeNode in typeNodes)
            {
                if (typeNode?.Children == null) continue;

                if (typeNode.HasUnrealizedChildren && typeNode.Children.Count == 0)
                {
                    EsmBrowserTreeBuilder.LoadRecordTypeChildren(typeNode, allRecords, resolver,
                        placementIndex, usageIndex, raceLookup, factionMembersIndex);
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

        // Snapshot each level under its collection lock — the background pre-load task may still be
        // populating these ObservableCollections while this UI-thread filter enumerates them.
        List<EsmBrowserNode> typeNodes;
        lock (category.Children)
        {
            typeNodes = category.Children.ToList();
        }

        foreach (var typeNode in typeNodes)
        {
            if (totalMatches >= maxResults) break; // Stop if limit reached

            List<EsmBrowserNode> typeChildren;
            lock (typeNode.Children)
            {
                typeChildren = typeNode.Children.ToList();
            }

            // Filter records within this type (preserves existing sort order)
            // Only take up to remaining limit to avoid processing extra records
            var matchingRecords = typeChildren
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

