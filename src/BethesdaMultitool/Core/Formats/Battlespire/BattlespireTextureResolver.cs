using System.Globalization;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Formats.Battlespire;

/// <summary>
///     Resolves a Battlespire mesh material — the (archive, record) pair <see cref="XnGineSubMesh" />
///     carries, which for this game is the plane's 32-bit texture key split into its high and low
///     words — to the BSI image it names, out of <c>BSI.BSA</c>.
///     <para>
///         The key decodes through <see cref="BattlespireTextureName" /> into a stem
///         (<c>wall35</c>), the archive entry <c>WALL35.BSI</c> is parsed with <see cref="BsiFile" />
///         and its first image's first frame is rendered through the 256-colour <c>CMAP</c> — the
///         same palette rule <c>sprite render</c> and the flat billboards follow. A colour plane
///         (key ≥ <see cref="BattlespireTextureName.SolidColorThreshold" />) becomes a small solid
///         texture so the GLB and the viewer treat every material alike.
///     </para>
///     <para>
///         Two outputs for the two consumers: <see cref="Resolve" /> hands the GLB exporter a PNG,
///         <see cref="ResolveDecoded" /> hands the native viewer RGBA pixels it registers as a
///         generated texture under <see cref="TexturePathFor" />. Both are cached per key.
///     </para>
///     <para>
///         ⚠ Twelve legal names decode from retail meshes that the shipped archive does not hold
///         (<c>strut0</c>, <c>hand00..09</c>, <c>presto</c>, <c>keyhol</c>, <c>wwheel</c> — 344 planes,
///         eleven meshes, EPUNCH.3D wanting the ten <c>handNN</c>). They are reported through
///         <see cref="MissingNames" />, never guessed at: BSI.BSA does hold <c>HAND</c>, <c>STRUT01</c>
///         and <c>KEYHOLE3</c>, but the game's own loader would fail those keys exactly as this does.
///     </para>
/// </summary>
internal sealed class BattlespireTextureResolver : IDisposable
{
    /// <summary>Pixel size of the texture stood in for a solid-colour plane.</summary>
    public const int SolidColorTextureSize = 4;

    private const string ArchiveName = "BSI.BSA";
    private const string ImageExtension = ".BSI";

    private readonly ArchiveReader? _archive;
    private readonly Dictionary<uint, DecodedTexture?> _decoded = [];
    private readonly Dictionary<string, string> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _missing = [];
    private readonly HashSet<string> _missingSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, XnGineTexturePng?> _pngs = [];

    private BattlespireTextureResolver(ArchiveReader? archive)
    {
        _archive = archive;
        if (archive is null)
        {
            return;
        }

        foreach (var entry in archive.ListFiles())
        {
            _entries[entry.Name] = entry.FullPath;
        }
    }

    /// <summary>Whether <c>BSI.BSA</c> was found; without it only colour planes resolve.</summary>
    public bool HasArchive => _archive is not null;

    /// <summary>Names that decoded legally but the archive lacks, in first-seen order.</summary>
    public IReadOnlyList<string> MissingNames => _missing;

    public void Dispose()
    {
        _archive?.Dispose();
    }

    /// <summary>Opens <c>BSI.BSA</c> from a GAMEDATA directory; a missing archive yields a resolver that resolves only colour planes.</summary>
    public static BattlespireTextureResolver Open(string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);

        var path = Path.Combine(dataRoot, ArchiveName);
        return new BattlespireTextureResolver(File.Exists(path) ? ArchiveReader.Open(path) : null);
    }

    /// <summary>Reassembles the plane key from the (archive, record) pair a sub-mesh carries.</summary>
    public static uint Key(int archive, int record)
    {
        return ((uint)archive << 16) | ((uint)record & 0xFFFF);
    }

    /// <summary>The BSI stem a material names, or null for a colour plane or an undecodable key.</summary>
    public static string? NameOf(int archive, int record)
    {
        return BattlespireTextureName.Decode(Key(archive, record));
    }

    /// <summary>
    ///     The canonical lookup path a viewer scene registers the material's pixels under:
    ///     <c>battlespire/bsi/wall35</c> for a named texture, <c>battlespire/color/7fff</c> for a
    ///     colour plane (its 15-bit colour), and null for a key that names nothing.
    /// </summary>
    public static string? TexturePathFor(int archive, int record)
    {
        var key = Key(archive, record);
        if (BattlespireTextureName.IsSolidColor(key))
        {
            return string.Create(CultureInfo.InvariantCulture, $"battlespire/color/{(key & 0xFFFF) >> 1:x4}");
        }

        var name = BattlespireTextureName.Decode(key);
        return name is null ? null : "battlespire/bsi/" + name;
    }

    /// <summary>The material's PNG, named after its BSI stem, or null when it cannot be resolved.</summary>
    public XnGineTexturePng? Resolve(int archive, int record)
    {
        var key = Key(archive, record);
        if (_pngs.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var texture = ResolveDecoded(archive, record);
        XnGineTexturePng? png = null;
        if (texture is not null)
        {
            png = new XnGineTexturePng(
                PngWriter.EncodeRgba(texture.Pixels, texture.Width, texture.Height),
                texture.Width,
                texture.Height,
                MaterialNameFor(key));
        }

        _pngs[key] = png;
        return png;
    }

    /// <summary>The material's RGBA pixels, or null when it cannot be resolved.</summary>
    public DecodedTexture? ResolveDecoded(int archive, int record)
    {
        var key = Key(archive, record);
        if (_decoded.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var texture = BattlespireTextureName.IsSolidColor(key) ? SolidTexture(key) : LoadNamed(key);
        _decoded[key] = texture;
        return texture;
    }

    /// <summary>A material name for the GLB: the BSI stem, or <c>color_7fff</c> for a colour plane.</summary>
    private static string MaterialNameFor(uint key)
    {
        if (BattlespireTextureName.IsSolidColor(key))
        {
            return string.Create(CultureInfo.InvariantCulture, $"color_{(key & 0xFFFF) >> 1:x4}");
        }

        return BattlespireTextureName.Decode(key) ?? string.Create(CultureInfo.InvariantCulture, $"key_{key:x8}");
    }

    private static DecodedTexture SolidTexture(uint key)
    {
        var (r, g, b) = BattlespireTextureName.SolidColor(key);
        var pixels = new byte[SolidColorTextureSize * SolidColorTextureSize * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }

        return DecodedTexture.FromBaseLevel(pixels, SolidColorTextureSize, SolidColorTextureSize, false);
    }

    private DecodedTexture? LoadNamed(uint key)
    {
        var name = BattlespireTextureName.Decode(key);
        if (name is null || _archive is null)
        {
            return null;
        }

        if (!_entries.TryGetValue(name + ImageExtension, out var fullPath))
        {
            RecordMissing(name);
            return null;
        }

        try
        {
            var bytes = _archive.ReadFile(fullPath);
            if (bytes is null)
            {
                RecordMissing(name);
                return null;
            }

            var file = BsiFile.Parse(bytes, name);
            var image = file.Images.Count > 0 ? file.Images[0] : null;
            if (image is null || image.Frames.Count == 0 || image.ColorMap is null)
            {
                RecordMissing(name);
                return null;
            }

            return image.Frames[0].ToDecodedTexture(image.ColorMap);
        }
        catch (InvalidDataException)
        {
            // An image that will not decode leaves its planes untextured rather than failing the mesh.
            RecordMissing(name);
            return null;
        }
    }

    private void RecordMissing(string name)
    {
        if (_missingSet.Add(name))
        {
            _missing.Add(name);
        }
    }
}
