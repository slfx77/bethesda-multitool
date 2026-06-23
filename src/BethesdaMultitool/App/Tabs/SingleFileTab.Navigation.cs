using System.Collections.ObjectModel;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Semantic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BethesdaMultitool;

// ── Unified Navigation Location Types ──
#pragma warning disable S2094 // Abstract base for sealed record hierarchy (pattern matching)
/// <summary>Base type for an entry in the tab's unified back/forward navigation history.</summary>
internal abstract record UnifiedNavLocation;
#pragma warning restore S2094
/// <summary>A navigation history entry pointing at a node in the data browser tree.</summary>
internal sealed record DataBrowserNavLocation(EsmBrowserNode Node) : UnifiedNavLocation;

/// <summary>A navigation history entry capturing a world map view state.</summary>
internal sealed record WorldMapNavLocation(WorldMapControl.WorldNavState State) : UnifiedNavLocation;

/// <summary>A navigation history entry for the dialogue viewer: the topic, scroll position, and active filters.</summary>
internal sealed record DialogueNavLocation(
    uint TopicFormId,
    double ScrollPosition,
    uint? QuestFilter,
    uint? SpeakerFilter) : UnifiedNavLocation;

/// <summary>
///     Navigation: unified back/forward history across all tabs, and FormID link navigation.
/// </summary>
public sealed partial class SingleFileTab
{
    private const int UnifiedNavStackLimit = 100;
    private readonly Stack<UnifiedNavLocation> _unifiedBackStack = new();
    private readonly Stack<UnifiedNavLocation> _unifiedForwardStack = new();
    private Task? _formIdBuildTask;
    private Dictionary<uint, EsmBrowserNode>? _formIdNodeIndex;
    private bool _isNavigating;

    /// <summary>
    ///     Invalidation token for in-flight index builds. ResetNavigation bumps it; a build
    ///     orphaned by a reload/load-order change sees the mismatch and skips publication,
    ///     so it can never install a stale index (or _flatListBuilt) over the next load's state.
    /// </summary>
    private int _navIndexGeneration;

    /// <summary>
    ///     Builds a lookup from FormID to EsmBrowserNode by walking the data model tree.
    ///     Runs on a background thread (pre-build) or the UI thread (fallback). All inputs are
    ///     parameters captured at launch: re-reading instance fields mid-task would race a reload
    ///     that swaps them, and _session.EffectiveResolver enumerates LoadOrder.Entries — an
    ///     ObservableCollection the UI thread Clear()s/Add()s, so it must never be evaluated here.
    /// </summary>
    private void BuildFormIdNodeIndex(
        ObservableCollection<EsmBrowserNode> tree,
        RecordCollection? allRecords,
        FormIdResolver? resolver,
        Dictionary<uint, List<WorldPlacement>>? placementIndex,
        FormUsageIndex? usageIndex,
        IReadOnlyDictionary<uint, RaceRecord>? raceLookup,
        Dictionary<uint, List<(uint FormId, string? Name)>>? factionMembersIndex,
        int generation)
    {
        // Ensure all children are loaded (same as search index building)
        if (!_flatListBuilt)
        {
            EnsureAllChildrenLoaded(
                tree,
                allRecords,
                resolver,
                placementIndex,
                usageIndex,
                raceLookup,
                factionMembersIndex);
            if (generation != Volatile.Read(ref _navIndexGeneration)) return;
            _flatListBuilt = true;
        }

        var index = new Dictionary<uint, EsmBrowserNode>();

        foreach (var category in tree)
        {
            // Snapshot each level under its collection lock — a UI re-sort (SortRecordChildren)
            // can Clear+Add these same ObservableCollections while this background walk reads them.
            List<EsmBrowserNode> typeNodes;
            lock (category.Children)
            {
                typeNodes = category.Children.ToList();
            }

            foreach (var typeNode in typeNodes)
            {
                List<EsmBrowserNode> records;
                lock (typeNode.Children)
                {
                    records = typeNode.Children.ToList();
                }

                foreach (var record in records)
                {
                    if (record.FormIdHex != null &&
                        uint.TryParse(record.FormIdHex.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null,
                            out var formId))
                    {
                        index.TryAdd(formId, record);
                    }
                }
            }
        }

        if (generation != Volatile.Read(ref _navIndexGeneration)) return;
        _formIdNodeIndex = index;
    }

    // ── Unified Navigation ──

    /// <summary>Captures the current location across all tabs.</summary>
    private UnifiedNavLocation? CaptureCurrentLocation()
    {
        if (ReferenceEquals(SubTabView.SelectedItem, DataBrowserTab))
        {
            return _selectedBrowserNode?.NodeType == "Record"
                ? new DataBrowserNavLocation(_selectedBrowserNode)
                : null;
        }

        if (ReferenceEquals(SubTabView.SelectedItem, WorldMapTab))
        {
            return new WorldMapNavLocation(WorldMapControl.CaptureNavState());
        }

        if (ReferenceEquals(SubTabView.SelectedItem, DialogueViewerTab))
        {
            return _currentDialogueTopic != null
                ? new DialogueNavLocation(_currentDialogueTopic.TopicFormId,
                    DialogueConversationScroller.VerticalOffset,
                    _dialogueQuestFilter, _dialogueSpeakerFilter)
                : null;
        }

        return null;
    }

    /// <summary>Pushes a navigation location onto the unified back stack.</summary>
    private void PushUnifiedNav()
    {
        var location = CaptureCurrentLocation();
        if (location == null)
        {
            return;
        }

        if (_unifiedBackStack.Count >= UnifiedNavStackLimit)
        {
            // Trim oldest entry
            var items = _unifiedBackStack.Reverse().Skip(1).ToArray();
            _unifiedBackStack.Clear();
            foreach (var item in items)
            {
                _unifiedBackStack.Push(item);
            }
        }

        _unifiedBackStack.Push(location);
        _unifiedForwardStack.Clear();
        UpdateUnifiedNavButtons();
    }

    internal async void UnifiedBack_Click(object _, RoutedEventArgs _e)
    {
        if (_unifiedBackStack.Count == 0)
        {
            return;
        }

        var current = CaptureCurrentLocation();
        if (current != null)
        {
            _unifiedForwardStack.Push(current);
        }

        var target = _unifiedBackStack.Pop();
        await RestoreNavLocationAsync(target);
        UpdateUnifiedNavButtons();
    }

    internal async void UnifiedForward_Click(object _, RoutedEventArgs _e)
    {
        if (_unifiedForwardStack.Count == 0)
        {
            return;
        }

        var current = CaptureCurrentLocation();
        if (current != null)
        {
            _unifiedBackStack.Push(current);
        }

        var target = _unifiedForwardStack.Pop();
        await RestoreNavLocationAsync(target);
        UpdateUnifiedNavButtons();
    }

    private async Task RestoreNavLocationAsync(UnifiedNavLocation location)
    {
        _isNavigating = true;
        switch (location)
        {
            case DataBrowserNavLocation db:
                SubTabView.SelectedItem = DataBrowserTab;
                await SelectAndScrollToNodeAsync(db.Node);
                break;

            case WorldMapNavLocation wm:
                SubTabView.SelectedItem = WorldMapTab;
                WorldMapControl.RestoreNavState(wm.State);
                break;

            case DialogueNavLocation dl:
                SubTabView.SelectedItem = DialogueViewerTab;
                _dialogueQuestFilter = dl.QuestFilter;
                _dialogueSpeakerFilter = dl.SpeakerFilter;
                if (_session.DialogueFormIdIndex?.TryGetValue(dl.TopicFormId, out var topic) == true)
                {
                    NavigateToDialogueTopic(topic, pushToStack: false);
                    DialogueConversationScroller.ChangeView(null, dl.ScrollPosition, null,
                        disableAnimation: true);
                }

                break;
        }

        _isNavigating = false;
    }

    private void UpdateUnifiedNavButtons()
    {
        MainWindow.Instance?.SetNavButtonStates(
            _unifiedBackStack.Count > 0,
            _unifiedForwardStack.Count > 0);
    }

    /// <summary>Called by WorldMapControl.BeforeNavigate to push unified nav state.</summary>
    private void WorldMap_BeforeNavigate()
    {
        if (!_isNavigating)
        {
            PushUnifiedNav();
        }
    }

    // ── FormID Navigation ──

    /// <summary>
    ///     Navigates to the record with the given FormID, updating the detail panel and tree selection.
    /// </summary>
#pragma warning disable S3168 // async void is required for UI event handler lambdas
    private async void NavigateToFormId(uint formId)
#pragma warning restore S3168
    {
        // Push current location before navigating
        if (!_isNavigating)
        {
            PushUnifiedNav();
        }

        // Dialogue records (DIAL/INFO) are only meaningfully viewable in the Dialogue tab —
        // route there before falling through to the Records tree.
        if (TryNavigateToDialogueRecord(formId))
        {
            return;
        }

        // Switch to Records tab FIRST so progress bar is visible during loading
        if (!ReferenceEquals(SubTabView.SelectedItem, DataBrowserTab))
        {
            SubTabView.SelectedItem = DataBrowserTab;
        }

        // Ensure Records tree is populated before FormID lookup.
        // The tree lazily initializes on first tab visit via SelectionChanged,
        // but that handler is fire-and-forget — we must await it here.
        if (_esmBrowserTree == null && _session.HasEsmRecords)
        {
            await EnsureSemanticParseAsync();
            if (_session.SemanticResult != null)
            {
                await PopulateDataBrowserAsync();
            }
        }

        if (_formIdNodeIndex == null)
        {
            // Wait for background index build if still in progress
            if (_formIdBuildTask != null)
            {
                await _formIdBuildTask;
            }

            // Fallback: build synchronously if background didn't run. Safe to evaluate
            // _session.EffectiveResolver here — this path runs on the UI thread.
            if (_formIdNodeIndex == null && _esmBrowserTree is { } currentTree)
            {
                BuildFormIdNodeIndex(
                    currentTree,
                    _session.SemanticResult,
                    _session.EffectiveResolver,
                    _placementIndex,
                    _usageIndex,
                    _raceLookup,
                    _factionMembersIndex,
                    Volatile.Read(ref _navIndexGeneration));
            }
        }

        if (_formIdNodeIndex == null || !_formIdNodeIndex.TryGetValue(formId, out var targetNode))
        {
            // Show brief status for records not in the data browser tree
            SelectedRecordTitle.Text = $"Record 0x{formId:X8} is not available in Records";
            return;
        }

        _isNavigating = true;
        await SelectAndScrollToNodeAsync(targetNode);
        _isNavigating = false;
    }

    /// <summary>
    ///     Returns true if the FormID resolves to a known record in the browser tree
    ///     or is present in the FormIdMap (unparsed but exists in the file).
    /// </summary>
    private bool IsFormIdNavigable(uint formId)
    {
        if (_formIdNodeIndex != null && _formIdNodeIndex.ContainsKey(formId))
        {
            return true;
        }

        // Dialogue topics and INFOs are navigable via the Dialogue Browser
        if (_session.DialogueFormIdIndex?.ContainsKey(formId) == true)
        {
            return true;
        }

        // Also check FormIdMap — the record exists in the file even if not in the tree index yet
        return _session.AnalysisResult?.FormIdMap?.ContainsKey(formId) == true;
    }

    /// <summary>
    ///     Resets navigation state (called from ResetSubTabs).
    /// </summary>
    private void ResetNavigation()
    {
        // Invalidate any in-flight background index build BEFORE clearing state — the orphaned
        // task keeps running (we can't await it here), but the generation mismatch stops it from
        // publishing a stale _formIdNodeIndex/_flatListBuilt over the next load's tree.
        Interlocked.Increment(ref _navIndexGeneration);
        _unifiedBackStack.Clear();
        _unifiedForwardStack.Clear();
        _formIdNodeIndex = null;
        _formIdBuildTask = null;
        UpdateUnifiedNavButtons();
    }

    /// <summary>
    ///     Selects a node in the detail panel and scrolls the tree to make it visible.
    ///     Async to allow TreeView layout between expansion steps.
    /// </summary>
    private async Task SelectAndScrollToNodeAsync(EsmBrowserNode target)
    {
        // Update the detail panel
        SelectBrowserNode(target);

        // Clear any active search filter that might hide the target
        if (!string.IsNullOrEmpty(_currentSearchQuery))
        {
            _currentSearchQuery = "";
            EsmSearchBox.Text = "";
            RebuildTreeViewFromSource();
        }

        // Find the target's parent chain in the data model
        if (_esmBrowserTree == null) return;

        EsmBrowserNode? parentCategory = null;
        EsmBrowserNode? parentType = null;

        // Snapshot each level under its collection lock — the background _formIdBuildTask
        // (and the search pre-load) populate these same ObservableCollections concurrently;
        // a lock-free enumeration/Contains during their Adds is a torn-read GC hole.
#pragma warning disable S3267 // Nested loop with break - LINQ impractical here
        foreach (var category in _esmBrowserTree)
        {
            List<EsmBrowserNode> typeNodes;
            lock (category.Children)
            {
                typeNodes = [.. category.Children];
            }

            foreach (var typeNode in typeNodes)
#pragma warning restore S3267
            {
                bool containsTarget;
                lock (typeNode.Children)
                {
                    containsTarget = typeNode.Children.Contains(target);
                }

                if (containsTarget)
                {
                    parentCategory = category;
                    parentType = typeNode;
                    break;
                }
            }

            if (parentCategory != null) break;
        }

        if (parentCategory == null || parentType == null) return;

        // Walk TreeView nodes to find matching category
        TreeViewNode? categoryTreeNode = null;
        foreach (var rootNode in EsmTreeView.RootNodes)
        {
            if (rootNode.Content == parentCategory)
            {
                categoryTreeNode = rootNode;
                break;
            }
        }

        if (categoryTreeNode == null) return;

        // Expand category (triggers EsmTreeView_Expanding which creates type children)
        if (!categoryTreeNode.IsExpanded)
        {
            categoryTreeNode.IsExpanded = true;
            await Task.Delay(50); // Let TreeView create children
        }

        // Find the type TreeViewNode
        TreeViewNode? typeTreeNode = null;
        foreach (var child in categoryTreeNode.Children)
        {
            if (child.Content == parentType)
            {
                typeTreeNode = child;
                break;
            }
        }

        if (typeTreeNode == null) return;

        // Expand the type node (triggers progressive loading of record children)
        if (!typeTreeNode.IsExpanded)
        {
            typeTreeNode.IsExpanded = true;
            await Task.Delay(50); // Let TreeView create children
        }

        // Realize children up to the target's position so its TreeViewNode exists. For a record deep in a
        // large type group (e.g. STAT with tens of thousands of merged FO3+FNV records) this can be many
        // thousands of nodes, so realize them in YIELDING batches: each Task.Yield lets the dispatcher pump
        // input + layout between batches, so the snap stays responsive instead of freezing the UI thread.
        if (_pendingTreeLoads.TryGetValue(typeTreeNode, out var pending))
        {
            // Take ownership of this node's pending realization so the scroll-based loader
            // (LoadNextPendingBatches) can't double-add to it while we yield between batches.
            _pendingTreeLoads.Remove(typeTreeNode);
            var (allChildren, loadedCount) = pending;
            var targetDataIndex = allChildren.IndexOf(target);
            var loadUntil = targetDataIndex >= 0
                ? Math.Min(targetDataIndex + 50, allChildren.Count)
                : allChildren.Count;

            for (var i = loadedCount; i < loadUntil; i++)
            {
                var child = allChildren[i];
                typeTreeNode.Children.Add(new TreeViewNode
                {
                    Content = child,
                    HasUnrealizedChildren = child.HasUnrealizedChildren ||
                                            child.NodeType is "Category" or "RecordType"
                });

                if ((i - loadedCount + 1) % TreeNodeBatchSize == 0)
                {
                    await Task.Yield();
                }
            }

            // Re-register the remainder (if the target wasn't near the end) for scroll-based loading.
            if (loadUntil < allChildren.Count)
            {
                _pendingTreeLoads[typeTreeNode] = (allChildren, loadUntil);
            }
        }

        // Force layout so containers are realized for the newly added children
        EsmTreeView.UpdateLayout();

        // Find the record TreeViewNode and its index within the type
        TreeViewNode? recordTreeNode = null;
        var recordIndex = 0;
        for (var i = 0; i < typeTreeNode.Children.Count; i++)
        {
            if (typeTreeNode.Children[i].Content == target)
            {
                recordTreeNode = typeTreeNode.Children[i];
                recordIndex = i;
                break;
            }
        }

        if (recordTreeNode == null) return;

        // Select the node
        EsmTreeView.SelectedNode = recordTreeNode;

        // Scroll the TreeView's internal ScrollViewer to the approximate position
        // so the virtualized container for this node gets realized
        EnsureTreeScrollViewerHooked();
        if (_treeScrollViewer != null)
        {
            var flatIndex = ComputeFlatNodeIndex(categoryTreeNode, typeTreeNode, recordIndex);

            // Measure actual row height from a realized container; fall back to 32px
            var rowHeight = 32.0;
            foreach (var root in EsmTreeView.RootNodes)
            {
                if (EsmTreeView.ContainerFromNode(root) is FrameworkElement { ActualHeight: > 0 } measured)
                {
                    rowHeight = measured.ActualHeight;
                    break;
                }
            }

            var estimatedOffset = flatIndex * rowHeight;
            var centeredOffset = Math.Max(0, estimatedOffset - _treeScrollViewer.ViewportHeight / 2);
            _treeScrollViewer.ChangeView(null, centeredOffset, null, disableAnimation: true);
            await Task.Delay(50);
            EsmTreeView.UpdateLayout();
        }

        // Retry ContainerFromNode after scrolling near the target
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var container = EsmTreeView.ContainerFromNode(recordTreeNode) as UIElement;
            if (container != null)
            {
                container.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
                return;
            }

            await Task.Delay(100);
            EsmTreeView.UpdateLayout();
        }
    }

    /// <summary>
    ///     Computes the approximate flat (visible) index of a record node in the TreeView,
    ///     accounting for expanded/collapsed nodes. Used for scroll position estimation.
    /// </summary>
    private int ComputeFlatNodeIndex(TreeViewNode categoryNode, TreeViewNode typeNode, int recordIndex)
    {
        var index = 0;
        foreach (var root in EsmTreeView.RootNodes)
        {
            index++; // The category node itself
            if (root == categoryNode)
            {
                if (root.IsExpanded)
                {
                    foreach (var child in root.Children)
                    {
                        index++; // The type node
                        if (child == typeNode)
                        {
                            return index + recordIndex;
                        }

                        if (child.IsExpanded)
                        {
                            index += child.Children.Count;
                        }
                    }
                }

                return index;
            }

            if (root.IsExpanded)
            {
                foreach (var child in root.Children)
                {
                    index++; // Type node
                    if (child.IsExpanded)
                    {
                        index += child.Children.Count;
                    }
                }
            }
        }

        return index;
    }
}

