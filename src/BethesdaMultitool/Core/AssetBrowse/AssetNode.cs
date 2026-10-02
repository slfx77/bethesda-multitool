using System.ComponentModel;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     One node of the asset-browser tree: a virtual folder, or a classified leaf from an
///     <see cref="Vfs.IGameFileSystem" /> enumeration. Identity members (<see cref="Name" />,
///     <see cref="VirtualPath" />, <see cref="Kind" />, <see cref="Size" />, the tree shape) are
///     immutable once <see cref="AssetTreeBuilder" /> finishes; the only mutable state is the
///     tristate <see cref="IsChecked" /> display mirror updated by <see cref="AssetTreeSelection" />.
///     Shared checks own propagation and export selection. Not thread-safe: use the GUI dispatcher.
/// </summary>
public sealed class AssetNode : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs IsCheckedChangedArgs = new(nameof(IsChecked));

    private readonly List<AssetNode> _children = [];
    private bool? _isChecked = false;

    /// <summary>Creates one builder-owned immutable identity with an initially unchecked display value.</summary>
    internal AssetNode(string name, string virtualPath, AssetNodeKind kind, long size)
    {
        Name = name;
        VirtualPath = virtualPath;
        Kind = kind;
        Size = size;
    }

    /// <summary>Display name (file or folder segment; the builder's label for the root).</summary>
    public string Name { get; }

    /// <summary>
    ///     The decoded picture's full size and frame count, published by the thumbnail worker once it has
    ///     decoded this node, or null before that (and for nodes that are not pictures). Written before the
    ///     worker hands its artwork to the dispatcher and read by the presenting thread after that artwork
    ///     arrives, so no change notification is raised for it.
    /// </summary>
    public AssetImageInfo? ImageInfo { get; internal set; }

    /// <summary>Normalized VFS path (backslash separators); empty for the root.</summary>
    public string VirtualPath { get; }

    /// <summary>Classification driving the capability predicates and the GUI's icon/preview choice.</summary>
    public AssetNodeKind Kind { get; }

    /// <summary>Leaf size as reported by the VFS (see <see cref="Vfs.GameFileEntry.Size" />); 0 for folders.</summary>
    public long Size { get; }

    /// <summary>The containing folder node; null for the root.</summary>
    public AssetNode? Parent { get; private set; }

    /// <summary>Children ordered folders-first, then by name (ordinal, ignore case).</summary>
    public IReadOnlyList<AssetNode> Children => _children;

    /// <summary>Whether the GUI shows an expander (folders now; archive leaves once nested browsing lands).</summary>
    public bool IsExpandable => Kind is AssetNodeKind.Folder or AssetNodeKind.Archive;

    /// <summary>Whether a preview pane can render this kind.</summary>
    public bool IsPreviewable => Kind is AssetNodeKind.Texture or AssetNodeKind.Model or AssetNodeKind.Audio
        or AssetNodeKind.Video or AssetNodeKind.Sprite or AssetNodeKind.Text or AssetNodeKind.Map;

    /// <summary>Whether the node has payload bytes to extract (everything except folders).</summary>
    public bool IsExtractable => Kind != AssetNodeKind.Folder;

    /// <summary>The shared controller's last committed display state; null means mixed.</summary>
    public bool? IsChecked => _isChecked;

    /// <summary>Raised for changed check-state display values only.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Attaches a child (builder only; the tree shape is frozen after the build).</summary>
    internal void AddChild(AssetNode child)
    {
        child.Parent = this;
        _children.Add(child);
    }

    /// <summary>Sorts the direct children folders-first, then ordinal-ignore-case by name (builder only).</summary>
    internal void SortChildren()
    {
        _children.Sort(CompareChildren);
    }

    /// <summary>Orders folders before leaves, then names deterministically with a case-sensitive tie break.</summary>
    private static int CompareChildren(AssetNode a, AssetNode b)
    {
        var aIsFolder = a.Kind == AssetNodeKind.Folder;
        if (aIsFolder != (b.Kind == AssetNodeKind.Folder))
        {
            return aIsFolder ? -1 : 1;
        }

        var byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return byName != 0 ? byName : string.CompareOrdinal(a.Name, b.Name);
    }

    /// <summary>Copies a final shared state without recursive propagation or a second selection owner.</summary>
    /// <param name="value">The shared controller's committed state.</param>
    internal void SetCheckState(bool? value)
    {
        if (_isChecked == value) return;
        _isChecked = value;
        PropertyChanged?.Invoke(this, IsCheckedChangedArgs);
    }
}
