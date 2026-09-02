using System.ComponentModel;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     One node of the asset-browser tree: a virtual folder, or a classified leaf from an
///     <see cref="Vfs.IGameFileSystem" /> enumeration. Identity members (<see cref="Name" />,
///     <see cref="VirtualPath" />, <see cref="Kind" />, <see cref="Size" />, the tree shape) are
///     immutable once <see cref="AssetTreeBuilder" /> finishes; the only mutable state is the
///     tristate <see cref="IsChecked" />, whose changes flood the subtree and recompute ancestors
///     (mixed → null), raising <see cref="PropertyChanged" /> only on nodes whose value actually
///     changed. All traversal is iterative, so deep chains never consume stack. Not thread-safe:
///     mutate from one thread (the GUI's dispatcher) only.
/// </summary>
public sealed class AssetNode : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs IsCheckedChangedArgs = new(nameof(IsChecked));

    private readonly List<AssetNode> _children = [];
    private bool? _isChecked = false;

    internal AssetNode(string name, string virtualPath, AssetNodeKind kind, long size)
    {
        Name = name;
        VirtualPath = virtualPath;
        Kind = kind;
        Size = size;
    }

    /// <summary>Raised for <see cref="IsChecked" /> only — everything else is immutable.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Display name (file or folder segment; the builder's label for the root).</summary>
    public string Name { get; }

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

    /// <summary>
    ///     Tristate check state: true/false when the subtree is uniform, null when mixed. Assigning
    ///     a definite value floods the whole subtree and recomputes every ancestor on the path to
    ///     the root; assigning null (the indeterminate stop of a tristate checkbox cycle) is
    ///     coerced to false, clearing the subtree. Only nodes whose value actually changed raise
    ///     <see cref="PropertyChanged" />.
    /// </summary>
    public bool? IsChecked
    {
        get => _isChecked;
        set => SetChecked(value ?? false);
    }

    /// <summary>
    ///     Depth-first (in <see cref="Children" /> order) enumeration of the checked non-folder
    ///     nodes in this subtree, self included — exactly what an extract of this node should write.
    /// </summary>
    public IEnumerable<AssetNode> CheckedFiles()
    {
        var stack = new Stack<AssetNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Kind != AssetNodeKind.Folder && node._isChecked == true)
            {
                yield return node;
            }

            // Push in reverse so the traversal preserves Children order.
            for (var i = node._children.Count - 1; i >= 0; i--)
            {
                stack.Push(node._children[i]);
            }
        }
    }

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

    private void SetChecked(bool value)
    {
        // Flood the subtree iteratively — deep chains must not consume stack.
        var stack = new Stack<AssetNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            node.SetCheckedCore(value);
            foreach (var child in node._children)
            {
                stack.Push(child);
            }
        }

        // Recompute ancestors. An unchanged ancestor ends the walk: its parent's inputs are
        // exactly its children's values, and only this path was touched, so everything above
        // is unchanged too (the class invariant keeps folder values consistent at all times).
        for (var ancestor = Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (!ancestor.SetCheckedCore(ancestor.ComputeFromChildren()))
            {
                break;
            }
        }
    }

    /// <summary>Sets the raw value; raises and reports true only when it changed.</summary>
    private bool SetCheckedCore(bool? value)
    {
        if (_isChecked == value)
        {
            return false;
        }

        _isChecked = value;
        PropertyChanged?.Invoke(this, IsCheckedChangedArgs);
        return true;
    }

    /// <summary>Aggregate of the children: uniform → that value, any mix or mixed child → null.</summary>
    private bool? ComputeFromChildren()
    {
        if (_children.Count == 0)
        {
            return _isChecked;
        }

        var first = _children[0]._isChecked;
        if (first is null)
        {
            return null;
        }

        for (var i = 1; i < _children.Count; i++)
        {
            if (_children[i]._isChecked != first)
            {
                return null;
            }
        }

        return first;
    }
}
