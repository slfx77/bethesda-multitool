using BethesdaMultitool.Core.Formats.Arena;
using BethesdaMultitool.Core.Formats.Battlespire;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Formats.Redguard;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     Assembles a whole classic LEVEL — many placed meshes — into a viewer scene, so a level can be
///     explored in the GUI rather than only exported to GLB.
///     <para>
///         Battlespire is the first backer because its level geometry is held BY REFERENCE: a
///         <c>.BS6</c> level carries a mesh list (LFIL) plus placements that index it (OBJD.IDFI
///         with POSI/ANGS), and <c>Bs6SceneAssembler</c> already resolves those against the mesh
///         archives. Reusing it means the GUI and <c>classic level export</c> place geometry
///         identically — the ANGS convention in particular was settled by measurement and must not
///         be re-derived here.
///     </para>
///     <para>
///         ⚑ Daggerfall joined it on 2026-09-06 and brings both halves of that game: the exterior
///         city blocks (<c>*.RMB</c>) and the dungeon blocks (<c>*.RDB</c>), assembled by
///         <see cref="DaggerfallBlockSceneAssembler" /> and <see cref="DaggerfallRdbSceneAssembler" />
///         respectively. ⚑ Unlike Battlespire these come out TEXTURED, because Daggerfall's
///         <c>TEXTURE.nnn</c> art is decoded and Battlespire's <c>BSI.BSA</c> is not.
///     </para>
///     <para>
///         ⚠ Meshes resolve against the archives sitting BESIDE the opened file, so this works for
///         a <c>.BS6</c> or <c>BLOCKS.BSA</c> opened from a real install and not for one copied
///         somewhere on its own. That is the same rule <c>classic level export</c> follows.
///     </para>
/// </summary>
internal static class ClassicLevelPreviewSource
{
    /// <summary>
    ///     Largest payload this will probe. Retail levels are ~60 KB (BS6.BSA is 2.8 MB across 47
    ///     entries); the cap keeps the probe from pulling a multi-megabyte entry just to read four
    ///     bytes, and a payload over it is not a level.
    /// </summary>
    private const int LevelProbeBudget = 4 * 1024 * 1024;

    /// <summary>The four bytes every Battlespire level opens with.</summary>
    private static ReadOnlySpan<byte> LevelTag => "GNRL"u8;

    /// <summary>
    ///     True when a node's CONTENT is a level this can assemble.
    ///     <para>
    ///         ⛔⛔ <b>Do NOT gate this on the source's <c>.bs6</c> extension.</b> That was the
    ///         original test and it is wrong in BOTH directions, because Battlespire's archive
    ///         naming is inverted: <c>3D.BS6</c> is the MESH archive (2,115 <c>.3D</c> entries)
    ///         while <c>BS6.BSA</c> is the LEVEL archive (47 entries, 45 levels). The extension
    ///         test therefore claimed all 2,115 meshes as levels — every one of which then failed
    ///         to assemble — and rejected the only archive that actually holds levels, so the GUI's
    ///         3D level pane could never open one. CLAUDE.md flags exactly this trap for this game:
    ///         "3D.BS6 is a BSA despite the extension".
    ///     </para>
    ///     <para>
    ///         The content is the honest test: a level's first chunk tag is <c>GNRL</c>, which is
    ///         what <see cref="Bs6File.Parse" /> itself requires.
    ///     </para>
    /// </summary>
    public static bool CanPreview(AssetBrowseSession session, AssetNode node)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(node);

        if (node.Kind == AssetNodeKind.Folder)
        {
            return false;
        }

        var head = session.FileSystem.TryReadAllBytesBounded(node.VirtualPath, LevelProbeBudget);
        if (head is null)
        {
            return false;
        }

        var data = head.Data.AsSpan();
        return (data.Length >= LevelTag.Length && data[..LevelTag.Length].SequenceEqual(LevelTag))
               || DaggerfallRdbBlock.IsRdb(data)
               || DaggerfallRmbBlock.IsRmb(data)
               || RedguardRgmFile.IsRgmFile(data)
               || ArenaLevelPreview.IsMif(data);
    }

    /// <summary>
    ///     Assembles the level, or returns null when it is not one or resolves no geometry.
    ///     <para>
    ///         A level that resolves NONE of its placements returns null rather than an empty scene:
    ///         an empty viewer looks like a working viewer pointed at an empty level, which is a
    ///         worse report than saying nothing loaded.
    ///     </para>
    /// </summary>
    public static BethesdaViewerScene? TryLoad(
        AssetBrowseSession session, AssetNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(node);

        if (!CanPreview(session, node))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var bytes = session.FileSystem.TryReadAllBytes(node.VirtualPath);
            if (bytes is null || bytes.Length == 0)
            {
                return null;
            }

            var meshDirectory = Path.GetDirectoryName(Path.GetFullPath(session.SourcePath));
            if (meshDirectory is null)
            {
                return null;
            }

            var generated = new Dictionary<string, DecodedTexture>(StringComparer.OrdinalIgnoreCase);
            var instances = Assemble(bytes, node.Name, meshDirectory, generated, out var textureFor);
            if (instances.Count == 0)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var stem = Path.GetFileNameWithoutExtension(node.Name).ToUpperInvariant();
            var scene = XnGineViewerSceneAdapter.ToViewerScene(stem, instances, textureFor);
            var viewerScene = BethesdaViewerSceneGlbAdapter.FromGlbScene(
                scene, stem, BethesdaViewerScenePurpose.ClassicMesh);
            foreach (var (path, texture) in generated)
            {
                viewerScene.AddGeneratedTexture(path, texture);
            }

            return viewerScene;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException
                                      or IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Routes the payload to its game's assembler by CONTENT and returns the placed instances,
    ///     empty when nothing resolved. <paramref name="generated" /> collects the RGBA textures the
    ///     scene must register (keyed by the lookup path <paramref name="textureFor" /> hands out),
    ///     and <paramref name="textureFor" /> is null for a game whose art is not resolved here.
    /// </summary>
    private static IReadOnlyList<XnGineMeshInstance> Assemble(
        byte[] bytes, string name, string meshDirectory,
        Dictionary<string, DecodedTexture> generated, out Func<int, int, XnGineViewerTexture?>? textureFor)
    {
        textureFor = null;
        if (RedguardRgmFile.IsRgmFile(bytes))
        {
            return AssembleRedguard(bytes, name, meshDirectory, generated, out textureFor);
        }

        // Arena .MIF: a voxel level, built (not placed) by ArenaSceneAssembler and textured through
        // the install's .INF + GLOBAL.BSA/PAL.COL, the route classic level export also takes.
        if (ArenaLevelPreview.IsMif(bytes))
        {
            return ArenaLevelPreview.Assemble(bytes, name, meshDirectory, generated, out textureFor);
        }

        if (DaggerfallRdbBlock.IsRdb(bytes))
        {
            var rdb = DaggerfallRdbBlock.Parse(bytes, name);
            var library = DaggerfallMeshLibrary.Open(meshDirectory);
            return DaggerfallRdbSceneAssembler
                .Assemble(rdb.Name, rdb.ModelReferences, rdb.AllObjects, library.Resolve).Instances;
        }

        if (DaggerfallRmbBlock.IsRmb(bytes))
        {
            var rmb = DaggerfallRmbBlock.Parse(bytes, name);
            var library = DaggerfallMeshLibrary.Open(meshDirectory);
            return DaggerfallBlockSceneAssembler
                .Assemble(rmb.Name, rmb.SubRecords, library.Resolve).Instances;
        }

        var level = Bs6File.Parse(bytes, name);
        using var meshes = BattlespireMeshLibrary.Open(meshDirectory);
        var assembly = Bs6SceneAssembler.Assemble(level, meshes.Resolve);

        // ⚑ Textured since 2026-09-08, the same way `classic level export` is: a plane's 32-bit key
        // NAMES its BSI image (base-40, GAME.EXE FUN_00075154), and the flats name theirs in FILN.
        // The pixels are handed to the viewer as generated textures, so no file is written.
        using var textures = BattlespireTextureResolver.Open(meshDirectory);
        using var sprites = BattlespireFlatSpriteSource.Open(meshDirectory);
        var flats = Bs6SceneAssembler.AssembleFlats(level.Flats, sprites.SizeOf, sprites.Register);
        IReadOnlyList<XnGineMeshInstance> instances = [.. assembly.Instances, .. flats.Instances];

        // ⚠ Resolved EAGERLY, here, because both sources are disposed when this method returns and
        // the adapter asks for textures afterwards; a lazy closure would read a closed archive.
        var lookup = new Dictionary<(int Archive, int Record), XnGineViewerTexture>();
        foreach (var material in instances.SelectMany(i => i.Mesh.SubMeshes)
                     .Select(s => (s.TextureArchive, s.TextureRecord)).Distinct())
        {
            var (archive, record) = material;
            var isFlat = archive == Bs6FlatBillboard.FlatTextureArchive;
            var path = isFlat
                ? sprites.TexturePathFor(record)
                : BattlespireTextureResolver.TexturePathFor(archive, record);
            if (path is null)
            {
                continue;
            }

            if (!generated.TryGetValue(path, out var texture))
            {
                texture = isFlat ? sprites.ResolveDecoded(archive, record) : textures.ResolveDecoded(archive, record);
                if (texture is null)
                {
                    continue;
                }

                generated[path] = texture;
            }

            lookup[material] = new XnGineViewerTexture(path, texture.Width, texture.Height);
        }

        textureFor = (archive, record) => lookup.GetValueOrDefault((archive, record));
        return instances;
    }

    /// <summary>
    ///     Redguard: a <c>maps\*.RGM</c> resolves its meshes, palette and terrain through its
    ///     INSTALL, not through the directory beside it — the ROB lives in <c>3dart</c>, the palette
    ///     and terrain are named by <c>WORLD.INI</c> at the data root — so the root is found by
    ///     walking up from the opened source to the directory holding <c>WORLD.INI</c>. Placed by
    ///     <see cref="RedguardSceneAssembler" />, textured through <c>3dart\TEXTURE.nnn</c> (or the
    ///     3dfx <c>fxart</c> when the install is Disc 1's), the same route <c>classic level export</c>
    ///     takes.
    /// </summary>
    private static IReadOnlyList<XnGineMeshInstance> AssembleRedguard(
        byte[] bytes, string name, string meshDirectory,
        Dictionary<string, DecodedTexture> generated, out Func<int, int, XnGineViewerTexture?>? textureFor)
    {
        textureFor = null;
        var dataRoot = RedguardLevelLoader.FindDataRoot(meshDirectory)
                       ?? RedguardLevelLoader.FindDataRoot(Path.Combine(meshDirectory, name));
        if (dataRoot is null)
        {
            return [];
        }

        using var level = RedguardLevelLoader.Load(bytes, name, dataRoot);
        if (level.Scene.Instances.Count == 0)
        {
            return [];
        }

        // Resolved EAGERLY for the same reason as Battlespire's: the level (and its ROB) is disposed
        // when this returns, and the adapter asks for textures afterwards.
        var lookup = new Dictionary<(int Archive, int Record), XnGineViewerTexture>();
        foreach (var material in level.Instances.SelectMany(i => i.Mesh.SubMeshes)
                     .Select(s => (s.TextureArchive, s.TextureRecord)).Distinct())
        {
            var (archive, record) = material;
            var path = RedguardTextureResolver.TexturePathFor(archive, record);
            if (!generated.TryGetValue(path, out var texture))
            {
                texture = level.Textures.ResolveDecoded(archive, record);
                if (texture is null)
                {
                    continue;
                }

                generated[path] = texture;
            }

            lookup[material] = new XnGineViewerTexture(path, texture.Width, texture.Height);
        }

        textureFor = (archive, record) => lookup.GetValueOrDefault((archive, record));
        return level.Instances;
    }
}
