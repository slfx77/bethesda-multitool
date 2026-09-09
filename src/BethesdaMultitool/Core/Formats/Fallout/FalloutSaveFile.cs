using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Fallout;

/// <summary>
///     A Fallout <c>SAVE.DAT</c> header — Fallout 1 and Fallout 2 share it. Original RE
///     2026-09-06 against a retail Fallout 2 SLOT01, extended to Fallout 1 on 2026-09-07.
///     <para>
///         BIG-endian, like every other Fallout container here. <c>+0</c> is the 18-byte magic
///         <c>"FALLOUT SAVE FILE\0"</c>, <c>+0x18</c> a u32 version, <c>+0x1C</c> a release-type
///         character, <c>+0x1D</c> a 32-byte player name, <c>+0x3D</c> a 30-byte save name, then
///         the real-world save DATE as three u16 (day, month, year), an undecoded u32 at
///         <c>+0x61</c>, the IN-GAME date as three u16 (MONTH, DAY, year) at <c>+0x65</c>, a u32
///         clock at <c>+0x6B</c>, an undecoded u32 at <c>+0x6F</c>, and at <c>+0x73</c> a
///         16-byte field holding the NUL-terminated name of the map the player was standing in.
///     </para>
///     <para>
///         ⚑ <b>Three oracles fix the fields, and none could be satisfied by accident.</b> The
///         real-world date agrees with the file's own filesystem modification time — a value from
///         outside the file entirely (Fallout 2: 16/10/2022; Fallout 1: 7/9/2026). The map name is
///         one of the <c>.SAV</c> sidecars sitting beside the save. And the IN-GAME date reads
///         <b>5 December 2161</b> on the Fallout 1 save and <b>25 July 2241</b> on the Fallout 2
///         one — each game's published campaign start date, again from outside the file.
///     </para>
///     <para>
///         ⚠ The in-game date's field ORDER is month-then-day, the opposite of the real-world
///         date's day-then-month, and the Fallout 2 save is what settles it: its two values are 7
///         and 25, and 25 cannot be a month. Reading them the same way round as the real date
///         would give "the 7th of month 25".
///     </para>
///     <para>
///         ⚑ <b>The <c>.SAV</c> sidecars are Fallout MAPs</b> — map version 20 under Fallout 2, 19
///         under Fallout 1 — and their <c>+4</c> name field equals their own file name, the same
///         self-naming rule <see cref="FalloutMapFile" /> already relies on for the 72 shipped
///         maps. So a save's per-map state parses with the SHIPPED map reader and needs no second
///         codec. ⚠⚠ <b>Fallout 2 gzips them and Fallout 1 does not</b>, so
///         <see cref="TryReadSidecarMap" /> routes on the <c>1F 8B</c> magic, never on the game or
///         the extension. ⚠ <c>AUTOMAP.SAV</c> is not a map in either game (it is the automap
///         overlay) and its leading dword is no map version, so it is rejected by content — but it
///         follows the same compression rule as the maps beside it, gzip under Fallout 2 and plain
///         under Fallout 1, so it must be inflated BEFORE anything about it is measured. Its
///         inflated LENGTH turns out to be written into the save body; see
///         <see cref="FalloutSaveBody.TryReadAutomapLength" />.
///     </para>
///     <para>
///         ⚑ <b>The preview thumbnail is a 224 x 133 8-bit image at <c>+0x83</c></b>, drawn
///         through the game's <c>COLOR.PAL</c>. Width 224 is what row autocorrelation picks
///         (0.324 at 224 against 0.311 for the runner-up), and the picture the Fallout 1 save
///         yields is the Vault 13 entrance cave — the map its own header names. The field ends at
///         <c>0x74E3</c> in BOTH games' saves, where a long zero run begins. ⚠ In the Fallout 1
///         save the last 224-byte row is NOT picture (it reads as small signed 16-bit pairs and
///         renders as a colour band) while in the Fallout 2 save it is continuous with the image;
///         one save per game cannot say whether Fallout 1 stores 132 rows plus a 224-byte field or
///         133 rows whose last is left uninitialised, so the field is exposed whole.
///     </para>
///     <para>
///         ⛔ The two undecoded dwords, and what has actually been checked. <c>+0x61</c> reads 14
///         (Fallout 1) and 9 (Fallout 2): nothing proposed. <c>+0x6F</c> reads 35 and 4, and there
///         IS an oracle for the Fallout 2 half — its <c>DATA/MAPS.TXT</c> (151 <c>[Map n]</c>
///         sections, in both <c>master.dat</c> and <c>patch000.dat</c>) gives
///         <c>[Map 004] map_name=arvillag</c>, and that save's 4 sits beside a map field reading
///         <c>ARVILLAG.sav</c>. That is ONE agreeing data point, so the field stays undecoded.
///         ⚠ Fallout 1 cannot second it: its <c>MASTER.DAT</c> ships no <c>MAPS.TXT</c>, and
///         <c>FALLOUTW.EXE</c> holds no map-index table either — "V13ENT" occurs exactly twice in
///         the executable, and the 22-byte city-entrance table that carries one of them has
///         <c>HOTEL.MAP</c> at index 35, not V13ENT. ⚠⚠ The reason once recorded ("Fallout 1 ships
///         no MAPS.TXT, so there is no oracle") was half true and stopped the check being made at
///         all — the CONTROL FIXTURE for this whole track is a Fallout 2 install.
///     </para>
///     <para>
///         ⚠ Only the header and that thumbnail are decoded here. Everything after them is the
///         saved game state, handed back as <see cref="Body" /> rather than guessed at;
///         <see cref="FalloutSaveBody" /> carries the parts of it that an oracle has located.
///     </para>
/// </summary>
internal sealed class FalloutSaveFile
{
    /// <summary>The magic every save opens with, its terminating NUL included.</summary>
    public const string Magic = "FALLOUT SAVE FILE\0";

    /// <summary>Offset of the u32 version.</summary>
    public const int VersionOffset = 0x18;

    /// <summary>Offset of the release-type character.</summary>
    public const int ReleaseTypeOffset = 0x1C;

    /// <summary>Offset of the 32-byte player name.</summary>
    public const int PlayerNameOffset = 0x1D;

    /// <summary>Bytes reserved for the player name.</summary>
    public const int PlayerNameLength = 32;

    /// <summary>Offset of the 30-byte save name.</summary>
    public const int SaveNameOffset = 0x3D;

    /// <summary>Bytes reserved for the save name.</summary>
    public const int SaveNameLength = 30;

    /// <summary>Offset of the u16 save day.</summary>
    public const int DayOffset = 0x5B;

    /// <summary>Offset of the u16 in-game MONTH; the day follows it, then the year.</summary>
    public const int GameMonthOffset = 0x65;

    /// <summary>Offset of the u32 in-game clock.</summary>
    public const int GameTimeOffset = 0x6B;

    /// <summary>Offset of the NUL-terminated current-map name.</summary>
    public const int MapNameOffset = 0x73;

    /// <summary>Bytes reserved for the current-map name.</summary>
    public const int MapNameLength = 16;

    /// <summary>Offset of the preview thumbnail, immediately after the map-name field.</summary>
    public const int PreviewOffset = MapNameOffset + MapNameLength;

    /// <summary>Pixels across the preview thumbnail.</summary>
    public const int PreviewWidth = 224;

    /// <summary>Pixel rows in the preview thumbnail field.</summary>
    public const int PreviewHeight = 133;

    /// <summary>Bytes the preview thumbnail occupies — one palette index per pixel.</summary>
    public const int PreviewLength = PreviewWidth * PreviewHeight;

    /// <summary>The version a retail Fallout 1 save declares.</summary>
    public const uint Fallout1Version = 0x0001_0001;

    /// <summary>The version a retail Fallout 2 save declares.</summary>
    public const uint Fallout2Version = 0x0001_0002;

    private FalloutSaveFile(
        ReadOnlyMemory<byte> bytes, string name, uint version, char releaseType, string playerName,
        string saveName, int day, int month, int year, int gameMonth, int gameDay, int gameYear,
        uint gameTime, string mapName)
    {
        Bytes = bytes;
        Name = name;
        Version = version;
        ReleaseType = releaseType;
        PlayerName = playerName;
        SaveName = saveName;
        Day = day;
        Month = month;
        Year = year;
        GameMonth = gameMonth;
        GameDay = gameDay;
        GameYear = gameYear;
        GameTime = gameTime;
        MapName = mapName;
    }

    /// <summary>The whole file, so the undecoded body can be handed back without a second read.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>
    ///     The declared version: <see cref="Fallout1Version" /> on a retail Fallout 1 save,
    ///     <see cref="Fallout2Version" /> on a Fallout 2 one. The header is otherwise identical,
    ///     so this word is what separates the two games.
    /// </summary>
    public uint Version { get; }

    /// <summary>Release-type character; <c>'R'</c> on the retail save measured.</summary>
    public char ReleaseType { get; }

    /// <summary>The player character's name.</summary>
    public string PlayerName { get; }

    /// <summary>The name the player gave the save.</summary>
    public string SaveName { get; }

    /// <summary>Day of month the save was written.</summary>
    public int Day { get; }

    /// <summary>Month the save was written.</summary>
    public int Month { get; }

    /// <summary>Year the save was written.</summary>
    public int Year { get; }

    /// <summary>In-game month, 1-12. December on the Fallout 1 save, July on the Fallout 2 one.</summary>
    public int GameMonth { get; }

    /// <summary>In-game day of month.</summary>
    public int GameDay { get; }

    /// <summary>In-game year — 2161 on the Fallout 1 save, 2241 on the Fallout 2 one.</summary>
    public int GameYear { get; }

    /// <summary>
    ///     The in-game clock at <see cref="GameTimeOffset" />: <b>264,861</b>
    ///     (<c>00 04 0A 9D</c>) on the Fallout 1 save and <b>315,657</b> (<c>00 04 D1 09</c>) on the
    ///     Fallout 2 one. ⚠ The UNIT is not established. What is measured is that the first
    ///     undecoded records at the end of the Fallout 1 body open at 264,867 — six above this
    ///     clock — and step by exactly 10, so the field counts something finer than a second;
    ///     nothing outside the file pins the rate, so the raw count is what is exposed.
    ///     ⚠⚠ Both numbers were first published 512 (0x200) low, from transcribing <c>0A9D</c> as
    ///     <c>089D</c>; they are re-read off the fixture here. A doc comment IS the record when a
    ///     fixture moves, so a wrong one is worse than none.
    /// </summary>
    public uint GameTime { get; }

    /// <summary>The map the player was in, matching one of the <c>.SAV</c> sidecars.</summary>
    public string MapName { get; }

    /// <summary>The preview thumbnail: <see cref="PreviewLength" /> palette indices, or empty when truncated.</summary>
    public ReadOnlyMemory<byte> Preview =>
        Bytes.Length >= PreviewOffset + PreviewLength
            ? Bytes.Slice(PreviewOffset, PreviewLength)
            : ReadOnlyMemory<byte>.Empty;

    /// <summary>Everything after the thumbnail: the saved game state, undecoded here.</summary>
    public ReadOnlyMemory<byte> Body =>
        Bytes.Length >= PreviewOffset + PreviewLength
            ? Bytes[(PreviewOffset + PreviewLength)..]
            : ReadOnlyMemory<byte>.Empty;

    /// <summary>Content probe: the magic.</summary>
    public static bool IsSaveFile(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= MapNameOffset
               && bytes[..Magic.Length].SequenceEqual(Encoding.ASCII.GetBytes(Magic));
    }

    /// <summary>
    ///     Routes a <c>.SAV</c> sidecar to <see cref="FalloutMapFile" /> BY CONTENT: Fallout 2
    ///     gzips them, Fallout 1 stores them plain, and <c>AUTOMAP.SAV</c> is not a map in either
    ///     game. A sidecar opening with the gzip magic <c>1F 8B</c> is inflated first; everything
    ///     else is handed to the map reader as it stands, which rejects the automap on its
    ///     leading dword.
    /// </summary>
    public static bool TryReadSidecarMap(
        ReadOnlyMemory<byte> sidecar, string name, out FalloutMapFile map, out string error)
    {
        map = null!;
        var span = sidecar.Span;
        if (span.Length >= 2 && span[0] == 0x1F && span[1] == 0x8B)
        {
            byte[] inflated;
            try
            {
                using var source = new MemoryStream(sidecar.ToArray(), false);
                using var gzip = new GZipStream(source, CompressionMode.Decompress);
                using var buffer = new MemoryStream();
                gzip.CopyTo(buffer);
                inflated = buffer.ToArray();
            }
            catch (InvalidDataException gzipError)
            {
                error = $"{name}: opens with the gzip magic but does not inflate ({gzipError.Message}).";
                return false;
            }

            return FalloutMapFile.TryParse(inflated, name, out map, out error);
        }

        return FalloutMapFile.TryParse(sidecar, name, out map, out error);
    }

    /// <summary>Parses the header, throwing <see cref="InvalidDataException" /> when it does not fit.</summary>
    public static FalloutSaveFile Parse(ReadOnlyMemory<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var save, out var error))
        {
            throw new InvalidDataException(error);
        }

        return save;
    }

    /// <summary>Parses the header, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlyMemory<byte> bytes, string name, out FalloutSaveFile save, out string error)
    {
        save = null!;
        var span = bytes.Span;
        if (!IsSaveFile(span))
        {
            error = $"{name}: does not open with the '{Magic.TrimEnd('\0')}' magic.";
            return false;
        }

        var mapName = ReadCString(span, MapNameOffset);
        if (mapName.Length == 0)
        {
            error = $"{name}: carries no current-map name at +0x{MapNameOffset:X2}.";
            return false;
        }

        save = new FalloutSaveFile(
            bytes,
            name,
            BinaryPrimitives.ReadUInt32BigEndian(span[VersionOffset..]),
            (char)span[ReleaseTypeOffset],
            ReadFixed(span, PlayerNameOffset, PlayerNameLength),
            ReadFixed(span, SaveNameOffset, SaveNameLength),
            BinaryPrimitives.ReadUInt16BigEndian(span[DayOffset..]),
            BinaryPrimitives.ReadUInt16BigEndian(span[(DayOffset + 2)..]),
            BinaryPrimitives.ReadUInt16BigEndian(span[(DayOffset + 4)..]),
            BinaryPrimitives.ReadUInt16BigEndian(span[GameMonthOffset..]),
            BinaryPrimitives.ReadUInt16BigEndian(span[(GameMonthOffset + 2)..]),
            BinaryPrimitives.ReadUInt16BigEndian(span[(GameMonthOffset + 4)..]),
            BinaryPrimitives.ReadUInt32BigEndian(span[GameTimeOffset..]),
            mapName);
        error = string.Empty;
        return true;
    }

    /// <summary>
    ///     Reads a fixed-width name field, ending it at the FIRST NUL — the TERMINATED reading.
    ///     <para>
    ///         ⚠⚠
    ///         <b>
    ///             Which of the two a fixed-width field really is (NUL-padded, wanting
    ///             <c>TrimEnd('\0')</c>, or NUL-terminated, wanting the first NUL) has NOT been
    ///             established for these three fields, and this doc comment once asserted "padded" while
    ///             the body below did the terminated thing.
    ///         </b>
    ///         The retail data cannot settle it: in the
    ///         player-name, save-name and map-name fields of BOTH fixtures, every byte after the
    ///         first NUL is zero (measured 2026-09-07 — the Fallout 1 slot's fields terminate at
    ///         3 / 4 / 10 and the Fallout 2 slot's at 9 / 9 / 12, with nothing but zeros after), so
    ///         no authoring leftover exists here to discriminate them, the way <c>CAVES.MAP</c>'s
    ///         does for <see cref="FalloutMapFile" />'s name field.
    ///     </para>
    ///     <para>
    ///         First-NUL is therefore the CONSERVATIVE choice rather than a measured one: it agrees
    ///         with the padded reading on every field whose padding is NUL, and it can never emit
    ///         junk as part of a name. ⚠ It would differ from <c>TrimEnd('\0')</c> only on a field
    ///         carrying bytes after its terminator — which is exactly the observation that would
    ///         settle the question, so if one is ever seen, decide it then instead of assuming.
    ///     </para>
    /// </summary>
    private static string ReadFixed(ReadOnlySpan<byte> bytes, int offset, int length)
    {
        var field = bytes.Slice(offset, length);
        var end = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? field : field[..end]);
    }

    /// <summary>Reads a NUL-terminated string.</summary>
    private static string ReadCString(ReadOnlySpan<byte> bytes, int offset)
    {
        var rest = bytes[offset..];
        var end = rest.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? rest : rest[..end]);
    }
}
