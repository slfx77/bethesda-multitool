using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Arena;

/// <summary>
///     Everything an Arena level needs from its install to become a textured scene: the
///     <c>.INF</c> its <c>INFO</c> chunk names, the wall/floor art that .INF lists, and
///     <c>PAL.COL</c> to colour it — each read loose from the data directory first and from
///     <c>GLOBAL.BSA</c> second, which is the precedence the game applies (three of the five loose
///     .INF files differ from their archived namesakes).
///     <para>
///         ⚠ A level without an <c>INFO</c> chunk — every one of the 455 retail city-block and
///         whole-city levels — is textured by the game through a city .INF chosen from the
///         location's climate and the weather (<c>TCN.INF</c> = temperate, city, clear; the
///         reference's <c>ArenaCityUtils</c>). Nothing in the .MIF says which, so this library
///         defaults to <see cref="DefaultCityInf" /> and the wilderness to
///         <see cref="DefaultWildernessInf" />, and a caller may override either. That is a
///         presentation default, stated as such.
///     </para>
/// </summary>
internal sealed class ArenaLevelLibrary : IDisposable
{
    /// <summary>The archive the game keeps most of its data in.</summary>
    public const string GlobalArchiveName = "GLOBAL.BSA";

    /// <summary>The game-world palette.</summary>
    public const string PaletteName = "PAL.COL";

    /// <summary>City .INF used when a level names none: temperate climate, clear weather.</summary>
    public const string DefaultCityInf = "TCN.INF";

    /// <summary>Wilderness .INF used for .RMD chunks: temperate climate, clear weather.</summary>
    public const string DefaultWildernessInf = "TWN.INF";

    private readonly ArchiveReader? _archive;
    private readonly Dictionary<string, string> _archiveEntries;
    private readonly Dictionary<string, XnGineTexturePng?> _textureCache = new(StringComparer.OrdinalIgnoreCase);
    private Palette? _palette;
    private bool _paletteResolved;

    private ArenaLevelLibrary(string dataRoot, ArchiveReader? archive)
    {
        DataRoot = dataRoot;
        _archive = archive;
        _archiveEntries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (archive is not null)
        {
            foreach (var entry in archive.ListFiles())
            {
                _archiveEntries.TryAdd(entry.Name, entry.FullPath);
            }
        }
    }

    /// <summary>The ARENA data directory.</summary>
    public string DataRoot { get; }

    /// <summary>True when GLOBAL.BSA was found beside the data.</summary>
    public bool HasArchive => _archive is not null;

    /// <summary>
    ///     Finds the data directory a path belongs to: the directory itself or the nearest
    ///     ancestor holding <c>GLOBAL.BSA</c> or <c>PAL.COL</c>, or null.
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

        for (var depth = 0; current is not null && depth < 4; depth++)
        {
            if (File.Exists(Path.Combine(current, GlobalArchiveName)) || File.Exists(Path.Combine(current, PaletteName)))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    /// <summary>
    ///     <see cref="FindDataRoot" /> for a directory handed over by the asset browser, which is the
    ///     PARENT of the opened source: a folder session on the ARENA directory itself arrives as
    ///     its parent, so the immediate children are probed too, after the directory and its
    ///     ancestors.
    /// </summary>
    public static string? FindDataRootNear(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (FindDataRoot(directory) is { } root)
        {
            return root;
        }

        if (!Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateDirectories(directory)
                .FirstOrDefault(child => File.Exists(Path.Combine(child, GlobalArchiveName)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Opens the library on a data directory (GLOBAL.BSA optional, but expected).</summary>
    public static ArenaLevelLibrary Open(string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);

        var archivePath = Path.Combine(dataRoot, GlobalArchiveName);
        ArchiveReader? archive = null;
        if (File.Exists(archivePath))
        {
            archive = ArchiveReader.Open(archivePath);
        }

        return new ArenaLevelLibrary(dataRoot, archive);
    }

    /// <summary>Reads a data file by name — loose first, then the archive. Null when neither has it.</summary>
    public byte[]? ReadFile(string name, out bool fromArchive)
    {
        ArgumentNullException.ThrowIfNull(name);
        fromArchive = false;

        var loosePath = Path.Combine(DataRoot, name);
        if (File.Exists(loosePath))
        {
            return File.ReadAllBytes(loosePath);
        }

        if (_archive is not null && _archiveEntries.TryGetValue(name, out var fullPath))
        {
            fromArchive = true;
            return _archive.ReadFile(fullPath);
        }

        return null;
    }

    /// <summary>
    ///     Reads and parses an .INF, decrypting it when it came from the archive — residency, not
    ///     content, decides that. Null when the install has no such file.
    /// </summary>
    public ArenaInfFile? ResolveInf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var fileName = name.EndsWith(".INF", StringComparison.OrdinalIgnoreCase) ? name : name + ".INF";
        var bytes = ReadFile(fileName, out var fromArchive);
        return bytes is null ? null : ArenaInfFile.Parse(bytes, fileName.ToUpperInvariant(), fromArchive);
    }

    /// <summary>
    ///     The texture index a level resolves through: the .INF it names, or the world's default
    ///     when it names none (or when <paramref name="overrideInf" /> is given).
    /// </summary>
    /// <returns>Null when no candidate .INF exists in the install.</returns>
    public ArenaInfVoxelTextures? ResolveTextures(string? infoName, ArenaMapKind kind, string? overrideInf, out string resolvedName)
    {
        var candidate = overrideInf
                        ?? (string.IsNullOrWhiteSpace(infoName) ? null : infoName)
                        ?? (kind == ArenaMapKind.Wilderness ? DefaultWildernessInf : DefaultCityInf);
        resolvedName = candidate.ToUpperInvariant();
        if (!resolvedName.EndsWith(".INF", StringComparison.Ordinal))
        {
            resolvedName += ".INF";
        }

        var inf = ResolveInf(resolvedName);
        return inf is null ? null : ArenaInfVoxelTextures.FromInf(inf);
    }

    /// <summary>The world kind a .MIF level is: interior when it names its own .INF, city otherwise.</summary>
    public static ArenaMapKind KindOf(ArenaMifLevel level)
    {
        ArgumentNullException.ThrowIfNull(level);
        return string.IsNullOrWhiteSpace(level.InfoFile) ? ArenaMapKind.City : ArenaMapKind.Interior;
    }

    /// <summary>
    ///     A texture provider for the GLB exporter, bound to one level's index: maps the
    ///     assembler's (archive, slot) material keys to decoded PNGs.
    /// </summary>
    public Func<int, int, XnGineTexturePng?> TextureProviderFor(ArenaInfVoxelTextures textures)
    {
        ArgumentNullException.ThrowIfNull(textures);
        return (archive, record) =>
        {
            if (archive != ArenaSceneAssembler.TextureArchive || record < 0 || record >= textures.Slots.Count)
            {
                return null;
            }

            var slot = textures.Slots[record];
            return ResolveTexture(slot.FileName, slot.SetIndex);
        };
    }

    /// <summary>Decodes one wall/floor image (or one tile of a .SET) to PNG through PAL.COL; null when it cannot be.</summary>
    public XnGineTexturePng? ResolveTexture(string fileName, int? setIndex)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        var key = setIndex is { } tile ? $"{fileName}#{tile}" : fileName;
        if (_textureCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var png = DecodeTexture(fileName, setIndex);
        _textureCache[key] = png;
        return png;
    }

    /// <summary>
    ///     Decodes one wall/floor image (or one tile of a .SET) to RGBA through PAL.COL — the form
    ///     the GUI viewer registers as a generated texture. Null when the file or palette is missing
    ///     or the image will not decode.
    /// </summary>
    public DecodedTexture? ResolveDecoded(string fileName, int? setIndex)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        var palette = ResolvePalette();
        var bytes = ReadFile(fileName, out _);
        if (palette is null || bytes is null)
        {
            return null;
        }

        ArenaImgDecodeResult decoded;
        try
        {
            decoded = ArenaImgDecoder.Decode(bytes, fileName);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        var index = setIndex ?? 0;
        if (index < 0 || index >= decoded.Images.Count)
        {
            return null;
        }

        return decoded.Images[index].ToDecodedTexture(decoded.EmbeddedPalette ?? palette);
    }

    /// <summary>The generated-texture path the viewer keys one slot's image under.</summary>
    public static string ViewerTexturePathFor(ArenaVoxelTextureSlot slot)
    {
        return $"arena/texture/{slot.FileName.ToUpperInvariant()}/{slot.SetIndex ?? 0}";
    }

    private XnGineTexturePng? DecodeTexture(string fileName, int? setIndex)
    {
        var texture = ResolveDecoded(fileName, setIndex);
        if (texture is null)
        {
            return null;
        }

        return new XnGineTexturePng(
            PngWriter.EncodeRgba(texture.Pixels, texture.Width, texture.Height), texture.Width, texture.Height);
    }

    private Palette? ResolvePalette()
    {
        if (_paletteResolved)
        {
            return _palette;
        }

        _paletteResolved = true;
        var bytes = ReadFile(PaletteName, out _);
        if (bytes is not null)
        {
            try
            {
                _palette = Palette.LoadArenaCol(bytes);
            }
            catch (InvalidDataException)
            {
                _palette = null;
            }
        }

        return _palette;
    }

    public void Dispose()
    {
        _archive?.Dispose();
    }
}
