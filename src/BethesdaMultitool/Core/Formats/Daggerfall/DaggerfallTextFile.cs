// Ported from daggerfall-unity's DaggerfallConnect API (MIT License),
//   https://github.com/Interkarma/daggerfall-unity — Assets/Scripts/API/TextFile.cs. License
//   texts are collected centrally in THIRD_PARTY_LICENSES.

using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>
///     Daggerfall's string table, <c>TEXT.RSC</c>: a u16 header length, then
///     <c>headerLength / 6 - 1</c> entries of (u16 record id, u32 absolute offset) followed by a
///     terminator entry (id 0xFFFF, offset = file length) the reference never reads. Each record
///     runs from its offset to the first end-of-record byte (0xFE) and may hold several
///     subrecords (alternative phrasings the game picks between) separated by 0xFF.
///     <para>
///         Measured on retail (2026-09-03): 1,408 records with unique ids 0-9999, no record embeds
///         an end-of-record byte, and six records are SHARED — 12 ids point at another id's offset
///         (e.g. 1200/1201/1202), so a table of 1,408 entries names 1,396 distinct records.
///     </para>
/// </summary>
internal sealed class DaggerfallTextFile
{
    /// <summary>The only file this parser reads.</summary>
    public const string FileName = "TEXT.RSC";

    private const int EntryLength = 6;

    private readonly Dictionary<int, DaggerfallTextRecord> _byId;

    private DaggerfallTextFile(IReadOnlyList<DaggerfallTextRecord> records)
    {
        Records = records;
        _byId = new Dictionary<int, DaggerfallTextRecord>(records.Count);
        foreach (var record in records)
        {
            _byId.TryAdd(record.Id, record);
        }
    }

    /// <summary>Records in table order.</summary>
    public IReadOnlyList<DaggerfallTextRecord> Records { get; }

    /// <summary>The record with a given id, or null.</summary>
    public DaggerfallTextRecord? FindById(int id)
    {
        return _byId.GetValueOrDefault(id);
    }

    /// <summary>Parses a complete TEXT.RSC image.</summary>
    public static DaggerfallTextFile Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length < 2)
        {
            throw new InvalidDataException("TEXT.RSC is shorter than its 2-byte header length.");
        }

        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        if (headerLength < EntryLength || headerLength % EntryLength != 0)
        {
            throw new InvalidDataException(
                $"TEXT.RSC header length {headerLength} is not a whole number of 6-byte entries.");
        }

        if (bytes.Length < 2 + headerLength)
        {
            throw new InvalidDataException(
                $"TEXT.RSC declares a {headerLength}-byte table but is only {bytes.Length} bytes.");
        }

        var count = headerLength / EntryLength - 1;
        var records = new DaggerfallTextRecord[count];
        var memory = new ReadOnlyMemory<byte>(bytes);
        for (var i = 0; i < count; i++)
        {
            var entry = bytes.AsSpan(2 + i * EntryLength, EntryLength);
            var id = BinaryPrimitives.ReadUInt16LittleEndian(entry);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[2..]);
            if (offset >= (uint)bytes.Length)
            {
                throw new InvalidDataException(
                    $"TEXT.RSC record {id} starts at {offset}, past the {bytes.Length}-byte file.");
            }

            var end = Array.IndexOf(bytes, DaggerfallTextTokens.EndOfRecord, (int)offset);
            if (end < 0)
            {
                throw new InvalidDataException($"TEXT.RSC record {id} at {offset} has no end-of-record byte.");
            }

            var raw = memory[(int)offset..end];
            var subrecords = DaggerfallTextTokens.SplitSubrecords(raw)
                .Select(s => DaggerfallTextTokens.RenderPlain(s.Span))
                .ToList();
            records[i] = new DaggerfallTextRecord(id, (int)offset, raw, subrecords);
        }

        return new DaggerfallTextFile(records);
    }
}

/// <summary>One TEXT.RSC record: its id, raw bytes (terminator excluded) and rendered subrecords.</summary>
internal sealed class DaggerfallTextRecord
{
    public DaggerfallTextRecord(int id, int offset, ReadOnlyMemory<byte> raw, IReadOnlyList<string> subrecords)
    {
        Id = id;
        Offset = offset;
        Raw = raw;
        Subrecords = subrecords;
    }

    /// <summary>Authored record id (0-9999 on retail).</summary>
    public int Id { get; }

    /// <summary>Absolute file offset of the first byte.</summary>
    public int Offset { get; }

    /// <summary>The record's bytes up to, not including, the end-of-record byte.</summary>
    public ReadOnlyMemory<byte> Raw { get; }

    /// <summary>Plain-text renderings of each subrecord, in order (at least one).</summary>
    public IReadOnlyList<string> Subrecords { get; }

    /// <summary>The first subrecord's text.</summary>
    public string Text => Subrecords[0];
}
