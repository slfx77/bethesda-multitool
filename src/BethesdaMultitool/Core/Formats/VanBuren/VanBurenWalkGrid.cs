using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.VanBuren;

/// <summary>
///     The Van Buren run-length cell grid — the <c>&lt;stem&gt;.rle</c> every <c>EMAP</c> header
///     names beside its <c>8TRE</c> scene. Original RE 2026-09-08.
///     <para>
///         LE: <c>u32 width</c>, <c>u32 height</c>, then runs of <c>u32 count + u8 value</c> until
///         the payload ends. ⚑ <b>THE GATE IS EXACT TILING</b>: the runs must sum to exactly
///         <c>width * height</c> cells AND consume the payload to its last byte — 39 of 39 shipped
///         grids (38 in <c>Maps.grp</c>, one in <c>Engine.grp</c>), and the count is what
///         <c>resource.rht</c> lists under type 1700, so nothing is missed or over-claimed.
///     </para>
///     <para>
///         ⚠ The layout is NOT <c>(u8 value, u32 count)</c>: that reading tiles nothing and lands
///         one full row short on every grid — the first run of every shipped grid is a whole row of
///         the border value, which is what exposed the order.
///     </para>
///     <para>
///         Cells are 0.5 world units square — the paired scene's <c>LVLD INFO</c> declares the
///         same width and height with a 0.5 cell size, and the scene's root bounds span exactly
///         <c>width / 2</c> by <c>height / 2</c> units. Values: <b>47</b> is the border and every
///         wall (it outweighs the open cells on 14 of the 39 grids); the open interior carries a single small
///         value per map (0, 1, 2, 5 or 9) with a few others in bands of eleven (0/11/22/33/44,
///         1/12/23/34, 2/24/35, 4/26, 5/27/38, 9/31/42). ⚠ What the bands MEAN is not
///         established — they read as a surface kind and a level band, but that is a reading, not a
///         measurement — so a rendering must colour them diagnostically.
///     </para>
///     <para>
///         ⚠ There is NO magic: the probe is the tiling itself. Two other untagged payload
///         families share the <c>.grp</c> archives (36 in <c>Critters</c>, 83 in <c>Items</c>) and
///         are refused by it.
///     </para>
/// </summary>
internal sealed class VanBurenWalkGrid
{
    /// <summary>Two dwords: width and height.</summary>
    public const int HeaderLength = 8;

    /// <summary>A dword count then a byte value.</summary>
    public const int RunLength = 5;

    /// <summary>The value every border cell and wall carries.</summary>
    public const byte Blocked = 47;

    /// <summary>The largest edge accepted; the biggest shipped grid is 704 cells.</summary>
    public const int MaxEdge = 8192;

    private VanBurenWalkGrid(string name, int width, int height, byte[] cells, int runCount)
    {
        Name = name;
        Width = width;
        Height = height;
        Cells = cells;
        RunCount = runCount;
    }

    /// <summary>Source name, for messages.</summary>
    public string Name { get; }

    /// <summary>Cells across.</summary>
    public int Width { get; }

    /// <summary>Cells down.</summary>
    public int Height { get; }

    /// <summary>Row-major cell values, the first run filling the first row.</summary>
    public byte[] Cells { get; }

    /// <summary>How many runs encoded the grid.</summary>
    public int RunCount { get; }

    /// <summary>The value at a cell.</summary>
    public byte At(int x, int y)
    {
        return Cells[y * Width + x];
    }

    /// <summary>Content probe: there is no magic, so the probe is the full tiling walk.</summary>
    public static bool IsWalkGrid(ReadOnlySpan<byte> bytes)
    {
        return TryParse(bytes, "probe", out _, out _);
    }

    /// <summary>Parses a grid, throwing <see cref="InvalidDataException" /> when it does not tile.</summary>
    public static VanBurenWalkGrid Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var grid, out var error))
        {
            throw new InvalidDataException(error);
        }

        return grid;
    }

    /// <summary>Parses a grid, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, string name, out VanBurenWalkGrid grid, out string error)
    {
        grid = null!;
        if (bytes.Length < HeaderLength + RunLength)
        {
            error = $"{name}: {bytes.Length} bytes is shorter than a header and one run.";
            return false;
        }

        var width = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var height = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (width is 0 or > MaxEdge || height is 0 or > MaxEdge)
        {
            error = $"{name}: {width}x{height} is not a plausible grid.";
            return false;
        }

        // A run stream that does not divide evenly cannot tile, so refuse before allocating.
        if ((bytes.Length - HeaderLength) % RunLength != 0)
        {
            error =
                $"{name}: {bytes.Length - HeaderLength} bytes after the header is not a whole number of {RunLength}-byte runs.";
            return false;
        }

        var total = (int)(width * height);
        var cells = new byte[total];
        var filled = 0;
        var runs = 0;
        for (var at = HeaderLength; at < bytes.Length; at += RunLength)
        {
            var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
            var value = bytes[at + 4];
            if (count == 0 || filled + count > total)
            {
                error = $"{name}: run {runs} of {count} cells overflows the {width}x{height} grid at cell {filled}.";
                return false;
            }

            cells.AsSpan(filled, (int)count).Fill(value);
            filled += (int)count;
            runs++;
        }

        // ⚑ THE GATE: the runs must fill the grid exactly. A wrong run layout lands short here.
        if (filled != total)
        {
            error = $"{name}: the runs fill {filled} of {total} cells.";
            return false;
        }

        grid = new VanBurenWalkGrid(name, (int)width, (int)height, cells, runs);
        error = string.Empty;
        return true;
    }
}
