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

    /// <summary>
    ///     Name → kind. Unknown or missing extensions fall to <see cref="AssetNodeKind.Raw" />.
    ///     <para>
    ///         Extension alone does not settle the classic catalogue, so two name rules come first.
    ///         Daggerfall's textures are <c>TEXTURE.000</c>… — a NUMERIC extension, which no
    ///         extension table can match and which would otherwise leave every one of the ~1,500
    ///         texture files unclassified. Its sky sets are <c>SKY00.DAT</c>… while other
    ///         <c>.DAT</c> files are anything at all, so only the SKY prefix is claimed.
    ///     </para>
    /// </summary>
    private static AssetNodeKind ClassifyExtension(string fileName)
    {
        if (IsDaggerfallTextureRecord(fileName))
        {
            return AssetNodeKind.Texture;
        }

        if (fileName.StartsWith("SKY", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".DAT", StringComparison.OrdinalIgnoreCase))
        {
            return AssetNodeKind.Sprite;
        }

        // Battlespire's 3D.BS6 is an XnGine BSA of meshes despite the extension every other .BS6
        // uses for a level, so the name decides rather than the extension.
        if (fileName.Equals("3D.BS6", StringComparison.OrdinalIgnoreCase))
        {
            return AssetNodeKind.Archive;
        }

        var dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1)
        {
            return AssetNodeKind.Raw;
        }

        return fileName[(dot + 1)..].ToLowerInvariant() switch
        {
            "dds" or "ddx" or "png" or "tga" => AssetNodeKind.Texture,
            // .3D is the XnGine mesh shared by Daggerfall, Battlespire and Redguard; .3DC is
            // Redguard's variant.
            "nif" or "glb" or "gltf" or "3d" or "3dc" => AssetNodeKind.Model,
            // .SND is a numbered XnGine BSA of samples (DAGGER.SND, SPIRE.SND); HMI/XMI/MID are
            // sequenced music.
            "wav" or "mp3" or "ogg" or "xma" or "voc" or "acm" or "snd" or "hmi" or "xmi" or "mid"
                => AssetNodeKind.Audio,
            "bik" or "mve" or "flc" or "vid" or "smk" or "cel" => AssetNodeKind.Video,
            // IMG/MNU/SET are Arena's image families; BSI is Battlespire's; GXA is Redguard's.
            "frm" or "cif" or "cfa" or "dfa" or "zar" or "til" or "spr" or "rci"
                or "img" or "mnu" or "set" or "bsi" or "gxa" => AssetNodeKind.Sprite,
            // MIF/RMD are Arena's voxel maps; WLD is Daggerfall's WOODS heightmap; PAK its
            // CLIMATE/POLITIC overlays; BS6 a Battlespire level.
            "mif" or "rmd" or "wld" or "pak" or "bs6" => AssetNodeKind.Map,
            "esm" or "esp" => AssetNodeKind.Plugin,
            "fos" or "fxs" => AssetNodeKind.Save,
            // RSC is TEXT.RSC; INF an Arena level definition; QRC/QBN the two quest halves.
            "txt" or "msg" or "ini" or "cfg" or "xml" or "json" or "lst" or "gam"
                or "rsc" or "inf" or "qrc" or "qbn" => AssetNodeKind.Text,
            "bsa" or "ba2" or "bos" or "pck" or "dat2" => AssetNodeKind.Archive,
            _ => AssetNodeKind.Raw
        };
    }

    /// <summary>
    ///     True for a Daggerfall texture record — <c>TEXTURE.000</c> through <c>TEXTURE.999</c>,
    ///     whose "extension" is a number rather than a name.
    /// </summary>
    private static bool IsDaggerfallTextureRecord(string fileName)
    {
        const string prefix = "TEXTURE.";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = fileName[prefix.Length..];
        return suffix.Length > 0 && suffix.All(char.IsAsciiDigit);
    }
}
