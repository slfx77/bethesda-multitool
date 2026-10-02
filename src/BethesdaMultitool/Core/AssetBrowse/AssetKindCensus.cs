namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     Counts the leaves of each kind under a tree root, so the type-filter flyout can list only the
///     kinds a source actually contains, each with its count.
/// </summary>
internal static class AssetKindCensus
{
    /// <summary>One counter slot per defined kind value, indexed by the enum's integer value.</summary>
    private static readonly int KindSlots = Enum.GetValues<AssetNodeKind>().Max(static kind => (int)kind) + 1;

    /// <summary>
    ///     The kinds present under <paramref name="root" /> with their leaf counts, in
    ///     <see cref="AssetKindFilter.FilterableKinds" /> order; kinds with no leaves are omitted and
    ///     folders are never counted. The walk is iterative, so a pathologically deep tree cannot
    ///     overflow the stack.
    /// </summary>
    /// <param name="root">The tree to census; usually the session root.</param>
    internal static IReadOnlyList<AssetKindCount> Present(AssetNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var counts = new int[KindSlots];
        var pending = new Stack<AssetNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node.Kind != AssetNodeKind.Folder)
            {
                counts[(int)node.Kind]++;
            }

            foreach (var child in node.Children)
            {
                pending.Push(child);
            }
        }

        var present = new List<AssetKindCount>();
        foreach (var kind in AssetKindFilter.FilterableKinds)
        {
            var count = counts[(int)kind];
            if (count > 0)
            {
                present.Add(new AssetKindCount(kind, count));
            }
        }

        return present.AsReadOnly();
    }
}
