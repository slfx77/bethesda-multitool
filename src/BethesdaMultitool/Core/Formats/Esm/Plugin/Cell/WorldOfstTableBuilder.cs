using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;

/// <summary>
///     Builds the per-file <c>OFST</c> cell-offset table that every WRLD record in a plugin
///     must carry.
///     <para>
///     OFST is a <c>columns × rows</c> array of uint32, indexed <c>row * columns + col</c>,
///     where each entry is the byte offset of that grid cell's CELL record <b>relative to the
///     start of the WRLD record</b>, or 0 when the file contributes no cell at that coordinate.
///     Because the offsets are WRLD-relative they can be computed entirely inside the cell
///     section stream — no post-assembly fixup is needed.
///     </para>
///     <para>
///     Emitting the table is not optional. Every one of the 63 WRLD records across the shipped
///     FNV and FO3 DLC plugins carries an OFST — 23 master-worldspace overrides and 40 of the
///     plugins' own new worldspaces, with no exceptions. A file that is ESM-flagged but has no
///     OFST is a combination that occurs in no shipped plugin, and it pushes the engine off the
///     fast cell-seek path onto a GRUP scan of the file's world-children group. Where that group
///     holds only a persistent-container CELL override (flag 0x400) the scan can serve the
///     container into an exterior grid slot, and <c>TESObjectCELL::GetLandRecord</c> returns NULL
///     for persistent cells — which <c>GridCellArray::LoadCell</c> passes to
///     <c>TESObjectLAND::Load</c> unchecked. That is the Freeside "Wilderness cell Attaching"
///     access violation. <c>DeadMoney.esm</c> is the control: it makes byte-for-byte the same
///     WastelandNV emission we do — ESM-flagged, master WRLD override, world-children group
///     holding just the persistent container at XCLC (0,0) colliding with a real master grid
///     cell — and ships an all-zero 44,278-entry OFST, and does not crash.
///     </para>
///     <para>
///     Copying the master's OFST payload verbatim is equally wrong and was tried first: the
///     offsets address the file the record was READ from, so the engine seeks THIS plugin at the
///     master's offsets and every exterior cell fails with "CELLS: Failed to load temporary
///     data". The three states are distinct — absent (crashes), master's bytes (fails to load),
///     and rebuilt for this file (correct). The DLCs confirm the third: their OFST subrecords
///     are the same LENGTH as the master's for the same worldspace but differ in content, and
///     hold a non-zero entry only where that plugin actually contributes a cell.
///     </para>
/// </summary>
internal static class WorldOfstTableBuilder
{
    /// <summary>World units per exterior cell along one axis.</summary>
    private const float CellSize = 4096f;

    /// <summary>NAM0/NAM9 sentinel magnitude used by the GECK for "bounds never set".</summary>
    private const float UnsetFloatThreshold = 1e20f;

    /// <summary>
    ///     Refuse to allocate a table larger than this. The largest real worldspace is
    ///     WastelandNV at 131 × 338 = 44,278 entries; this cap only guards against a corrupt
    ///     NAM0/NAM9 pair asking for gigabytes.
    /// </summary>
    private const int MaxEntries = 1_000_000;

    /// <summary>
    ///     The exterior cell grid of one worldspace, derived from its NAM0 (min corner) and
    ///     NAM9 (max corner) object bounds. Verified against all 14 FalloutNV.esm worldspaces:
    ///     <c>(maxX - minX + 1) * (maxY - minY + 1)</c> reproduces the shipped OFST entry count
    ///     exactly, 14 of 14.
    /// </summary>
    internal sealed record WorldGrid(int MinX, int MinY, int Columns, int Rows)
    {
        internal int Count => Columns * Rows;

        /// <summary>Table index for a cell coordinate, or -1 when it falls outside the grid.</summary>
        internal int IndexOf(int gridX, int gridY)
        {
            var column = gridX - MinX;
            var row = gridY - MinY;
            return column < 0 || column >= Columns || row < 0 || row >= Rows
                ? -1
                : (row * Columns) + column;
        }
    }

    /// <summary>
    ///     Reads the worldspace grid from an encoded WRLD record's NAM0/NAM9 bounds.
    ///     Returns null when the bounds are missing or degenerate, or when the record already
    ///     carries an OFST — in every one of those cases the caller must leave the record alone.
    /// </summary>
    internal static WorldGrid? TryReadGrid(byte[] wrldRecordBytes)
    {
        if (wrldRecordBytes.Length <= EsmParser.MainRecordHeaderSize)
        {
            return null;
        }

        var data = wrldRecordBytes[EsmParser.MainRecordHeaderSize..];
        (float X, float Y)? min = null;
        (float X, float Y)? max = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, data.Length, bigEndian: false))
        {
            switch (sub.Signature)
            {
                case "OFST":
                    return null;
                case "NAM0" when sub.DataLength >= 8:
                    min = ReadPair(data, sub.DataOffset);
                    break;
                case "NAM9" when sub.DataLength >= 8:
                    max = ReadPair(data, sub.DataOffset);
                    break;
                default:
                    break;
            }
        }

        if (min is null || max is null)
        {
            return null;
        }

        var minX = ToCellCoordinate(min.Value.X);
        var minY = ToCellCoordinate(min.Value.Y);
        var columns = ToCellCoordinate(max.Value.X) - minX + 1;
        var rows = ToCellCoordinate(max.Value.Y) - minY + 1;

        return columns <= 0 || rows <= 0 || (long)columns * rows > MaxEntries
            ? null
            : new WorldGrid(minX, minY, columns, rows);
    }

    /// <summary>
    ///     Appends a zero-filled OFST subrecord of <paramref name="entryCount" /> entries to an
    ///     encoded WRLD record and fixes up the header's data size. OFST is emitted last, which
    ///     is where every shipped plugin puts it. Reports where the payload landed within the
    ///     returned record so the caller can patch real offsets in once the cells are written.
    /// </summary>
    internal static byte[] AppendEmptyOfst(byte[] wrldRecordBytes, int entryCount, out int payloadOffset)
    {
        using var subrecordStream = new MemoryStream();
        using (var writer = new BinaryWriter(subrecordStream, Encoding.Latin1, leaveOpen: true))
        {
            // Handles the XXXX extended-size escape on its own — WastelandNV's table is
            // 177,112 bytes, far past the uint16 subrecord length field.
            SubrecordEncoder.WriteSubrecord(writer, "OFST", new byte[entryCount * 4]);
        }

        var ofstBytes = subrecordStream.ToArray();
        var result = new byte[wrldRecordBytes.Length + ofstBytes.Length];
        wrldRecordBytes.CopyTo(result, 0);
        ofstBytes.CopyTo(result, wrldRecordBytes.Length);

        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(4, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), dataSize + (uint)ofstBytes.Length);

        // The payload is the tail of whatever the encoder emitted, so this stays correct
        // whether or not an XXXX prefix was needed.
        payloadOffset = wrldRecordBytes.Length + ofstBytes.Length - (entryCount * 4);
        return result;
    }

    /// <summary>
    ///     Writes the real WRLD-relative offsets over a previously appended zero-filled table.
    ///     Coordinates outside the grid are skipped; when two cells claim one coordinate the
    ///     lower offset wins, matching <c>EsmConverterOfstBuilder</c>.
    /// </summary>
    internal static void PatchTable(
        Stream stream,
        long ofstPayloadPosition,
        long wrldRecordPosition,
        WorldGrid grid,
        IReadOnlyList<(long Position, int GridX, int GridY)> cells)
    {
        if (!stream.CanSeek || cells.Count == 0)
        {
            return;
        }

        var table = new uint[grid.Count];
        foreach (var (position, gridX, gridY) in cells)
        {
            var index = grid.IndexOf(gridX, gridY);
            if (index < 0)
            {
                continue;
            }

            var relative = position - wrldRecordPosition;
            if (relative <= 0 || relative > uint.MaxValue)
            {
                continue;
            }

            var value = (uint)relative;
            if (table[index] == 0 || value < table[index])
            {
                table[index] = value;
            }
        }

        var payload = new byte[table.Length * 4];
        for (var i = 0; i < table.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(i * 4, 4), table[i]);
        }

        var resume = stream.Position;
        stream.Position = ofstPayloadPosition;
        stream.Write(payload);
        stream.Position = resume;
    }

    /// <summary>Reads a CELL record's XCLC grid coordinate. False when the record has no XCLC.</summary>
    internal static bool TryReadCellGrid(byte[] cellRecordBytes, out int gridX, out int gridY)
    {
        gridX = 0;
        gridY = 0;
        if (cellRecordBytes.Length <= EsmParser.MainRecordHeaderSize)
        {
            return false;
        }

        var data = cellRecordBytes[EsmParser.MainRecordHeaderSize..];
        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, data.Length, bigEndian: false))
        {
            if (sub.Signature != "XCLC" || sub.DataLength < 8)
            {
                continue;
            }

            gridX = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(sub.DataOffset, 4));
            gridY = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(sub.DataOffset + 4, 4));
            return true;
        }

        return false;
    }

    private static (float X, float Y) ReadPair(byte[] data, int offset) =>
        (BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset + 4, 4)));

    private static int ToCellCoordinate(float worldUnits) =>
        float.IsNaN(worldUnits) || Math.Abs(worldUnits) >= UnsetFloatThreshold
            ? 0
            : (int)Math.Round(worldUnits / CellSize);
}
