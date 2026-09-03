// Ported from daggerfall-unity's DaggerfallConnect API (MIT License),
//   https://github.com/Interkarma/daggerfall-unity — Assets/Scripts/API/PakFile.cs. License
//   texts are collected centrally in THIRD_PARTY_LICENSES.

using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>
///     A Daggerfall <c>.PAK</c> world-map overlay — <c>CLIMATE.PAK</c> gives each map pixel its
///     climate index, <c>POLITIC.PAK</c> its region index. A 500-entry table of u32 row offsets
///     opens the file; each row is run-length coded as (u16 count, u8 value) pairs that must sum
///     to exactly 1,001 values.
///     <para>
///         The width is 1,001, one more than WOODS.WLD's 1,000-pixel heightmap: the last column is
///         a repeat the engine uses so a lookup at the map's right edge never falls off it.
///     </para>
/// </summary>
internal sealed class DaggerfallPakFile
{
    /// <summary>Values per row.</summary>
    public const int Width = 1001;

    /// <summary>Rows in the map.</summary>
    public const int Height = 500;

    /// <summary>Bytes in the leading row-offset table.</summary>
    private const int OffsetTableLength = Height * 4;

    private DaggerfallPakFile(string name, byte[] values)
    {
        Name = name;
        Values = values;
    }

    /// <summary>Logical file name this overlay was parsed from.</summary>
    public string Name { get; }

    /// <summary>The decoded overlay, row-major, <see cref="Width" /> x <see cref="Height" />.</summary>
    public byte[] Values { get; }

    /// <summary>The value at a map pixel.</summary>
    public byte this[int x, int y]
    {
        get
        {
            if ((uint)x >= Width || (uint)y >= Height)
            {
                throw new ArgumentOutOfRangeException(nameof(x), $"({x}, {y}) is outside the {Width}x{Height} map.");
            }

            return Values[(y * Width) + x];
        }
    }

    /// <summary>Whether a file name is one of the two overlays.</summary>
    public static bool IsPakFileName(string fileName)
    {
        return fileName.Equals("CLIMATE.PAK", StringComparison.OrdinalIgnoreCase)
               || fileName.Equals("POLITIC.PAK", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses a .PAK overlay.</summary>
    public static DaggerfallPakFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (bytes.Length < OffsetTableLength)
        {
            throw new InvalidDataException(
                $"'{name}' is too small for a PAK row table ({bytes.Length} bytes; {OffsetTableLength} needed).");
        }

        var values = new byte[Width * Height];
        for (var row = 0; row < Height; row++)
        {
            var position = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[(row * 4)..]);
            if (position < 0 || position >= bytes.Length)
            {
                throw new InvalidDataException($"'{name}' row {row} starts outside the file ({position}).");
            }

            var written = 0;
            var rowStart = row * Width;
            while (written < Width)
            {
                if (position + 3 > bytes.Length)
                {
                    throw new InvalidDataException($"'{name}' row {row} runs past end of file.");
                }

                int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[position..]);
                var value = bytes[position + 2];
                position += 3;

                if (count == 0)
                {
                    throw new InvalidDataException($"'{name}' row {row} has a zero-length run.");
                }

                if (written + count > Width)
                {
                    // The reference would write past the row into the next; retail rows always sum
                    // to exactly 1,001, so an overrun is corruption here rather than a feature.
                    throw new InvalidDataException(
                        $"'{name}' row {row} overruns its {Width} values (run of {count} at {written}).");
                }

                values.AsSpan(rowStart + written, count).Fill(value);
                written += count;
            }
        }

        return new DaggerfallPakFile(name, values);
    }
}
