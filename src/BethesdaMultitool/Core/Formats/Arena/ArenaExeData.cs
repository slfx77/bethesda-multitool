using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Arena;

/// <summary>One province as the EXECUTABLE stores it — distinct from CITYDATA's <c>ArenaProvince</c>.</summary>
internal sealed record ArenaExeProvince(int Index, int X, int Y, int Width, int Height, string Name)
{
    /// <summary>Right edge of the province's rectangle on the world map.</summary>
    public int Right => X + Width;

    /// <summary>Bottom edge of the province's rectangle on the world map.</summary>
    public int Bottom => Y + Height;
}

/// <summary>
///     Tables hardcoded inside Arena's <c>A.EXE</c>, read from the PKLITE-unpacked image that
///     <c>classic exe</c> produces (174,021 packed → 304,624 bytes).
///     <para>
///         Arena keeps a great deal of its world in the executable rather than in data files, so
///         these tables are the only source for several record types. This class grows a table at a
///         time; each is located by a CONTENT ANCHOR and then validated, never by a bare hardcoded
///         offset, so a differently-built executable either matches or is rejected rather than
///         producing plausible nonsense from the wrong address.
///     </para>
/// </summary>
internal static class ArenaExeData
{
    /// <summary>Provinces in the game, including the Imperial Province.</summary>
    public const int ProvinceCount = 9;

    /// <summary>Bytes per province record: four u16 fields then a NUL-padded name.</summary>
    public const int ProvinceRecordLength = 0x62;

    /// <summary>Bytes of numeric fields ahead of the name inside a province record.</summary>
    public const int ProvinceNameOffset = 8;

    /// <summary>Width of Arena's world map in pixels; every province rectangle fits inside it.</summary>
    public const int WorldMapWidth = 320;

    /// <summary>Height of Arena's world map in pixels.</summary>
    public const int WorldMapHeight = 200;

    /// <summary>The first province's name, used to anchor the table.</summary>
    private const string FirstProvinceName = "High Rock";

    /// <summary>
    ///     Reads the province table, or returns null when this image does not contain a recognisable
    ///     one.
    ///     <para>
    ///         Measured on the retail unpacked image 2026-09-06: nine records of 98 bytes beginning
    ///         at 0x0392F0, each <c>u16 x, u16 y, u16 width, u16 height</c> then a NUL-terminated
    ///         name padded to 90 bytes. The fields are a RECTANGLE, not a bounding box —
    ///         <c>x &lt; right</c> holds only because width is added, and reading them as
    ///         (left, top, right, bottom) puts Summerset Isle at a negative size.
    ///     </para>
    ///     <para>
    ///         The reading is confirmed geographically, not merely arithmetically: High Rock lands
    ///         northwest, Skyrim north-centre, Morrowind northeast, Summerset southwest, Black Marsh
    ///         southeast and the Imperial Province centre — which is Tamriel.
    ///     </para>
    ///     <para>
    ///         ⚠⚠ <b>This is a SECOND province table and it does not agree with CITYDATA.</b>
    ///         <c>ArenaCityDataFile</c> reads its own name + rectangle per province, and all nine
    ///         rectangles differ — High Rock is (37,32,86,57) here against (41,26,83,61) there,
    ///         Morrowind (190,31,102,93) against (198,28,81,87) — and even the spelling differs
    ///         (<c>Summerset Isle</c> here, <c>Summurset Isle</c> in CITYDATA). They are close
    ///         enough to be the same intent and far enough apart to be independently authored.
    ///         ⛔ Which one the game actually draws with is NOT established, so neither is
    ///         presented as correct and nothing here overrides the CITYDATA-derived APRV records.
    ///         A consumer picking a rectangle must know there are two.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<ArenaExeProvince>? TryReadProvinces(ReadOnlySpan<byte> unpackedExe)
    {
        var anchor = FindAnchor(unpackedExe, FirstProvinceName);
        if (anchor < ProvinceNameOffset)
        {
            return null;
        }

        var start = anchor - ProvinceNameOffset;
        if (start + (ProvinceCount * ProvinceRecordLength) > unpackedExe.Length)
        {
            return null;
        }

        var provinces = new List<ArenaExeProvince>(ProvinceCount);
        for (var i = 0; i < ProvinceCount; i++)
        {
            var record = unpackedExe.Slice(start + (i * ProvinceRecordLength), ProvinceRecordLength);
            var x = BinaryPrimitives.ReadUInt16LittleEndian(record);
            var y = BinaryPrimitives.ReadUInt16LittleEndian(record[2..]);
            var width = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            var height = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
            var name = ReadName(record[ProvinceNameOffset..]);

            // Every record must be a named rectangle inside the world map. A table found at the
            // wrong address fails this immediately rather than yielding nine plausible provinces.
            if (name.Length == 0 || width == 0 || height == 0
                || x + width > WorldMapWidth || y + height > WorldMapHeight)
            {
                return null;
            }

            provinces.Add(new ArenaExeProvince(i, x, y, width, height, name));
        }

        return provinces;
    }

    private static string ReadName(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        var text = end < 0 ? field : field[..end];
        return Encoding.Latin1.GetString(text).Trim();
    }

    private static int FindAnchor(ReadOnlySpan<byte> haystack, string needle)
    {
        Span<byte> pattern = stackalloc byte[needle.Length];
        Encoding.ASCII.GetBytes(needle, pattern);
        return haystack.IndexOf(pattern);
    }
}
