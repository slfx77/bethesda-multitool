namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     The asset browser's type filter: an immutable set of EXCLUDED <see cref="AssetNodeKind" /> values.
///     <para>
///         Exclusion rather than inclusion is what makes "persisted across source reloads within the
///         session" fall out naturally: a kind that first appears in a later source is admitted unless
///         the user excluded it earlier, and a kind absent from the current source keeps whatever
///         exclusion it carried. <see cref="AssetNodeKind.Folder" /> is never filterable; a folder's
///         visibility is derived from its descendants by <see cref="AssetTreeVisibility" />.
///     </para>
///     <para>
///         Value equality over the excluded set lets a caller detect a no-op change with
///         <see cref="Equals(AssetKindFilter)" /> before rebuilding a tree.
///     </para>
/// </summary>
internal sealed class AssetKindFilter : IEquatable<AssetKindFilter>
{
    /// <summary>Every filterable kind in the order the census and the filter flyout list them.</summary>
    private static readonly AssetNodeKind[] DisplayOrder =
    [
        AssetNodeKind.Texture,
        AssetNodeKind.Sprite,
        AssetNodeKind.Model,
        AssetNodeKind.Audio,
        AssetNodeKind.Video,
        AssetNodeKind.Map,
        AssetNodeKind.Text,
        AssetNodeKind.Plugin,
        AssetNodeKind.Archive,
        AssetNodeKind.Save,
        AssetNodeKind.Raw
    ];

    /// <summary>One bit per filterable kind, so the mask covers every member of <see cref="DisplayOrder" />.</summary>
    private static readonly int EveryKindMask = DisplayOrder.Aggregate(0, static (mask, kind) => mask | Bit(kind));

    private readonly int _excludedMask;

    private AssetKindFilter(int excludedMask)
    {
        _excludedMask = excludedMask;
        ExcludedKinds = Array.AsReadOnly(DisplayOrder.Where(kind => (excludedMask & Bit(kind)) != 0).ToArray());
    }

    /// <summary>The filter that excludes nothing; the browser's initial state.</summary>
    internal static AssetKindFilter All { get; } = new(0);

    /// <summary>Every kind except <see cref="AssetNodeKind.Folder" />, in display order.</summary>
    internal static IReadOnlyList<AssetNodeKind> FilterableKinds { get; } = Array.AsReadOnly(DisplayOrder);

    /// <summary>The kinds this filter hides, in display order; empty for <see cref="All" />.</summary>
    internal IReadOnlyCollection<AssetNodeKind> ExcludedKinds { get; }

    /// <summary>Whether any kind is excluded.</summary>
    internal bool IsActive => _excludedMask != 0;

    /// <summary>Whether leaves of <paramref name="kind" /> pass the filter; folders always do.</summary>
    /// <param name="kind">The classification to test.</param>
    internal bool Admits(AssetNodeKind kind) => kind == AssetNodeKind.Folder || (_excludedMask & Bit(kind)) == 0;

    /// <summary>Whether <paramref name="node" /> passes the filter by its kind alone; folders always do.</summary>
    /// <param name="node">The tree node to test.</param>
    internal bool Admits(AssetNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return Admits(node.Kind);
    }

    /// <summary>Returns a filter that includes or excludes one kind, leaving every other kind as it is.</summary>
    /// <param name="kind">A filterable kind.</param>
    /// <param name="included">True to admit the kind, false to exclude it.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind" /> is a folder or not a defined kind.</exception>
    internal AssetKindFilter With(AssetNodeKind kind, bool included)
    {
        ThrowIfNotFilterable(kind, nameof(kind));
        var mask = included ? _excludedMask & ~Bit(kind) : _excludedMask | Bit(kind);
        return mask == _excludedMask ? this : new AssetKindFilter(mask);
    }

    /// <summary>Returns a filter that includes every kind (<see cref="All" />) or excludes every kind.</summary>
    /// <param name="included">True to admit every kind, false to exclude every kind.</param>
    internal AssetKindFilter WithAll(bool included)
    {
        var mask = included ? 0 : EveryKindMask;
        return mask == _excludedMask ? this : new AssetKindFilter(mask);
    }

    /// <summary>
    ///     Returns a filter whose state for the kinds in <paramref name="present" /> is exactly
    ///     <paramref name="included" />: a present kind that is not included becomes excluded, a present kind
    ///     that is included becomes admitted, and a kind that is NOT present keeps whatever exclusion it had,
    ///     so a kind absent from the current source keeps an earlier exclusion.
    /// </summary>
    /// <param name="present">The kinds the current source lists, typically from <see cref="AssetKindCensus.Present" />.</param>
    /// <param name="included">The subset of <paramref name="present" /> the user wants shown.</param>
    /// <exception cref="ArgumentOutOfRangeException">A present kind is a folder or not a defined kind.</exception>
    internal AssetKindFilter WithIncluded(IEnumerable<AssetNodeKind> present, IEnumerable<AssetNodeKind> included)
    {
        ArgumentNullException.ThrowIfNull(present);
        ArgumentNullException.ThrowIfNull(included);
        var includedMask = 0;
        foreach (var kind in included)
        {
            ThrowIfNotFilterable(kind, nameof(included));
            includedMask |= Bit(kind);
        }

        var mask = _excludedMask;
        foreach (var kind in present)
        {
            ThrowIfNotFilterable(kind, nameof(present));
            var bit = Bit(kind);
            mask = (includedMask & bit) != 0 ? mask & ~bit : mask | bit;
        }

        return mask == _excludedMask ? this : new AssetKindFilter(mask);
    }

    /// <inheritdoc />
    public bool Equals(AssetKindFilter? other) => other is not null && other._excludedMask == _excludedMask;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as AssetKindFilter);

    /// <inheritdoc />
    public override int GetHashCode() => _excludedMask;

    /// <summary>The mask bit for a kind; every defined kind fits in the low bits of an int.</summary>
    private static int Bit(AssetNodeKind kind) => 1 << (int)kind;

    private static void ThrowIfNotFilterable(AssetNodeKind kind, string paramName)
    {
        if (kind == AssetNodeKind.Folder || !Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(paramName, kind, "Only leaf kinds can be filtered.");
        }
    }
}
