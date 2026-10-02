using BethesdaMultitool.Core.Vfs;
using Slfx77.Multitool.Core.Browsing;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>Requests a new source opening for one current archive leaf backed by an original loose file.</summary>
/// <remarks>The workspace must recheck the exact snapshot before replacing its source. This request owns
/// no lease or copied payload, and never interprets an archive's virtual entry as a physical file.</remarks>
internal sealed record AssetArchiveOpenRequest
{
    private AssetArchiveOpenRequest(BrowserSnapshot snapshot, AssetNode node, string path)
    {
        Snapshot = snapshot;
        Node = node;
        Path = path;
    }

    /// <summary>The exact opening which supplied the selected occurrence.</summary>
    internal BrowserSnapshot Snapshot { get; }
    /// <summary>The original immutable tree node, not a recreated same-path selection.</summary>
    internal AssetNode Node { get; }
    /// <summary>The original loose archive's absolute path, suitable for the normal source-opening pipeline.</summary>
    internal string Path { get; }

    /// <summary>Resolves only a current archive occurrence through known loose and ordered layered filesystem owners.</summary>
    /// <param name="selection">The existing fixed-tree selection owner.</param>
    /// <param name="snapshot">The snapshot currently published by the browser.</param>
    /// <param name="node">The original selected archive leaf.</param>
    /// <returns>A physical source request, or null for retired, foreign, missing or archive-backed entries.</returns>
    internal static AssetArchiveOpenRequest? TryCreate(AssetTreeSelection selection, BrowserSnapshot snapshot, AssetNode node)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(node);
        if (node.Kind != AssetNodeKind.Archive || !selection.ContainsCurrentNode(snapshot, node)) return null;
        var path = ResolveLooseFile(selection.Session.FileSystem, node.VirtualPath);
        return path is not null && selection.ContainsCurrentNode(snapshot, node)
            ? new AssetArchiveOpenRequest(snapshot, node, path)
            : null;
    }

    /// <summary>Preserves the first metadata winner; an opaque or archive-backed higher layer never falls through.</summary>
    /// <param name="filesystem">The actual source owner, not its diagnostic label.</param>
    /// <param name="virtualPath">The original current node's virtual path spelling.</param>
    /// <returns>The current physical loose path when its known owner supplies matching provenance.</returns>
    private static string? ResolveLooseFile(IGameFileSystem filesystem, string virtualPath)
    {
        if (filesystem is LayeredGameFileSystem layered)
        {
            foreach (var layer in layered.Layers)
            {
                if (layer.TryStat(virtualPath) is not null) return ResolveLooseFile(layer, virtualPath);
            }
            return null;
        }

        if (filesystem is not LooseFileSystem loose || loose.TryStat(virtualPath) is not { } entry ||
            !string.Equals(entry.Source, loose.Label, StringComparison.Ordinal) ||
            !VfsPath.Comparer.Equals(entry.Path, virtualPath)) return null;

        var relative = entry.Path.Replace('\\', System.IO.Path.DirectorySeparatorChar);
        if (System.IO.Path.IsPathRooted(relative) || relative.Split(System.IO.Path.DirectorySeparatorChar).Contains("..")) return null;
        var fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(loose.Label, relative));
        var scoped = System.IO.Path.GetRelativePath(loose.Label, fullPath);
        return System.IO.Path.IsPathRooted(scoped) || scoped.Split(System.IO.Path.DirectorySeparatorChar).Contains("..")
            ? null
            : fullPath;
    }
}
