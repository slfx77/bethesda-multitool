using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Formats.Battlespire;

/// <summary>
///     Resolves a <c>FLAT</c>'s <c>FILN</c> sprite name to a decoded PNG out of <c>BSI.BSA</c>, and
///     hands out the record index a <see cref="Bs6FlatBillboard" /> is keyed by.
///     <para>
///         ⚑ This is what makes flats renderable without the undecoded mesh-texture mapping: a flat
///         NAMES its sprite, so it needs no numeric texture reference at all. 27 of the 28 distinct
///         retail names resolve, covering 2,272 of 2,319 references (2026-09-06).
///     </para>
///     <para>
///         ⚠ Names arrive WITHOUT an extension (<c>monster1</c>, <c>flmw00</c>) while the archive
///         stores <c>MONSTER1.BSI</c>, so the lookup appends the extension and is case-insensitive.
///     </para>
///     <para>
///         ⚠ Rendered through the 256-colour <c>CMAP</c>, not <c>HICL</c> — HICL fills only the 128
///         even slots and retail art indexes the whole range, the same rule
///         <c>sprite render</c> follows for BSI.
///     </para>
/// </summary>
internal sealed class BattlespireFlatSpriteSource : IDisposable
{
    private readonly ArchiveReader? _archive;
    private readonly List<string> _byRecord = [];
    private readonly Dictionary<string, string> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BsiImage?> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, XnGineTexturePng?> _pngs = [];
    private readonly Dictionary<string, int> _records = new(StringComparer.OrdinalIgnoreCase);

    private BattlespireFlatSpriteSource(ArchiveReader? archive)
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

    /// <summary>Sprite names registered so far, in record order.</summary>
    public IReadOnlyList<string> RegisteredNames => _byRecord;

    public void Dispose()
    {
        _archive?.Dispose();
    }

    /// <summary>Opens <c>BSI.BSA</c> from a GAMEDATA directory; a missing archive yields a source that resolves nothing.</summary>
    public static BattlespireFlatSpriteSource Open(string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);

        var path = Path.Combine(dataRoot, "BSI.BSA");
        return new BattlespireFlatSpriteSource(File.Exists(path) ? ArchiveReader.Open(path) : null);
    }

    /// <summary>The record index for a sprite name, assigning one on first use.</summary>
    public int Register(string spriteName)
    {
        ArgumentNullException.ThrowIfNull(spriteName);

        if (_records.TryGetValue(spriteName, out var existing))
        {
            return existing;
        }

        var record = _byRecord.Count;
        _records[spriteName] = record;
        _byRecord.Add(spriteName);
        return record;
    }

    /// <summary>The sprite's pixel size, or null when the archive has no such image.</summary>
    public (int Width, int Height)? SizeOf(string spriteName)
    {
        var image = LoadImage(spriteName);
        return image is null ? null : (image.Width, image.Height);
    }

    /// <summary>The PNG for a registered flat material, or null when it cannot be resolved.</summary>
    public XnGineTexturePng? Resolve(int archive, int record)
    {
        if (archive != Bs6FlatBillboard.FlatTextureArchive || record < 0 || record >= _byRecord.Count)
        {
            return null;
        }

        if (_pngs.TryGetValue(record, out var cached))
        {
            return cached;
        }

        var png = Decode(_byRecord[record]);
        _pngs[record] = png;
        return png;
    }

    /// <summary>The RGBA pixels for a registered flat material, for a viewer that takes generated textures.</summary>
    public DecodedTexture? ResolveDecoded(int archive, int record)
    {
        if (archive != Bs6FlatBillboard.FlatTextureArchive || record < 0 || record >= _byRecord.Count)
        {
            return null;
        }

        var image = LoadImage(_byRecord[record]);
        return image is null || image.Frames.Count == 0 || image.ColorMap is null
            ? null
            : image.Frames[0].ToDecodedTexture(image.ColorMap);
    }

    /// <summary>The lookup path a viewer scene registers a registered flat's pixels under, or null for an unknown record.</summary>
    public string? TexturePathFor(int record)
    {
        return record < 0 || record >= _byRecord.Count ? null : "battlespire/flat/" + _byRecord[record].ToLowerInvariant();
    }

    private XnGineTexturePng? Decode(string spriteName)
    {
        var image = LoadImage(spriteName);
        if (image is null || image.Frames.Count == 0 || image.ColorMap is null)
        {
            return null;
        }

        var decoded = image.Frames[0].ToDecodedTexture(image.ColorMap);
        return new XnGineTexturePng(
            PngWriter.EncodeRgba(decoded.Pixels, decoded.Width, decoded.Height), decoded.Width, decoded.Height,
            spriteName.ToLowerInvariant());
    }

    private BsiImage? LoadImage(string spriteName)
    {
        if (_images.TryGetValue(spriteName, out var cached))
        {
            return cached;
        }

        BsiImage? image = null;
        if (_archive is not null && _entries.TryGetValue(spriteName + ".BSI", out var fullPath))
        {
            try
            {
                var bytes = _archive.ReadFile(fullPath);
                if (bytes is not null)
                {
                    var file = BsiFile.Parse(bytes, spriteName);
                    image = file.Images.Count > 0 ? file.Images[0] : null;
                }
            }
            catch (InvalidDataException)
            {
                // A sprite that will not decode leaves its flat untextured rather than failing the level.
                image = null;
            }
        }

        _images[spriteName] = image;
        return image;
    }
}
