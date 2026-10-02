using System.Collections.ObjectModel;
using Slfx77.Multitool.Core.Browsing;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>Projects the current folder's original leaf nodes without changing export checks or preview ownership.</summary>
/// <remarks>The presentation thread owns this adapter. Its caller retains the exact source snapshot and
/// stops admitting native events before source retirement. Filtering never opens files or acquires leases.
/// Every non-folder child is an item; picture kinds get artwork, the rest a kind glyph. The name search and
/// the type filter combine as AND, and both hide rows without touching checks or the retained focus.</remarks>
internal sealed class AssetGalleryProjection : IDisposable
{
    private readonly AssetTreeSelection _selection;
    private readonly BrowserFilterController<AssetNode> _filter = new(static node => [node.Name, node.VirtualPath]);
    private readonly HashSet<AssetNode> _members = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    /// <summary>Retains the existing check owner and restores the browser's current query and type filter without changing checks.</summary>
    /// <param name="selection">The exact attached source's check owner.</param>
    /// <param name="query">The retained native search text, independent of the source generation.</param>
    /// <param name="kinds">The retained type filter, independent of the source generation.</param>
    internal AssetGalleryProjection(AssetTreeSelection selection, string query, AssetKindFilter kinds)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(kinds);
        _selection = selection;
        Kinds = kinds;
        _filter.SetFilter(query, Admits);
    }

    /// <summary>The original direct leaf children in builder order, before filtering.</summary>
    internal IReadOnlyList<AssetNode> Items { get; private set; } = Array.Empty<AssetNode>();
    /// <summary>The shared filter's retained collection of original nodes.</summary>
    internal ReadOnlyObservableCollection<AssetNode> VisibleItems => _filter.VisibleItems;
    /// <summary>The exact folder currently projected; null before navigation and after disposal.</summary>
    internal AssetNode? Folder { get; private set; }
    /// <summary>The retained gallery focus, including when a filter hides it; separate from the tree's preview.</summary>
    internal AssetNode? Focus { get; private set; }
    /// <summary>The focused row if visible; hidden focus remains available when the query or filter admits it again.</summary>
    internal AssetNode? VisibleFocus => Focus is { } node && VisibleItems.Contains(node) ? node : null;
    /// <summary>The type filter currently applied, retained across folders.</summary>
    internal AssetKindFilter Kinds { get; private set; }
    /// <summary>The count of direct files the type filter excludes; zero when no kind is excluded.</summary>
    internal int OtherCount => Items.Count(node => !Kinds.Admits(node.Kind));
    /// <summary>Whether the borrowed source still admits commands.</summary>
    private bool IsCurrent => !_disposed && !_selection.Checks.Snapshot.CancellationToken.IsCancellationRequested;

    /// <summary>Changes only to an exact current folder; revisiting the same folder preserves tiles and focus.</summary>
    /// <param name="folder">An original folder in the attached immutable tree.</param>
    /// <returns>True only when the gallery scope changed; rejected and same-folder requests leave it untouched.</returns>
    internal bool TryShowFolder(AssetNode folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!IsCurrent || folder.Kind != AssetNodeKind.Folder || ReferenceEquals(Folder, folder) ||
            !_selection.ContainsCurrentNode(_selection.Checks.Snapshot, folder)) return false;
        var eligible = folder.Children.Where(node =>
            node.Kind != AssetNodeKind.Folder && _selection.ContainsCurrentNode(_selection.Checks.Snapshot, node)).ToArray();
        _filter.ReplaceItems(eligible);
        Items = Array.AsReadOnly(eligible);
        _members.Clear();
        _members.UnionWith(eligible);
        Folder = folder;
        Focus = null;
        return true;
    }

    /// <summary>Filters names and original relative paths through the shared literal case-insensitive search.</summary>
    /// <param name="query">The exact entered text; no trimming, recursive enumeration or machine identity changes.</param>
    /// <returns>Whether the source still admitted the query.</returns>
    internal bool TrySetQuery(string query)
    {
        if (!IsCurrent) return false;
        _filter.SetFilter(query, Admits);
        return true;
    }

    /// <summary>Applies a type filter to this and every later folder, keeping the query, the checks and the focus.</summary>
    /// <param name="kinds">The kinds to admit; hidden rows stay members and keep their checks.</param>
    /// <returns>Whether the source still admitted the filter.</returns>
    internal bool TrySetKinds(AssetKindFilter kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        if (!IsCurrent) return false;
        Kinds = kinds;
        _filter.SetFilter(_filter.Query, Admits);
        return true;
    }

    /// <summary>Records only a visible original row without opening a preview or changing any export check.</summary>
    /// <param name="node">The row selected by the native gallery.</param>
    /// <returns>Whether the exact row belongs to the currently visible source projection.</returns>
    internal bool TryFocus(AssetNode node)
    {
        if (!IsCurrent || !_members.Contains(node) || !VisibleItems.Contains(node)) return false;
        Focus = node;
        return true;
    }

    /// <summary>Forwards an exact gallery occurrence to the existing source-wide check owner, even if hidden.</summary>
    /// <param name="node">An original leaf node in this folder.</param>
    /// <param name="isChecked">The native boolean check command.</param>
    /// <returns>Whether the source and occurrence admitted the command.</returns>
    internal bool TrySetChecked(AssetNode node, bool isChecked)
    {
        if (!IsCurrent || !_members.Contains(node)) return false;
        // Two-way native bindings echo committed checks while the tree is notifying.
        // Read the owner: another node's display mirror may not have been updated yet.
        if (_selection.Checks.GetState(node) == isChecked) return true;
        _selection.SetChecked(_selection.Checks.Snapshot, node, isChecked);
        return true;
    }

    /// <summary>Releases only presentation references; the caller continues to own source lifetime and export checks.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Folder = null;
        Focus = null;
        Items = Array.Empty<AssetNode>();
        _members.Clear();
        _filter.Dispose();
    }

    /// <summary>The shared filter's eligibility predicate: the retained type filter, read at evaluation time.</summary>
    private bool Admits(AssetNode node) => Kinds.Admits(node.Kind);
}
