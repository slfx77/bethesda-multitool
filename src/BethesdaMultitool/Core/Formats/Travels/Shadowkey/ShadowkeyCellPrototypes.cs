using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     A Shadowkey (N-Gage) <c>.zcp</c> cell-prototype table: the de-duplicated floor/ceiling
///     geometry a zone's grid cells index. Little-endian, wrapped in the
///     <see cref="ShadowkeyCompressedFile" /> envelope; <see cref="Parse" /> takes the INFLATED
///     payload.
///     <code>
///     +0  u32              record count
///     +4  record[count]    36 bytes each (see ShadowkeyCellPrototype)
///     </code>
///     <para>
///         Measured on all 21 retail zones 2026-09-05: <c>4 + 36 * count</c> equals the inflated
///         length every time, from 4 + 99*36 = 3,568 (raiders) to 4 + 11,828*36 = 425,812 (azra).
///         The table is exactly as large as the zone needs — for every zone the largest
///         <c>.zmp</c> cell index is <c>count - 1</c> AND every record is referenced by at least
///         one cell, so there is neither slack nor an out-of-range index anywhere in the retail
///         data. That pairing is what <see cref="ValidateAgainst" /> checks.
///     </para>
///     <para>
///         The count tracks how much distinct terrain a zone has rather than its size: the flat
///         crypts need a couple of hundred prototypes for 16,384 cells, the outdoor zones with
///         sloped ground need thousands.
///     </para>
/// </summary>
internal sealed class ShadowkeyCellPrototypes
{
    /// <summary>Bytes of count in front of the first record.</summary>
    public const int HeaderLength = 4;

    private readonly ShadowkeyCellPrototype[] _records;

    private ShadowkeyCellPrototypes(string name, ShadowkeyCellPrototype[] records)
    {
        Name = name;
        _records = records;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The prototypes, in table order — the order the grid indexes.</summary>
    public IReadOnlyList<ShadowkeyCellPrototype> Records => _records;

    /// <summary>Number of prototypes in the table.</summary>
    public int Count => _records.Length;

    /// <summary>
    ///     Parses an inflated <c>.zcp</c> payload. Throws <see cref="InvalidDataException" /> naming
    ///     <paramref name="name" /> and the byte position when the count is missing, is larger than
    ///     the payload can hold, or does not tile it exactly.
    /// </summary>
    public static ShadowkeyCellPrototypes Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (bytes.Length < HeaderLength)
        {
            throw new InvalidDataException(
                $"'{name}': the record count ends at byte {HeaderLength}, past the {bytes.Length}-byte payload.");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var expected = HeaderLength + (long)count * ShadowkeyCellPrototype.RecordLength;
        if (expected != bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}': {count} records need {expected} bytes ({HeaderLength} + {count}*{ShadowkeyCellPrototype.RecordLength}) but the payload is {bytes.Length}.");
        }

        var records = new ShadowkeyCellPrototype[count];
        for (var i = 0; i < records.Length; i++)
        {
            var offset = HeaderLength + i * ShadowkeyCellPrototype.RecordLength;
            records[i] = ReadRecord(bytes.Slice(offset, ShadowkeyCellPrototype.RecordLength));
        }

        return new ShadowkeyCellPrototypes(name, records);
    }

    /// <summary>
    ///     Checks that every cell of <paramref name="map" /> indexes a record of this table, and
    ///     throws <see cref="InvalidDataException" /> naming <paramref name="name" /> and the byte
    ///     position of the first offending cell when one does not. Reported separately (not during
    ///     <see cref="Parse" />) because the two files are independent reads.
    /// </summary>
    public void ValidateAgainst(ShadowkeyZoneMap map, string name)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(name);

        for (var i = 0; i < map.Cells.Count; i++)
        {
            var index = map.Cells[i].PrototypeIndex;
            if (index >= _records.Length)
            {
                var offset = ShadowkeyZoneMap.HeaderLength + i * ShadowkeyZoneMap.CellLength;
                throw new InvalidDataException(
                    $"'{name}': cell {i % map.Width},{i / map.Width} at map byte {offset + 4} indexes prototype {index}, but the table holds {_records.Length}.");
            }
        }
    }

    /// <summary>
    ///     Counts how many prototypes no cell of <paramref name="map" /> references. Zero on every
    ///     retail zone — an unreferenced record would mean the table is not the de-duplicated set
    ///     the grid was built from.
    /// </summary>
    public int CountUnreferencedBy(ShadowkeyZoneMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var referenced = new bool[_records.Length];
        foreach (var cell in map.Cells)
        {
            if (cell.PrototypeIndex < referenced.Length)
            {
                referenced[cell.PrototypeIndex] = true;
            }
        }

        var unreferenced = 0;
        foreach (var used in referenced)
        {
            if (!used)
            {
                unreferenced++;
            }
        }

        return unreferenced;
    }

    private static ShadowkeyCellPrototype ReadRecord(ReadOnlySpan<byte> record)
    {
        var floor = new short[ShadowkeyCellPrototype.CornerCount];
        var ceiling = new short[ShadowkeyCellPrototype.CornerCount];
        for (var i = 0; i < ShadowkeyCellPrototype.CornerCount; i++)
        {
            floor[i] = BinaryPrimitives.ReadInt16LittleEndian(record[(6 + i * 2)..]);
            ceiling[i] = BinaryPrimitives.ReadInt16LittleEndian(record[(14 + i * 2)..]);
        }

        return new ShadowkeyCellPrototype(
            (sbyte)record[0],
            record[1],
            BinaryPrimitives.ReadInt16LittleEndian(record[2..]),
            BinaryPrimitives.ReadInt16LittleEndian(record[4..]),
            floor,
            ceiling,
            record.Slice(22, ShadowkeyCellPrototype.SurfaceSlotCount).ToArray(),
            record.Slice(30, ShadowkeyCellPrototype.EdgeByteCount).ToArray(),
            BinaryPrimitives.ReadUInt16LittleEndian(record[34..]));
    }
}
