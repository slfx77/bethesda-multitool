using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FalloutXbox360Utils;

/// <summary>
///     Navigation: FormID link navigation with back/forward history stacks.
/// </summary>
public sealed partial class SingleFileTab
{
    private readonly Stack<EsmBrowserNode> _navBackStack = new();
    private readonly Stack<EsmBrowserNode> _navForwardStack = new();
    private Task? _formIdBuildTask;
    private Dictionary<uint, EsmBrowserNode>? _formIdNodeIndex;
    private bool _isNavigating;

    /// <summary>
    ///     Builds a lookup from FormID to EsmBrowserNode by walking the data model tree.
    ///     Called lazily on first link click.
    /// </summary>
    private void BuildFormIdNodeIndex()
    {
        if (_esmBrowserTree == null) return;

        // Ensure all children are loaded (same as search index building)
        if (!_flatListBuilt)
        {
            EnsureAllChildrenLoaded(
                _esmBrowserTree,
                _session.AnalysisResult?.FormIdMap,
                _session.SemanticResult?.FormIdToDisplayName);
            _flatListBuilt = true;
        }

        var index = new Dictionary<uint, EsmBrowserNode>();

        foreach (var category in _esmBrowserTree)
        {
            foreach (var typeNode in category.Children)
            {
                foreach (var record in typeNode.Children)
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

        _formIdNodeIndex = index;
    }

    /// <summary>
    ///     Navigates to the record with the given FormID, updating the detail panel and tree selection.
    /// </summary>
#pragma warning disable S3168 // async void is required for UI event handler lambdas
    private async void NavigateToFormId(uint formId)
#pragma warning restore S3168
    {
        // Switch to Data Browser tab so the user can see the result
        if (!ReferenceEquals(SubTabView.SelectedItem, DataBrowserTab))
        {
            SubTabView.SelectedItem = DataBrowserTab;
        }

        if (_formIdNodeIndex == null)
        {
            // Wait for background index build if still in progress
            if (_formIdBuildTask != null)
            {
                await _formIdBuildTask;
            }

            // Fallback: build synchronously if background didn't run
            if (_formIdNodeIndex == null)
            {
                BuildFormIdNodeIndex();
            }
        }

        if (_formIdNodeIndex == null || !_formIdNodeIndex.TryGetValue(formId, out var targetNode))
        {
            // Try navigating to Dialogue Viewer if this is a dialogue record
            if (TryNavigateToDialogueRecord(formId))
            {
                return;
            }

            // Show brief status for records not in the data browser tree
            SelectedRecordTitle.Text = $"Record 0x{formId:X8} is not available in the Data Browser";
            return;
        }

        // Push current node onto back stack
        if (_selectedBrowserNode?.NodeType == "Record")
        {
            _navBackStack.Push(_selectedBrowserNode);
        }

        _navForwardStack.Clear();
        _isNavigating = true;
        await SelectAndScrollToNodeAsync(targetNode);
        UpdateNavButtons();
        _isNavigating = false;
    }

    private async void NavigateBack_Click(object sender, RoutedEventArgs e)
    {
        if (_navBackStack.Count == 0) return;

        if (_selectedBrowserNode?.NodeType == "Record")
        {
            _navForwardStack.Push(_selectedBrowserNode);
        }

        var target = _navBackStack.Pop();
        _isNavigating = true;
        await SelectAndScrollToNodeAsync(target);
        UpdateNavButtons();
        _isNavigating = false;
    }

    private async void NavigateForward_Click(object sender, RoutedEventArgs e)
    {
        if (_navForwardStack.Count == 0) return;

        if (_selectedBrowserNode?.NodeType == "Record")
        {
            _navBackStack.Push(_selectedBrowserNode);
        }

        var target = _navForwardStack.Pop();
        _isNavigating = true;
        await SelectAndScrollToNodeAsync(target);
        UpdateNavButtons();
        _isNavigating = false;
    }

    private void UpdateNavButtons()
    {
        NavBackButton.IsEnabled = _navBackStack.Count > 0;
        NavForwardButton.IsEnabled = _navForwardStack.Count > 0;
    }

    /// <summary>
    ///     Returns true if the FormID resolves to a known record in the browser tree
    ///     or is present in the FormIdMap (unreconstructed but exists in the file).
    /// </summary>
    private bool IsFormIdNavigable(uint formId)
    {
        if (_formIdNodeIndex != null && _formIdNodeIndex.ContainsKey(formId))
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
        _navBackStack.Clear();
        _navForwardStack.Clear();
        _formIdNodeIndex = null;
        _formIdBuildTask = null;
        NavBackButton.IsEnabled = false;
        NavForwardButton.IsEnabled = false;
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

#pragma warning disable S3267 // Nested loop with break - LINQ impractical here
        foreach (var category in _esmBrowserTree)
        {
            foreach (var typeNode in category.Children)
#pragma warning restore S3267
            {
                if (typeNode.Children.Contains(target))
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

        // Load children up to the target's position so its TreeViewNode exists.
        // Avoid loading ALL children for large type groups (e.g. STAT with 10,000+ records)
        // which would freeze the UI thread.
        if (_pendingTreeLoads.TryGetValue(typeTreeNode, out var pending))
        {
            var (allChildren, loadedCount) = pending;
            var targetDataIndex = allChildren.IndexOf(target);
            var loadUntil = targetDataIndex >= 0
                ? Math.Min(targetDataIndex + 50, allChildren.Count)
                : allChildren.Count;

            for (var i = loadedCount; i < loadUntil; i++)
            {
                var child = allChildren[i];
                var childNode = new TreeViewNode
                {
                    Content = child,
                    HasUnrealizedChildren = child.HasUnrealizedChildren ||
                                            child.NodeType is "Category" or "RecordType"
                };
                typeTreeNode.Children.Add(childNode);
            }

            if (loadUntil >= allChildren.Count)
            {
                _pendingTreeLoads.Remove(typeTreeNode);
            }
            else
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

        // Now try ContainerFromNode — should succeed since we scrolled near the target
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
