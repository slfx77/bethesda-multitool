using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Fallout;

/// <summary>
///     The art a map render needs — <c>COLOR.PAL</c>, the per-type <c>ART\*\*.LST</c> name lists and the
///     <c>.FRM</c> sprites they name — resolved through one file reader and cached. The reader takes a
///     data-root-relative path (<c>ART/TILES/TILES.LST</c>) and returns null for a file that is not there,
///     so a layered install mount, a lone <c>MASTER.DAT</c> and a loose tree all serve.
///     <para>
///         ⚠ Critter art lives in <c>CRITTER.DAT</c>, not <c>MASTER.DAT</c>; a reader over the master
///         alone resolves every tile, wall and scenery sprite and no critter. That is reported, not hidden:
///         <see cref="Missing" /> lists what was asked for and not found.
///     </para>
/// </summary>
internal sealed class FalloutMapArt
{
    private readonly Func<string, byte[]?> _read;
    private readonly Dictionary<int, FalloutProList?> _lists = new();
    private readonly Dictionary<string, FalloutFrmFile?> _sprites = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, FalloutFrmFile?> _byFid = new();

    /// <summary>Wraps a reader; the palette is loaded up front because nothing draws without it.</summary>
    /// <exception cref="InvalidDataException">When <c>COLOR.PAL</c> is not readable through <paramref name="read" />.</exception>
    public FalloutMapArt(Func<string, byte[]?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        _read = read;
        var paletteBytes = read(FalloutPalette.FileName)
                           ?? throw new InvalidDataException($"{FalloutPalette.FileName} is not reachable, so no tile can be coloured.");
        Palette = FalloutPalette.Parse(paletteBytes, FalloutPalette.FileName);
    }

    /// <summary>The game palette, index 0 transparent.</summary>
    public Palette Palette { get; }

    /// <summary>Every art file asked for and not found, in the order first missed.</summary>
    public IReadOnlyCollection<string> Missing => _missing;

    /// <summary>The floor or roof tile at a map word's art index, or null when the list or file is absent.</summary>
    public FalloutFrmFile? Tile(int artIndex)
    {
        var list = List(FalloutArtId.TileType);
        if (list is null || artIndex < 0 || artIndex >= list.Count)
        {
            return null;
        }

        return Sprite($"ART/TILES/{list.Names[artIndex]}");
    }

    /// <summary>The sprite an art id names, trying the file names <see cref="FalloutArtId.FileNamesFor" /> gives.</summary>
    public FalloutFrmFile? Sprite(uint fid)
    {
        if (_byFid.TryGetValue(fid, out var cached))
        {
            return cached;
        }

        FalloutFrmFile? found = null;
        var type = FalloutArtId.Type(fid);
        var list = List(type);
        var directory = FalloutArtId.DirectoryFor(type);
        var index = FalloutArtId.Index(fid);
        if (list is not null && directory is not null && index < list.Count)
        {
            foreach (var candidate in FalloutArtId.FileNamesFor(fid, list.Names[index]))
            {
                found = Sprite($"ART/{directory}/{candidate}");
                if (found is not null)
                {
                    break;
                }
            }
        }

        _byFid[fid] = found;
        return found;
    }

    /// <summary>One type's name list, read once.</summary>
    public FalloutProList? List(int type)
    {
        if (_lists.TryGetValue(type, out var list))
        {
            return list;
        }

        var path = FalloutArtId.ListPathFor(type);
        list = path is not null && _read(path) is { } bytes ? FalloutProList.Parse(bytes, path) : null;
        if (list is null && path is not null)
        {
            _missing.Add(path);
        }

        _lists[type] = list;
        return list;
    }

    private FalloutFrmFile? Sprite(string path)
    {
        if (_sprites.TryGetValue(path, out var sprite))
        {
            return sprite;
        }

        var bytes = _read(path);
        sprite = null;
        if (bytes is not null)
        {
            try
            {
                sprite = FalloutFrmFile.Parse(bytes, path);
            }
            catch (InvalidDataException)
            {
                sprite = null;
            }
        }

        if (sprite is null)
        {
            _missing.Add(path);
        }

        _sprites[path] = sprite;
        return sprite;
    }
}
