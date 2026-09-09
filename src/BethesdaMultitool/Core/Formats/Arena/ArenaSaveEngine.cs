namespace BethesdaMultitool.Core.Formats.Arena;

/// <summary>
///     An Arena <c>SAVEENGN.NN</c> file (17,983 bytes): a 3,664-byte head followed by an array of
///     1,054-byte records, then a 617-byte partial region that no whole record fits.
///     <para>
///         What is established (measured 2026-09-06 on retail SAVEENGN.00/.01/.02/.64): the record
///         stride is exactly 1,054 and the array base is 3,664. Proof is an oracle, not a
///         statistic — every populated record repeats an 8-byte block at <c>+40 == +48</c>, and
///         scanning EVERY byte offset of the file for a non-zero block satisfying that condition
///         yields exactly six hits, 3,664 + k x 1,054 for k = 0..5, identically in all four files
///         and with zero false positives elsewhere (the same scan on SAVEGAME fires tens of
///         thousands of times with no 1,054 spacing).
///         1,054 does not divide 17,983 (17 x 1,054 + 65 = 17,983), and that phasing is refuted, so
///         the base is 3,664: 13 whole records fit, ending at 17,366, leaving 617 bytes.
///     </para>
///     <para>
///         What is NOT established, and deliberately not modelled: what the head is (it is not
///         known to be a bitmap or a table), what a record represents, any record count field
///         (none was found), and the meaning of any record byte. The head, each record and the
///         tail are exposed as raw bytes; the only interpretation offered is the populated-record
///         oracle above.
///     </para>
/// </summary>
internal sealed class ArenaSaveEngine
{
    /// <summary>Exact length of every retail SAVEENGN.NN.</summary>
    public const int FileLength = 17_983;

    /// <summary>Bytes before the first record.</summary>
    public const int HeadLength = 3_664;

    /// <summary>Bytes from one record to the next.</summary>
    public const int RecordStride = 1_054;

    /// <summary>Whole records between the head and the end of the file: (17,983 - 3,664) / 1,054.</summary>
    public const int RecordSlotCount = 13;

    /// <summary>Bytes after the last whole record: 17,983 - 3,664 - 13 x 1,054.</summary>
    public const int TailLength = 617;

    /// <summary>Record offset of the first copy of the doubled 8-byte block.</summary>
    public const int OracleFirstOffset = 40;

    /// <summary>Record offset of the second copy of the doubled 8-byte block.</summary>
    public const int OracleSecondOffset = 48;

    /// <summary>Length of the doubled block.</summary>
    public const int OracleBlockLength = 8;

    private ArenaSaveEngine(string name, byte[] head, IReadOnlyList<ArenaSaveEngineRecord> records, byte[] tail)
    {
        Name = name;
        Head = head;
        Records = records;
        Tail = tail;
    }

    /// <summary>Logical file name this file was parsed from.</summary>
    public string Name { get; }

    /// <summary>The 3,664 bytes before the record array, raw and uninterpreted.</summary>
    public byte[] Head { get; }

    /// <summary>The 13 record slots, in file order, each carrying its raw bytes.</summary>
    public IReadOnlyList<ArenaSaveEngineRecord> Records { get; }

    /// <summary>The 617 bytes after the last whole record, raw. All zero in the retail files.</summary>
    public byte[] Tail { get; }

    /// <summary>File offsets of the slots the oracle marks populated, ascending.</summary>
    public IEnumerable<int> PopulatedRecordOffsets =>
        Records.Where(r => r.IsPopulated).Select(r => r.Offset);

    /// <summary>
    ///     Probes for a SAVEENGN.NN. The only gate is the exact length — no magic, no field with a
    ///     known value — so any 17,983-byte file passes; callers pair it with the file name.
    /// </summary>
    public static bool IsSaveEngine(ReadOnlySpan<byte> file)
    {
        return file.Length == FileLength;
    }

    /// <summary>Parses a SAVEENGN.NN. The length must be exactly <see cref="FileLength" />.</summary>
    public static ArenaSaveEngine Parse(ReadOnlySpan<byte> file, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (file.Length != FileLength)
        {
            throw new InvalidDataException(
                $"'{name}' is {file.Length} bytes; an Arena SAVEENGN is exactly {FileLength}.");
        }

        var records = new List<ArenaSaveEngineRecord>(RecordSlotCount);
        for (var k = 0; k < RecordSlotCount; k++)
        {
            var offset = HeadLength + k * RecordStride;
            var bytes = file.Slice(offset, RecordStride).ToArray();
            records.Add(new ArenaSaveEngineRecord(k, offset, bytes, IsPopulatedRecord(bytes)));
        }

        return new ArenaSaveEngine(
            name,
            file[..HeadLength].ToArray(),
            records,
            file.Slice(HeadLength + RecordSlotCount * RecordStride, TailLength).ToArray());
    }

    /// <summary>
    ///     The populated-record oracle: the 8 bytes at +40 equal the 8 bytes at +48 and are not
    ///     all zero. The non-zero condition is part of the oracle — without it a zero-filled slot
    ///     (and every zero run in the file) satisfies the equality trivially.
    /// </summary>
    public static bool IsPopulatedRecord(ReadOnlySpan<byte> record)
    {
        if (record.Length < OracleSecondOffset + OracleBlockLength)
        {
            return false;
        }

        var first = record.Slice(OracleFirstOffset, OracleBlockLength);
        var second = record.Slice(OracleSecondOffset, OracleBlockLength);
        return first.SequenceEqual(second) && first.ContainsAnyExcept((byte)0);
    }

    /// <summary>
    ///     Runs <see cref="IsPopulatedRecord" /> at EVERY byte offset of <paramref name="file" />
    ///     and returns the offsets that satisfy it. This is the measurement that fixed the base
    ///     and stride: on the retail files it returns exactly 3,664 + k x 1,054 for k = 0..5.
    /// </summary>
    public static IReadOnlyList<int> FindOracleHits(ReadOnlySpan<byte> file)
    {
        var hits = new List<int>();
        var last = file.Length - (OracleSecondOffset + OracleBlockLength);
        for (var offset = 0; offset <= last; offset++)
        {
            if (IsPopulatedRecord(file[offset..]))
            {
                hits.Add(offset);
            }
        }

        return hits;
    }
}

/// <summary>One 1,054-byte SAVEENGN record slot, raw.</summary>
/// <param name="Index">Slot number, 0-based.</param>
/// <param name="Offset">File offset of the slot's first byte.</param>
/// <param name="Bytes">The slot's 1,054 bytes, uninterpreted.</param>
/// <param name="IsPopulated">Whether <see cref="ArenaSaveEngine.IsPopulatedRecord" /> holds for the slot.</param>
internal sealed record ArenaSaveEngineRecord(int Index, int Offset, byte[] Bytes, bool IsPopulated)
{
    /// <summary>The doubled 8-byte block at +40 (equal to the copy at +48 when populated).</summary>
    public ReadOnlySpan<byte> OracleBlock =>
        Bytes.AsSpan(ArenaSaveEngine.OracleFirstOffset, ArenaSaveEngine.OracleBlockLength);
}
