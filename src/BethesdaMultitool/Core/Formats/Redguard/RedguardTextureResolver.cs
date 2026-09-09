using System.Globalization;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Redguard;

/// <summary>
///     Resolves a Redguard mesh material — the <c>(archive, record)</c> pair a plane's texture word
///     packs as <c>archive·128 + record</c> — to an image.
///     <para>
///         ⚑ <b>The pair indexes <c>3dart\TEXTURE.nnn</c> directly</b> (measured 2026-09-08,
///         <c>census3.py</c>): every one of the 245,535 planes in the 4,667 ROB meshes names an
///         archive that ships (280 distinct) and a record below that archive's count — 245,535 of
///         245,535, zero out of range. That container is Daggerfall's, so decoding goes through
///         <see cref="DaggerfallTextureFile" /> and the world's <c>.COL</c> palette.
///     </para>
///     <para>
///         ⚑ <b>The 3dfx <c>fxart\TEXBSI.nnn</c> sets are index-parallel</b>: 413 of the 415 carry
///         exactly as many images as their <c>TEXTURE.nnn</c> twin has records (the exceptions are
///         set 0, the solid-colour table, and set 73), and 279 of the 280 archives the meshes use
///         have one. So when an <c>fxart</c> directory is supplied — Disc 1's install tree; the Steam
///         build ships none — image <c>record</c> of <c>TEXBSI.archive</c> is preferred, falling back
///         to the software art for a set or record it lacks. A TEXBSI still has no palette of its own
///         and is rendered through the same world palette; the 56 animated ones use their own.
///     </para>
///     <para>
///         Two outputs for the two consumers, as <see cref="Battlespire.BattlespireTextureResolver" />
///         does: <see cref="Resolve" /> hands the GLB exporter a PNG, <see cref="ResolveDecoded" />
///         hands the native viewer RGBA pixels it registers as a generated texture under
///         <see cref="TexturePathFor" />. Both are cached per pair.
///     </para>
/// </summary>
internal sealed class RedguardTextureResolver
{
    private readonly string _artDirectory;
    private readonly Dictionary<int, DaggerfallTextureFile?> _archives = [];
    private readonly Dictionary<(int, int), DecodedTexture?> _decoded = [];
    private readonly string? _fxArtDirectory;
    private readonly Dictionary<int, RedguardTexBsiFile?> _fxSets = [];
    private readonly Palette _palette;
    private readonly Dictionary<(int, int), XnGineTexturePng?> _pngs = [];

    /// <summary>
    ///     Creates a resolver over a <c>3dart</c> directory and a palette, optionally preferring an
    ///     <c>fxart</c> directory's TEXBSI sets.
    /// </summary>
    public RedguardTextureResolver(string artDirectory, Palette palette, string? fxArtDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(artDirectory);
        ArgumentNullException.ThrowIfNull(palette);

        _artDirectory = artDirectory;
        _palette = palette;
        _fxArtDirectory = fxArtDirectory is not null && Directory.Exists(fxArtDirectory) ? fxArtDirectory : null;
    }

    /// <summary>Whether TEXBSI sets are being preferred.</summary>
    public bool UsesFxArt => _fxArtDirectory is not null;

    /// <summary>Materials served from a TEXBSI image so far.</summary>
    public int FxArtHits { get; private set; }

    /// <summary>Materials served from a TEXTURE.nnn record so far.</summary>
    public int SoftwareArtHits { get; private set; }

    /// <summary>
    ///     The canonical lookup path a viewer scene registers the material's pixels under:
    ///     <c>redguard/texture/302/17</c>.
    /// </summary>
    public static string TexturePathFor(int archive, int record)
    {
        return string.Create(CultureInfo.InvariantCulture, $"redguard/texture/{archive:D3}/{record}");
    }

    /// <summary>The material's pixel size, or null when it cannot be resolved.</summary>
    public (int Width, int Height)? SizeOf(int archive, int record)
    {
        var texture = ResolveDecoded(archive, record);
        return texture is null ? null : (texture.Width, texture.Height);
    }

    /// <summary>The material's PNG, or null when it cannot be resolved.</summary>
    public XnGineTexturePng? Resolve(int archive, int record)
    {
        var key = (archive, record);
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
                string.Create(CultureInfo.InvariantCulture, $"TEXTURE.{archive:D3}#{record}"));
        }

        _pngs[key] = png;
        return png;
    }

    /// <summary>The material's RGBA pixels, or null when it cannot be resolved.</summary>
    public DecodedTexture? ResolveDecoded(int archive, int record)
    {
        var key = (archive, record);
        if (_decoded.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var texture = DecodeFx(archive, record);
        if (texture is not null)
        {
            FxArtHits++;
        }
        else
        {
            texture = DecodeSoftware(archive, record);
            if (texture is not null)
            {
                SoftwareArtHits++;
            }
        }

        _decoded[key] = texture;
        return texture;
    }

    private DecodedTexture? DecodeFx(int archive, int record)
    {
        if (_fxArtDirectory is null || record < 0)
        {
            return null;
        }

        if (!_fxSets.TryGetValue(archive, out var set))
        {
            set = null;
            var path = FindFile(_fxArtDirectory, string.Create(CultureInfo.InvariantCulture, $"TEXBSI.{archive:D3}"));
            if (path is not null)
            {
                try
                {
                    set = RedguardTexBsiFile.Parse(File.ReadAllBytes(path), Path.GetFileName(path));
                }
                catch (InvalidDataException)
                {
                    set = null;
                }
            }

            _fxSets[archive] = set;
        }

        if (set is null || record >= set.Images.Count)
        {
            return null;
        }

        var image = set.Images[record];
        if (image.Frames.Count == 0)
        {
            return null;
        }

        return image.Frames[0].ToDecodedTexture(image.Palette ?? _palette);
    }

    private DecodedTexture? DecodeSoftware(int archive, int record)
    {
        if (!_archives.TryGetValue(archive, out var file))
        {
            file = null;
            var name = "TEXTURE." + archive.ToString("D3", CultureInfo.InvariantCulture);
            var path = FindFile(_artDirectory, name);
            if (path is not null && !DaggerfallTextureFile.IsUnsupported(name))
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
        }

        if (file is null)
        {
            return null;
        }

        DaggerfallTextureRecord? textureRecord = null;
        foreach (var candidate in file.Records)
        {
            if (candidate.Index == record)
            {
                textureRecord = candidate;
                break;
            }
        }

        if (textureRecord is null || textureRecord.Frames.Count == 0)
        {
            return null;
        }

        return textureRecord.Frames[0].ToDecodedTexture(_palette);
    }

    /// <summary>A file by name, matched without regard to case (retail mixes <c>TEXTURE.302</c> and <c>texture.005</c>).</summary>
    private static string? FindFile(string directory, string fileName)
    {
        var direct = Path.Combine(directory, fileName);
        if (File.Exists(direct))
        {
            return direct;
        }

        if (!Directory.Exists(directory))
        {
            return null;
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
