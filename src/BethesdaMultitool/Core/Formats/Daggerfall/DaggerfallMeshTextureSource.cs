using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>
///     Resolves a mesh's (archive, record) texture references to PNG images from an ARENA2
///     directory: <c>TEXTURE.nnn</c> record <c>r</c>, first frame, through <c>ART_PAL.COL</c>.
///     Archives are parsed once and cached; a missing archive, record or palette yields null so
///     the export proceeds untextured for that material.
/// </summary>
internal sealed class DaggerfallMeshTextureSource
{
    private readonly Dictionary<int, DaggerfallTextureFile?> _archives = new();
    private readonly Dictionary<(int Archive, int Record), XnGineTexturePng?> _cache = new();
    private readonly string _dataRoot;
    private Palette? _palette;
    private bool _paletteResolved;

    public DaggerfallMeshTextureSource(string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);
        _dataRoot = dataRoot;
    }

    /// <summary>The PNG for a texture reference, or null when it cannot be resolved.</summary>
    public XnGineTexturePng? Resolve(int archive, int record)
    {
        if (_cache.TryGetValue((archive, record), out var cached))
        {
            return cached;
        }

        var png = Decode(archive, record);
        _cache[(archive, record)] = png;
        return png;
    }

    private XnGineTexturePng? Decode(int archive, int record)
    {
        var palette = ResolvePalette();
        var file = LoadArchive(archive);
        if (palette is null || file is null)
        {
            return null;
        }

        var textureRecord = file.Records.FirstOrDefault(r => r.Index == record);
        if (textureRecord is null || textureRecord.Frames.Count == 0)
        {
            return null;
        }

        var frame = textureRecord.Frames[0];
        var decoded = frame.ToDecodedTexture(palette);
        return new XnGineTexturePng(PngWriter.EncodeRgba(decoded.Pixels, decoded.Width, decoded.Height), decoded.Width,
            decoded.Height);
    }

    private Palette? ResolvePalette()
    {
        if (_paletteResolved)
        {
            return _palette;
        }

        _paletteResolved = true;
        var path = Path.Combine(_dataRoot, "ART_PAL.COL");
        if (File.Exists(path))
        {
            _palette = Palette.LoadDaggerfallCol(File.ReadAllBytes(path));
        }

        return _palette;
    }

    private DaggerfallTextureFile? LoadArchive(int archive)
    {
        if (_archives.TryGetValue(archive, out var cached))
        {
            return cached;
        }

        var name = "TEXTURE." + archive.ToString("D3", CultureInfo.InvariantCulture);
        var path = Path.Combine(_dataRoot, name);
        DaggerfallTextureFile? file = null;
        if (File.Exists(path) && !DaggerfallTextureFile.IsUnsupported(name))
        {
            try
            {
                file = DaggerfallTextureFile.Parse(File.ReadAllBytes(path), name);
            }
            catch (InvalidDataException)
            {
                file = null;
            }
        }

        _archives[archive] = file;
        return file;
    }
}
