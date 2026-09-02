using BethesdaMultitool.Core.Vfs;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     Builds the asset-browser tree over an <see cref="IGameFileSystem" />: one full enumeration,
///     virtual paths split into a synthesized folder hierarchy, leaves classified by extension.
///     Iterative throughout (dictionary-keyed folder creation, one explicit sort pass per folder),
///     so 100k+ entry mounts stay O(n log n) with no recursion-depth exposure.
/// </summary>
public static class AssetTreeBuilder
{
    /// <summary>
    ///     Enumerates <paramref name="fs" /> and builds the tree under a
    ///     <see cref="AssetNodeKind.Folder" /> root named <paramref name="rootLabel" />. Duplicate
    ///     virtual paths keep the first entry (mirroring layered first-hit-wins enumeration).
    /// </summary>
    /// <param name="fs">The filesystem to enumerate (not disposed here).</param>
    /// <param name="rootLabel">Display name for the root node.</param>
    /// <param name="headReader">
    ///     Reserved magic-sniff refinement hook (virtual path → leading bytes) for formats whose
    ///     extension alone is ambiguous — the classic-game formats need it. Accepted and ignored
    ///     for now; extension classification is the only path.
    /// </param>
    public static AssetNode Build(IGameFileSystem fs, string rootLabel,
        Func<string, ReadOnlyMemory<byte>>? headReader = null)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(rootLabel);
        _ = headReader; // Reserved — see the doc comment.

        var root = new AssetNode(rootLabel, string.Empty, AssetNodeKind.Folder, 0);
        var folders = new Dictionary<string, AssetNode>(VfsPath.Comparer) { [string.Empty] = root };
        var seenFiles = new HashSet<string>(VfsPath.Comparer);

        foreach (var entry in fs.EnumerateFiles(string.Empty))
        {
            var path = VfsPath.Normalize(entry.Path);
            if (path.Length == 0 || !seenFiles.Add(path))
            {
                continue;
            }

            var lastSep = path.LastIndexOf('\\');
            var parent = lastSep < 0 ? root : GetOrCreateFolder(folders, path, lastSep);
            var name = path[(lastSep + 1)..];
            parent.AddChild(new AssetNode(name, path, ClassifyExtension(name), entry.Size));
        }

        foreach (var folder in folders.Values)
        {
            folder.SortChildren();
        }

        return root;
    }

    /// <summary>
    ///     Resolves (creating as needed) the folder chain for <paramref name="filePath" /> up to
    ///     <paramref name="dirEnd" /> (the last separator index). Iterative: one dictionary probe
    ///     for the full directory (the hot case — files cluster), then a segment walk on miss.
    /// </summary>
    private static AssetNode GetOrCreateFolder(
        Dictionary<string, AssetNode> folders, string filePath, int dirEnd)
    {
        if (folders.TryGetValue(filePath[..dirEnd], out var hit))
        {
            return hit;
        }

        var parent = folders[string.Empty];
        var start = 0;
        while (start < dirEnd)
        {
            var sep = filePath.IndexOf('\\', start, dirEnd - start);
            var end = sep < 0 ? dirEnd : sep;
            if (end == start)
            {
                start++; // empty segment (doubled separator): skip rather than synthesize a nameless folder
                continue;
            }

            var prefix = filePath[..end];
            if (!folders.TryGetValue(prefix, out var node))
            {
                node = new AssetNode(filePath[start..end], prefix, AssetNodeKind.Folder, 0);
                parent.AddChild(node);
                folders.Add(prefix, node);
            }

            parent = node;
            start = end + 1;
        }

        return parent;
    }

    /// <summary>Extension → kind. Unknown or missing extensions fall to <see cref="AssetNodeKind.Raw" />.</summary>
    private static AssetNodeKind ClassifyExtension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1)
        {
            return AssetNodeKind.Raw;
        }

        return fileName[(dot + 1)..].ToLowerInvariant() switch
        {
            "dds" or "ddx" or "png" or "tga" => AssetNodeKind.Texture,
            "nif" or "glb" or "gltf" => AssetNodeKind.Model,
            "wav" or "mp3" or "ogg" or "xma" or "voc" or "acm" => AssetNodeKind.Audio,
            "bik" or "mve" or "flc" or "vid" or "smk" => AssetNodeKind.Video,
            "frm" or "cif" or "cfa" or "dfa" or "zar" or "til" or "spr" or "rci" => AssetNodeKind.Sprite,
            "esm" or "esp" => AssetNodeKind.Plugin,
            "fos" or "fxs" => AssetNodeKind.Save,
            "txt" or "msg" or "ini" or "cfg" or "xml" or "json" or "lst" or "gam" => AssetNodeKind.Text,
            _ => AssetNodeKind.Raw
        };
    }
}
