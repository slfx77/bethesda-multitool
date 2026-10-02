using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Redguard;

/// <summary>Everything one loaded Redguard level carries: the map, its placed geometry and its textures.</summary>
internal sealed class RedguardLevelAssembly : IDisposable
{
    private readonly RedguardMeshLibrary _meshes;

    public RedguardLevelAssembly(
        RedguardRgmFile map,
        string stem,
        RedguardMeshLibrary meshes,
        RedguardSceneAssembly scene,
        RedguardFlatAssembly flats,
        XnGineMeshInstance? terrain,
        string? terrainName,
        RedguardTextureResolver textures,
        string paletteName,
        IReadOnlyList<XnGineMeshInstance> instances)
    {
        Map = map;
        Stem = stem;
        _meshes = meshes;
        Scene = scene;
        Flats = flats;
        Terrain = terrain;
        TerrainName = terrainName;
        Textures = textures;
        PaletteName = paletteName;
        Instances = instances;
    }

    /// <summary>The parsed map database.</summary>
    public RedguardRgmFile Map { get; }

    /// <summary>The map's upper-case stem (<c>ISLAND</c>).</summary>
    public string Stem { get; }

    /// <summary>The statics and placements.</summary>
    public RedguardSceneAssembly Scene { get; }

    /// <summary>The flat billboards.</summary>
    public RedguardFlatAssembly Flats { get; }

    /// <summary>The terrain, when the map's world is an outdoor one.</summary>
    public XnGineMeshInstance? Terrain { get; }

    /// <summary>The <c>.WLD</c> file the terrain came from, or null.</summary>
    public string? TerrainName { get; }

    /// <summary>The material resolver, over the world's palette.</summary>
    public RedguardTextureResolver Textures { get; }

    /// <summary>The <c>.COL</c> palette the textures were rendered through.</summary>
    public string PaletteName { get; }

    /// <summary>Every instance: statics, placements, flats, then the terrain.</summary>
    public IReadOnlyList<XnGineMeshInstance> Instances { get; }

    /// <summary>The mesh archive's name.</summary>
    public string MeshArchiveName => _meshes.ArchiveName;

    /// <summary>Segments in the mesh archive.</summary>
    public int MeshArchiveCount => _meshes.Count;

    /// <summary>Meshes that would not parse, an empty placeholder's loose file included.</summary>
    public IReadOnlyDictionary<string, string> MeshFailures => _meshes.Failures;

    /// <summary>
    ///     Where each resolved mesh came from, keyed by ROB segment name: the archive's own inline mesh,
    ///     or the loose <c>.3DC</c> an empty ROB placeholder stands for, drawn in its keyframe pose
    ///     (see <see cref="RedguardMeshLibrary" />).
    /// </summary>
    public IReadOnlyDictionary<string, RedguardMeshOrigin> MeshOrigins => _meshes.Origins;

    /// <summary>
    ///     One line stating the approximations behind <see cref="MeshOrigins" />' loose meshes, or null when
    ///     every mesh is inline (see <see cref="RedguardMeshLibrary.DescribeLooseKeyframes" />). The CLI prints it
    ///     and the GUI's 3D level pane shows it, so an approximated mesh is never drawn silently.
    /// </summary>
    public string? LooseKeyframeNote => RedguardMeshLibrary.DescribeLooseKeyframes(MeshOrigins);

    public void Dispose()
    {
        _meshes.Dispose();
    }
}

/// <summary>
///     Turns one <c>maps\*.RGM</c> into a placed scene by finding everything the map needs in its
///     install: the mesh archive <c>3dart\&lt;MAP&gt;.ROB</c> (and, for each of its empty placeholders,
///     the loose <c>3dart\&lt;NAME&gt;.3DC</c> it stands for), the world's palette and terrain from
///     <c>WORLD.INI</c>, the software art under <c>3dart</c> and — when the install is Disc 1's
///     tree — the 3dfx art under <c>fxart</c>. Shared by <c>classic level info|export</c> and the GUI's
///     3D level pane so both place geometry identically.
///     <para>
///         <b>Which world a map belongs to</b> comes from <c>WORLD.INI</c>'s <c>world_map</c>
///         entries; the FIRST world registering the map wins, which for ISLAND is world 1 (day —
///         <c>island.COL</c>, <c>ISLAND.WLD</c>) rather than its night and sunset re-registrations.
///         A map no world registers (<c>HIDEOUT.RGM</c> on retail) falls back to a same-stem
///         <c>.COL</c> and <c>.WLD</c> beside its files, then to <c>art_pal.col</c> and no terrain.
///     </para>
/// </summary>
internal static class RedguardLevelLoader
{
    /// <summary>The registry every install carries at its data root.</summary>
    public const string RegistryFileName = RedguardWorldIni.FileName;

    private const string FxArtDirectoryName = "fxart";
    private const string MapsDirectoryName = "maps";
    private const string DefaultPaletteName = "art_pal.col";
    private const int MaxAncestorProbes = 4;

    /// <summary>
    ///     The data root — the directory holding <c>WORLD.INI</c> — for a path inside an install,
    ///     probing the path itself and up to four ancestors. Null when none holds the registry.
    /// </summary>
    public static string? FindDataRoot(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string? current;
        try
        {
            var full = Path.GetFullPath(path);
            current = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }

        for (var depth = 0; current is not null && depth <= MaxAncestorProbes; depth++)
        {
            if (File.Exists(Path.Combine(current, RegistryFileName)))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    /// <summary>Loads a map from its file, resolving the install from the file's own location.</summary>
    public static RedguardLevelAssembly Load(string rgmPath, string? fxArtDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(rgmPath);

        var dataRoot = FindDataRoot(rgmPath)
                       ?? throw new FileNotFoundException(
                           $"No {RegistryFileName} above '{rgmPath}' — a Redguard map resolves its meshes, palette and terrain through its install.",
                           rgmPath);
        return Load(File.ReadAllBytes(rgmPath), Path.GetFileName(rgmPath), dataRoot, fxArtDirectory);
    }

    /// <summary>
    ///     Loads a map from its bytes and a data root. <paramref name="fxArtDirectory" /> overrides
    ///     the 3dfx art location; by default <c>fxart</c> under the data root is used when present.
    /// </summary>
    public static RedguardLevelAssembly Load(
        byte[] rgmBytes, string name, string dataRoot, string? fxArtDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(rgmBytes);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(dataRoot);

        var stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        var map = RedguardRgmFile.Parse(rgmBytes, name);

        var robPath = RedguardMeshLibrary.FindArchive(dataRoot, stem)
                      ?? throw new FileNotFoundException(
                          $"No {RedguardMeshLibrary.ArtDirectoryName}\\{stem}.ROB under '{dataRoot}': a map's own archive names every mesh it places.",
                          Path.Combine(dataRoot, RedguardMeshLibrary.ArtDirectoryName, stem + ".ROB"));

        var meshes = RedguardMeshLibrary.Open(robPath, dataRoot);
        try
        {
            var world = RegisteringWorld(dataRoot, stem);
            var palettePath = ResolvePalette(dataRoot, stem, world);
            var palette = Palette.LoadDaggerfallCol(File.ReadAllBytes(palettePath));
            var fxArt = fxArtDirectory ?? Path.Combine(dataRoot, FxArtDirectoryName);
            var textures = new RedguardTextureResolver(
                Path.Combine(dataRoot, RedguardMeshLibrary.ArtDirectoryName), palette,
                Directory.Exists(fxArt) ? fxArt : null);

            var scene = RedguardSceneAssembler.Assemble(map, meshes.Resolve);
            var flats = RedguardSceneAssembler.AssembleFlats(map.Flats, textures.SizeOf);

            XnGineMeshInstance? terrain = null;
            var terrainPath = ResolveTerrain(dataRoot, stem, world);
            if (terrainPath is not null)
            {
                var wld = RedguardWldFile.Parse(File.ReadAllBytes(terrainPath), Path.GetFileName(terrainPath));
                terrain = new XnGineMeshInstance(
                    RedguardTerrainMeshBuilder.Build(wld, textures.SizeOf),
                    System.Numerics.Matrix4x4.Identity,
                    "terrain_" + Path.GetFileNameWithoutExtension(terrainPath).ToUpperInvariant());
            }

            var instances = new List<XnGineMeshInstance>(scene.Instances.Count + flats.Instances.Count + 1);
            instances.AddRange(scene.Instances);
            instances.AddRange(flats.Instances);
            if (terrain is not null)
            {
                instances.Add(terrain);
            }

            return new RedguardLevelAssembly(
                map, stem, meshes, scene, flats, terrain,
                terrainPath is null ? null : Path.GetFileName(terrainPath),
                textures, Path.GetFileName(palettePath), instances);
        }
        catch
        {
            meshes.Dispose();
            throw;
        }
    }

    /// <summary>The first <c>WORLD.INI</c> world whose <c>world_map</c> names the map, or null.</summary>
    public static RedguardWorld? RegisteringWorld(string dataRoot, string stem)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);
        ArgumentNullException.ThrowIfNull(stem);

        var registryPath = Path.Combine(dataRoot, RegistryFileName);
        if (!File.Exists(registryPath))
        {
            return null;
        }

        var registry = RedguardWorldIni.Load(dataRoot);
        foreach (var world in registry.Worlds)
        {
            if (world.MapPath is { } mapPath &&
                Path.GetFileNameWithoutExtension(mapPath).Equals(stem, StringComparison.OrdinalIgnoreCase))
            {
                return world;
            }
        }

        return null;
    }

    private static string ResolvePalette(string dataRoot, string stem, RedguardWorld? world)
    {
        if (world?.PalettePath is { } declared && ResolveRelative(dataRoot, declared) is { } declaredPath)
        {
            return declaredPath;
        }

        var art = Path.Combine(dataRoot, RedguardMeshLibrary.ArtDirectoryName);
        return FindFile(art, stem + ".COL")
               ?? FindFile(art, DefaultPaletteName)
               ?? throw new FileNotFoundException(
                   $"No palette for {stem}: neither WORLD.INI names one nor does {RedguardMeshLibrary.ArtDirectoryName} hold {stem}.COL or {DefaultPaletteName}.",
                   Path.Combine(art, DefaultPaletteName));
    }

    private static string? ResolveTerrain(string dataRoot, string stem, RedguardWorld? world)
    {
        if (world is not null)
        {
            // A registered map takes its world's terrain — or none, for the 22 interiors.
            return world.TerrainPath is { } declared ? ResolveRelative(dataRoot, declared) : null;
        }

        return FindFile(Path.Combine(dataRoot, MapsDirectoryName), stem + ".WLD");
    }

    /// <summary>A registry path like <c>3DART\island.COL</c> resolved under the data root, case-insensitively.</summary>
    private static string? ResolveRelative(string dataRoot, string relative)
    {
        var normalised = relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var direct = Path.Combine(dataRoot, normalised);
        if (File.Exists(direct))
        {
            return direct;
        }

        var directory = Path.GetDirectoryName(direct);
        return directory is null ? null : FindFile(FindDirectory(dataRoot, directory) ?? directory, Path.GetFileName(direct));
    }

    private static string? FindDirectory(string dataRoot, string wanted)
    {
        if (Directory.Exists(wanted))
        {
            return wanted;
        }

        var name = Path.GetFileName(wanted);
        foreach (var candidate in Directory.EnumerateDirectories(dataRoot))
        {
            if (Path.GetFileName(candidate).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FindFile(string directory, string fileName)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var direct = Path.Combine(directory, fileName);
        if (File.Exists(direct))
        {
            return direct;
        }

        foreach (var path in Directory.EnumerateFiles(directory))
        {
            if (Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        return null;
    }
}
