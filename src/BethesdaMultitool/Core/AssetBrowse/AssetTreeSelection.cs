using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Browsing;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>Adapts one complete Bethesda tree to shared export checks without owning focus or source lifetime.</summary>
/// <remarks>
/// The caller retains the existing browser snapshot. Paths preserve the builder's first-hit, case-insensitive
/// VFS identity; archive leaves remain opaque. Late discovery requires a new source tree and adapter.
/// Shared path normalization also removes dot/empty segments. Conflicting canonical identities are rejected
/// explicitly, rather than silently choosing between VFS-distinct spellings that would target the same export path.
/// </remarks>
internal sealed class AssetTreeSelection
{
    /// <summary>Maps shared normalized identities back to original nodes and VFS path spelling.</summary>
    private readonly Dictionary<string, AssetNode> _exportNodes;

    /// <summary>Attaches the tree belonging to this exact retained Bethesda source opening, initially unchecked.</summary>
    /// <param name="snapshot">The existing window source snapshot, already retained by the caller.</param>
    internal AssetTreeSelection(BrowserSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Source is not BethesdaBrowseSource source)
            throw new ArgumentException("Selection requires a Bethesda source tree.", nameof(snapshot));
        Session = source.Session;
        _exportNodes = new Dictionary<string, AssetNode>(source.PathComparer);
        var nodes = new List<AssetNode>();
        Checks = new AssetCheckSelection<AssetNode>(snapshot, [Session.Root], static node => node.Children,
            node =>
            {
                nodes.Add(node);
                if (!node.IsExtractable) return null;
                var identity = new AssetReference(source.Id, node.VirtualPath);
                _exportNodes.Add(identity.Path, node);
                return identity;
            });
        foreach (var node in nodes) node.SetCheckState(false);
        Checks.Changed += OnChecksChanged;
    }

    /// <summary>The original session and fixed tree; the adapter never disposes its filesystem.</summary>
    internal AssetBrowseSession Session { get; }

    /// <summary>The actual shared controller, independent of current preview and visible gallery rows.</summary>
    internal AssetCheckSelection<AssetNode> Checks { get; }

    /// <summary>The fixed number of extractable leaves, counted once while identities are attached.</summary>
    internal int FileCount => _exportNodes.Count;

    /// <summary>Admits only exact current tree objects, rejecting reused paths from a different source opening.</summary>
    /// <param name="currentSnapshot">The source currently published by the caller.</param>
    /// <param name="node">The object carried by a native tree or gallery event.</param>
    /// <returns>Whether the nonretired snapshot and node both belong to this adapter.</returns>
    internal bool ContainsCurrentNode(BrowserSnapshot currentSnapshot, AssetNode node) =>
        ReferenceEquals(currentSnapshot, Checks.Snapshot) && !currentSnapshot.CancellationToken.IsCancellationRequested &&
        Checks.ContainsNode(node);

    /// <summary>Maps the native tristate cycle's null command to the existing Bethesda clear behavior.</summary>
    /// <param name="currentSnapshot">The currently published source opening.</param>
    /// <param name="node">The exact attached node to change, together with its descendants.</param>
    /// <param name="isChecked">The native requested value; null clears rather than selecting a nullable leaf.</param>
    internal void SetChecked(BrowserSnapshot currentSnapshot, AssetNode node, bool? isChecked) =>
        Checks.SetChecked(currentSnapshot, node, isChecked ?? false);

    /// <summary>Captures original nodes once in tree order for export through the caller's retained source.</summary>
    /// <param name="currentSnapshot">The exact source already retained by the export caller's lease.</param>
    /// <returns>An immutable selection unaffected by later check commands or source replacement.</returns>
    internal IReadOnlyList<AssetNode> CaptureSelected(BrowserSnapshot currentSnapshot) =>
        Array.AsReadOnly(Checks.CaptureSelected(currentSnapshot).Select(identity => _exportNodes[identity.Path]).ToArray());

    /// <summary>Mirrors only final changed states; recursive propagation and counts remain shared responsibilities.</summary>
    /// <param name="sender">The attached shared controller.</param>
    /// <param name="args">The coherent committed change batch, including ancestor states.</param>
    private static void OnChecksChanged(object? sender, AssetChecksChangedEventArgs<AssetNode> args)
    {
        foreach (var change in args.Changes) change.Node.SetCheckState(change.IsChecked);
    }
}
