using System.Globalization;
using BethesdaMultitool.Core.Formats.Arena;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Fallout;
using BethesdaMultitool.Core.Formats.Tactics;
using BethesdaMultitool.Core.Formats.Travels.OblivionMobile;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Core.Formats.VanBuren;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Rendering.Level2D;
using BethesdaMultitool.Core.Vfs;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     Decides which classic level a browsed file is, and builds the matching
///     <see cref="ILevel2DSource" /> for the 2D map pane.
///     <para>
///         This is the "top-down view of the geometry" route — the equivalent of the terrain layer
///         with meshes drawn on. It exists because several classic level formats ARE grids: an
///         Arena <c>.MIF</c> is a voxel grid and Daggerfall's <c>WOODS.WLD</c> is a heightmap, so
///         building a 3D surface to look at one is a detour through a lossier representation.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The set of openable files is <see cref="Level2DViewPolicy" />'s to state, not this
///             class's.
///         </b>
///         Both used to keep their own extension list and they drifted: the policy
///         admitted <c>.ZMP</c> that this class could not build, so the UI offered a Shadowkey zone
///         a 2D view and then showed an empty pane. One list, here delegated to, is the fix.
///     </para>
///     <para>
///         ⚠ Layer colours are DIAGNOSTIC — a stable hue per voxel id, 0 = black — not the game's
///         textures. Resolving real textures needs the level's <c>.INF</c> plus tables still inside
///         the packed <c>A.EXE</c>. A viewer that presented these as the game's own art would be
///         lying about what has been decoded.
///     </para>
/// </summary>
internal static class AssetLevel2DSource
{
    /// <summary>True for a file this build can show as a 2D level.</summary>
    public static bool CanOpen(AssetNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return Level2DViewPolicy.Supports(node.Name);
    }

    /// <summary>Builds the source for a node, or returns null when it is not a level this can show.</summary>
    public static ILevel2DSource? TryOpen(
        AssetBrowseSession session, AssetNode node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(node);

        if (!CanOpen(node))
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

            var extension = Path.GetExtension(node.Name);
            if (extension.Equals(".mif", StringComparison.OrdinalIgnoreCase))
            {
                return ArenaMapLevel2DSource.ForMifLevel(ArenaMifFile.Parse(bytes, node.Name), 0);
            }

            if (extension.Equals(".rmd", StringComparison.OrdinalIgnoreCase))
            {
                return ArenaMapLevel2DSource.ForRmd(ArenaRmdFile.Parse(bytes, node.Name), node.Name);
            }

            if (extension.Equals(".zmp", StringComparison.OrdinalIgnoreCase))
            {
                return OpenShadowkeyZone(session, node, bytes);
            }

            if (extension.Equals(".jtm", StringComparison.OrdinalIgnoreCase))
            {
                return OpenOblivionMobileLevel(session, node, bytes, cancellationToken);
            }

            if (extension.Equals(".emap", StringComparison.OrdinalIgnoreCase))
            {
                return OpenVanBurenMap(session, node, bytes);
            }

            if (extension.Equals(".mis", StringComparison.OrdinalIgnoreCase))
            {
                return OpenTacticsMission(session, node, bytes);
            }

            if (extension.Equals(".map", StringComparison.OrdinalIgnoreCase))
            {
                return FalloutMapFile.IsMapFile(bytes) ? OpenFalloutMap(session, node, bytes) : null;
            }

            // WOODS is the heightmap; a PAK is a single-plane overlay over the same grid.
            return Path.GetExtension(node.Name).Equals(".wld", StringComparison.OrdinalIgnoreCase)
                ? new DaggerfallMapLevel2DSource(DaggerfallWoodsFile.Parse(bytes, node.Name), null)
                : new DaggerfallMapLevel2DSource(null, DaggerfallPakFile.Parse(bytes, node.Name));
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException
                                      or IOException or ArgumentException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    ///     A Shadowkey zone: the <c>.zmp</c> cell grid plus the <c>.zcp</c> prototype table it
    ///     indexes, both size-prefixed zlib.
    ///     <para>
    ///         ⚠ The <c>.zcp</c> is REQUIRED, not an enrichment — a cell carries only an index into
    ///         it, so without the table there are no corner heights and nothing to draw.
    ///         <c>ForZone</c> validates the grid against it. The <c>.ent</c> placements are genuinely
    ///         optional and simply drop the overlay layer when absent.
    ///     </para>
    /// </summary>
    private static ShadowkeyZoneLevel2DSource? OpenShadowkeyZone(
        AssetBrowseSession session, AssetNode node, byte[] gridBytes)
    {
        var prototypeName = SiblingName(node.Name, ".zcp");
        if (ReadSibling(session, node, ".zcp") is not { } prototypeBytes)
        {
            return null;
        }

        var map = ShadowkeyZoneMap.Parse(
            ShadowkeyCompressedFile.Inflate(gridBytes, node.Name), node.Name);
        var prototypes = ShadowkeyCellPrototypes.Parse(
            ShadowkeyCompressedFile.Inflate(prototypeBytes, prototypeName), prototypeName);

        var entityName = SiblingName(node.Name, ".ent");
        var entities = ReadSibling(session, node, ".ent") is { } entityBytes
            ? ShadowkeyZoneFiles.ParseEnt(entityBytes, entityName)
            : null;

        return ShadowkeyZoneLevel2DSource.ForZone(map, prototypes, entities);
    }

    /// <summary>
    ///     An Oblivion mobile level: the <c>.jtm</c> tile grid drawn through its <c>.cml</c> atlas.
    ///     <para>
    ///         ⚠⚠ <b>The atlas is NOT the same-stem file.</b> A <c>.jtm</c> never names its atlas;
    ///         the <c>.scr</c> scripts do, so every script in the install is read to resolve the
    ///         pairing through <see cref="OblivionMobileAtlasPairing" /> — the same index the record
    ///         browser uses. Pairing by stem instead would draw <c>l04_1</c>, <c>l06_1</c> and
    ///         <c>l10_1</c> with the wrong tileset, and the result would look entirely plausible.
    ///     </para>
    ///     <para>
    ///         A map no script mentions (retail: <c>l01_r.jtm</c>) resolves to no atlas and gets no
    ///         view, rather than being drawn with a guessed one.
    ///     </para>
    /// </summary>
    private static OblivionMobileLevel2DSource? OpenOblivionMobileLevel(
        AssetBrowseSession session, AssetNode node, byte[] mapBytes, CancellationToken cancellationToken)
    {
        var assets = IndexAssets(session.FileSystem);

        var scripts = new List<OblivionMobileScript>();
        foreach (var (name, path) in assets)
        {
            if (!name.EndsWith(".scr", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (session.FileSystem.TryReadAllBytes(path) is { } scriptBytes)
            {
                scripts.Add(OblivionMobileScript.Parse(scriptBytes, name));
            }
        }

        var pairing = OblivionMobileAtlasPairing.Build(scripts);
        if (OblivionMobileAtlasPairing.ResolveSingle(pairing, node.Name) is not { } atlasName ||
            !assets.TryGetValue(atlasName, out var atlasPath) ||
            session.FileSystem.TryReadAllBytes(atlasPath) is not { } atlasBytes)
        {
            return null;
        }

        return OblivionMobileLevel2DSource.ForMap(
            OblivionMobileTileMap.Parse(mapBytes, node.Name),
            OblivionMobileAtlas.Parse(atlasBytes, atlasName),
            sheet => assets.TryGetValue(OblivionMobileAtlasPairing.FileNameOf(sheet), out var sheetPath)
                ? session.FileSystem.TryReadAllBytes(sheetPath)
                : null,
            node.Name);
    }

    /// <summary>
    ///     A Van Buren level: the <c>EMAP</c> plus the <c>8TRE</c> scene and walk grid its header
    ///     names, both other entries of the same <c>.grp</c>. The archive's entries carry no names,
    ///     so the pairing goes through <c>resource.rht</c> beside the build's <c>data/</c> when the
    ///     session was opened on a file there, and otherwise through the scene's own texture names.
    /// </summary>
    private static VanBurenMapLevel2DSource? OpenVanBurenMap(AssetBrowseSession session, AssetNode node, byte[] mapBytes)
    {
        var map = VanBurenMapFile.Parse(mapBytes, node.Name);
        var candidates = new List<VanBurenMapCompanions.Candidate>();
        foreach (var entry in session.FileSystem.EnumerateFiles())
        {
            var name = Path.GetFileName(entry.Path);
            // The GRP backend names entries NNNNN.<TAG>, the tag being the payload's own four
            // bytes with unprintable ones as dots; that is enough to skip the tagged families.
            var stem = Path.GetFileNameWithoutExtension(name);
            if (stem.Length != 5 || !int.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                continue;
            }

            var extension = Path.GetExtension(name).TrimStart('.');
            candidates.Add(
                new VanBurenMapCompanions.Candidate(index, entry.Path, extension == "BIN" ? "...." : extension));
        }

        var index2 = File.Exists(session.SourcePath) ? VanBurenResourceIndex.TryLoadBeside(session.SourcePath) : null;
        var level = VanBurenMapCompanions.Resolve(
            map,
            Path.GetFileNameWithoutExtension(session.SourcePath),
            candidates,
            path => session.FileSystem.TryReadAllBytes(path),
            index2);
        return new VanBurenMapLevel2DSource(level);
    }

    /// <summary>
    ///     A Fallout Tactics mission: the inflated world's tile grid drawn from the <c>.til</c> art
    ///     each tile names, resolved through the session's file system — the <c>tiles/…</c> paths
    ///     are relative to the <c>core</c> mount, where <c>tiles_0.bos</c> supplies every one the
    ///     retail missions place. A world with no grid (a save's) yields no view.
    /// </summary>
    private static TacticsMissionLevel2DSource? OpenTacticsMission(AssetBrowseSession session, AssetNode node, byte[] bytes)
    {
        if (!TacticsMissionFile.TryParse(bytes, node.Name, out var mission, out _)
            || !TacticsMissionWorld.TryParse(mission, out var world, out _))
        {
            return null;
        }

        return new TacticsMissionLevel2DSource(
            world,
            path => session.FileSystem.TryReadAllBytes(path),
            Path.GetFileNameWithoutExtension(node.Name));
    }

    /// <summary>
    ///     A Fallout 1/2 map: its tile grids and placed objects drawn in the game's own art
    ///     (<see cref="FalloutMapLevel2DSource" />). The object section sizes its records by the prototypes
    ///     they point at, and the art is named by <c>ART\*\*.LST</c>, so both come from the install the map
    ///     belongs to: the session's own mount when it was opened on an install root, otherwise the install
    ///     found around the opened file — a map browsed inside a lone <c>MASTER.DAT</c> still draws its
    ///     critters, which live in <c>CRITTER.DAT</c> beside it. A map with no install around it (a stray
    ///     copy in a scratch folder) gets no view rather than a floor with nothing on it.
    /// </summary>
    private static ILevel2DSource? OpenFalloutMap(AssetBrowseSession session, AssetNode node, byte[] bytes)
    {
        var files = session.FileSystem;
        if (session.Profile is null || File.Exists(session.SourcePath))
        {
            if (ClassicGameLocator.DetectRootForFile(session.SourcePath) is not { } detected
                || detected.Profile.Game is not (BethesdaGame.Fallout1 or BethesdaGame.Fallout2))
            {
                return null;
            }

            // Transfer the private mount to the preview; failed opens and discarded previews
            // must release the archive handles as well as successfully displayed previews.
            files = GameFileSystem.OpenGameRoot(detected.Profile, detected.Root);
            return OwnedLevel2DSource.Create(files, () => ParseFalloutMap(files, node, bytes));
        }

        return ParseFalloutMap(files, node, bytes);
    }

    private static FalloutMapLevel2DSource? ParseFalloutMap(IGameFileSystem files, AssetNode node, byte[] bytes)
    {
        byte[]? Read(string path)
        {
            return files.TryReadAllBytes(path);
        }

        var prototypes = FalloutMapPrototypes.Load(Read, false);
        if (!FalloutMapFile.TryParse(bytes, node.Name, out var map, out _, prototypes.SubtypeOf))
        {
            return null;
        }

        return new FalloutMapLevel2DSource(map, new FalloutMapArt(Read));
    }

    /// <summary>
    ///     Every file in the install keyed by BARE NAME. A JAR nests its resources in folders and an
    ///     unpacked copy is the same tree, so the bare name is the identity both mounts agree on —
    ///     which is also how the scripts write their references.
    /// </summary>
    private static Dictionary<string, string> IndexAssets(IGameFileSystem install)
    {
        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in install.EnumerateFiles().OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            assets.TryAdd(OblivionMobileAtlasPairing.FileNameOf(entry.Path), entry.Path);
        }

        return assets;
    }

    /// <summary>The node's name with a different extension.</summary>
    private static string SiblingName(string fileName, string extension)
    {
        return Path.ChangeExtension(fileName, extension);
    }

    /// <summary>
    ///     A file beside <paramref name="node" /> with the same stem, or null when it is absent.
    ///     Resolved through the node's own virtual path so it works for a loose tree and an archive
    ///     mount alike.
    /// </summary>
    private static byte[]? ReadSibling(AssetBrowseSession session, AssetNode node, string extension)
    {
        var sibling = Path.ChangeExtension(node.VirtualPath, extension);
        return sibling is null ? null : session.FileSystem.TryReadAllBytes(sibling);
    }
}
