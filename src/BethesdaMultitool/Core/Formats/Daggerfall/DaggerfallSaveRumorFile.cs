// The 34-byte header's field list is ported from daggerfall-unity's RumorFile (MIT License),
//   https://github.com/Interkarma/daggerfall-unity — Assets/Scripts/API/RumorFile.cs.
//   License texts are collected centrally in THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>
///     One rumour: a 34-byte header and <see cref="TextLength" /> bytes of text (the NUL terminator
///     is INSIDE that length; 0xFD is the game's line break).
/// </summary>
internal sealed class DaggerfallSaveRumor
{
    /// <summary>Bytes before the text.</summary>
    public const int HeaderLength = 34;

    /// <summary>Bytes reserved for the quest stem.</summary>
    public const int QuestNameLength = 9;

    /// <summary>The byte the game renders as a line break.</summary>
    public const byte LineBreak = 0xFD;

    /// <summary>Position in the file.</summary>
    public required int Index { get; init; }

    /// <summary>File offset of the header.</summary>
    public required int Offset { get; init; }

    /// <summary>The faction the rumour is about — a FACTION.TXT id (208 = Northmoor on SAVE0).</summary>
    public required ushort Faction1 { get; init; }

    /// <summary>A second faction; 0 on every SAVE0 rumour.</summary>
    public required ushort Faction2 { get; init; }

    /// <summary>
    ///     12 new ruler, 4 plague, 7 famine, 10 witch burnings, 11 crime wave, 18 persecuted temple, 100 faction; 0 on a
    ///     quest rumour.
    /// </summary>
    public required uint RumorType { get; init; }

    /// <summary>RegionNames index (32 = Northmoor on SAVE0's first rumour).</summary>
    public required byte RegionId { get; init; }

    /// <summary>8 on the region rumours, 4 on two of the three quest rumours; meaning open.</summary>
    public required byte Flags { get; init; }

    public required byte QuestId { get; init; }

    /// <summary>The quest's file stem ("_BRISIEN", "_TUTOR__"), empty on a region rumour.</summary>
    public required string QuestName { get; init; }

    /// <summary>The quest's QRC message id (1005 / 1006 on SAVE0); 0 on a region rumour.</summary>
    public required ushort QuestMessageId { get; init; }

    public required uint NpcId { get; init; }

    /// <summary>Bytes of text, NUL terminator included.</summary>
    public required uint TextLength { get; init; }

    /// <summary>Game minute the rumour stops circulating.</summary>
    public required uint TimeLimit { get; init; }

    /// <summary>The raw text bytes, terminator and 0xFD line breaks included.</summary>
    public required ReadOnlyMemory<byte> TextBytes { get; init; }

    /// <summary>The text up to its NUL, with 0xFD rendered as a newline.</summary>
    public string Text
    {
        get
        {
            var span = TextBytes.Span;
            var end = span.IndexOf((byte)0);
            var body = end < 0 ? span : span[..end];
            return Encoding.Latin1.GetString(body).Replace((char)LineBreak, '\n');
        }
    }
}

/// <summary>
///     A Daggerfall save's <c>RUMOR.DAT</c>: the rumours the taverns are currently telling. ALL
///     LITTLE-ENDIAN, and a pure walk — the file states no count, so the record stream IS the file.
///     <para>
///         ⚑ EXACT TILING on the 1,481-byte retail SAVE0 (2026-09-07): 12 records of
///         34 + textLength (120, 112, 38, 115, 109, 44, 93, 111, 58, 65, 106, 102) land on EOF.
///         A record whose text overruns, or a file with a partial header left over, is refused —
///         which is what would have falsified the 34-byte header.
///     </para>
///     <para>
///         ⚑ Oracles from OUTSIDE the file: the nine region rumours pair a FACTION.TXT ruler-court
///         id (208, 214, 218, 220, 221, 224, 232, 233, 502) with the SAME-named RegionNames index
///         (32, 38, 42, 44, 45, 48, 56, 57, 16) 9/9, and their type 12 texts read as prose ("The
///         noble ruler, Lolelle R'on was found murdered in his bed."); the three quest rumours name
///         retail quest stems and carry QRC message ids that exist in those quests' .QRC files,
///         with the macros already expanded.
///     </para>
/// </summary>
internal sealed class DaggerfallSaveRumorFile
{
    /// <summary>The only file this parser reads.</summary>
    public const string FileName = "RUMOR.DAT";

    private DaggerfallSaveRumorFile(string name, int length, IReadOnlyList<DaggerfallSaveRumor> rumors)
    {
        Name = name;
        Length = length;
        Rumors = rumors;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>Physical file length; the walk consumed exactly this many bytes.</summary>
    public int Length { get; }

    /// <summary>Every rumour in file order.</summary>
    public IReadOnlyList<DaggerfallSaveRumor> Rumors { get; }

    /// <summary>Loads and parses a file.</summary>
    public static DaggerfallSaveRumorFile Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Parse(File.ReadAllBytes(path), Path.GetFileName(path));
    }

    /// <summary>Parses a complete image; throws <see cref="InvalidDataException" /> unless it tiles.</summary>
    public static DaggerfallSaveRumorFile Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);

        var span = bytes.AsSpan();
        var memory = new ReadOnlyMemory<byte>(bytes);
        var rumors = new List<DaggerfallSaveRumor>();
        var position = 0;
        while (position < bytes.Length)
        {
            if (position + DaggerfallSaveRumor.HeaderLength > bytes.Length)
            {
                throw new InvalidDataException(
                    $"{name}: {bytes.Length - position} bytes at {position} cannot hold a "
                    + $"{DaggerfallSaveRumor.HeaderLength}-byte rumour header.");
            }

            var header = span.Slice(position, DaggerfallSaveRumor.HeaderLength);
            var textLength = BinaryPrimitives.ReadUInt32LittleEndian(header[26..]);
            if (textLength > int.MaxValue
                || position + DaggerfallSaveRumor.HeaderLength + textLength > bytes.Length)
            {
                throw new InvalidDataException(
                    $"{name}: rumour {rumors.Count} at {position} declares {textLength} text bytes, "
                    + $"which overruns the {bytes.Length}-byte file.");
            }

            var nameEnd = header.Slice(11, DaggerfallSaveRumor.QuestNameLength).IndexOf((byte)0);
            var questName = Encoding.Latin1.GetString(
                header.Slice(11, nameEnd < 0 ? DaggerfallSaveRumor.QuestNameLength : nameEnd));

            rumors.Add(new DaggerfallSaveRumor
            {
                Index = rumors.Count,
                Offset = position,
                Faction1 = BinaryPrimitives.ReadUInt16LittleEndian(header),
                Faction2 = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]),
                RumorType = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]),
                RegionId = header[8],
                Flags = header[9],
                QuestId = header[10],
                QuestName = questName,
                QuestMessageId = BinaryPrimitives.ReadUInt16LittleEndian(header[20..]),
                NpcId = BinaryPrimitives.ReadUInt32LittleEndian(header[22..]),
                TextLength = textLength,
                TimeLimit = BinaryPrimitives.ReadUInt32LittleEndian(header[30..]),
                TextBytes = memory.Slice(position + DaggerfallSaveRumor.HeaderLength, (int)textLength)
            });

            position += DaggerfallSaveRumor.HeaderLength + (int)textLength;
        }

        return new DaggerfallSaveRumorFile(name, bytes.Length, rumors);
    }
}
