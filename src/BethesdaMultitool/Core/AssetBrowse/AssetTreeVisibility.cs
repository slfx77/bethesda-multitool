namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     Which nodes of one tree the asset tree view shows under a type filter. A leaf is visible when
///     its kind passes <see cref="AssetKindFilter.Admits(AssetNodeKind)" />; a folder is visible when any
///     descendant leaf is; the root is always visible. Computed once per filter change over the whole
///     immutable tree and then queried per folder, so the view can compare each folder's visible child
///     sequence against what it currently shows and touch only the folders that changed.
///     <para>
///         Export checks live in <see cref="AssetTreeSelection" />, not here: hiding a node never
///         changes whether it is checked.
///     </para>
/// </summary>
internal sealed class AssetTreeVisibility
{
    private readonly HashSet<AssetNode> _visible;

    private AssetTreeVisibility(HashSet<AssetNode> visible, int visibleFileCount, int totalFileCount)
    {
        _visible = visible;
        VisibleFileCount = visibleFileCount;
        TotalFileCount = totalFileCount;
    }

    /// <summary>The number of leaves the filter admits.</summary>
    internal int VisibleFileCount { get; }

    /// <summary>The number of leaves in the tree, filtered or not.</summary>
    internal int TotalFileCount { get; }

    /// <summary>Evaluates <paramref name="filter" /> over every node under <paramref name="root" /> in one iterative pass.</summary>
    /// <param name="root">The tree root, which is always visible.</param>
    /// <param name="filter">The kinds to admit.</param>
    internal static AssetTreeVisibility Compute(AssetNode root, AssetKindFilter filter)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(filter);

        // Pre-order collection, then a reverse sweep: every child precedes its parent in the reversed
        // list, which is the post-order a folder needs to know whether any descendant is visible.
        var preOrder = new List<AssetNode>();
        var pending = new Stack<AssetNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            preOrder.Add(node);
            foreach (var child in node.Children)
            {
                pending.Push(child);
            }
        }

        var visible = new HashSet<AssetNode>(ReferenceEqualityComparer.Instance);
        var visibleFiles = 0;
        var totalFiles = 0;
        for (var index = preOrder.Count - 1; index >= 0; index--)
        {
            var node = preOrder[index];
            var isVisible = false;
            if (node.Kind != AssetNodeKind.Folder)
            {
                totalFiles++;
                isVisible = filter.Admits(node.Kind);
                if (isVisible)
                {
                    visibleFiles++;
                }
            }

            if (!isVisible)
            {
                foreach (var child in node.Children)
                {
                    if (visible.Contains(child))
                    {
                        isVisible = true;
                        break;
                    }
                }
            }

            if (isVisible || ReferenceEquals(node, root))
            {
                visible.Add(node);
            }
        }

        return new AssetTreeVisibility(visible, visibleFiles, totalFiles);
    }

    /// <summary>Whether the view shows <paramref name="node" />; the root always, a folder only with a visible descendant.</summary>
    /// <param name="node">A node of the computed tree; any other node reads as hidden.</param>
    internal bool IsVisible(AssetNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return _visible.Contains(node);
    }

    /// <summary>The visible direct children of <paramref name="folder" /> in builder order.</summary>
    /// <param name="folder">A node of the computed tree; a leaf has no children.</param>
    internal IReadOnlyList<AssetNode> VisibleChildren(AssetNode folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var children = new List<AssetNode>();
        foreach (var child in folder.Children)
        {
            if (_visible.Contains(child))
            {
                children.Add(child);
            }
        }

        return children.AsReadOnly();
    }
}
