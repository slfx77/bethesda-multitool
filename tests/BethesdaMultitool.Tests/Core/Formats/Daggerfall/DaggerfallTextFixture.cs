using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     Synthetic <c>TEXT.RSC</c> and <c>BOKnnnnn.TXT</c> images in the retail layouts: a u16 header
///     length + (id, offset) entries + a terminator entry for the string table; a 236-byte header
///     + page-offset table for a book.
/// </summary>
internal static class DaggerfallTextFixture
{
    /// <summary>
    ///     Builds a TEXT.RSC image. Each body is written followed by an end-of-record byte; a null
    ///     body makes that id an alias of the previous record's offset, as six retail records are.
    /// </summary>
    public static byte[] TextRsc(params (ushort Id, byte[]? Body)[] records)
    {
        var headerLength = (records.Length + 1) * 6;
        var dataStart = 2 + headerLength;
        var data = new List<byte>();
        var offsets = new int[records.Length];
        for (var i = 0; i < records.Length; i++)
        {
            if (records[i].Body is { } body)
            {
                offsets[i] = dataStart + data.Count;
                data.AddRange(body);
                data.Add(0xFE);
            }
            else
            {
                offsets[i] = i == 0 ? dataStart : offsets[i - 1];
            }
        }

        var bytes = new List<byte>();
        AddUInt16(bytes, (ushort)headerLength);
        for (var i = 0; i < records.Length; i++)
        {
            AddUInt16(bytes, records[i].Id);
            AddUInt32(bytes, (uint)offsets[i]);
        }

        AddUInt16(bytes, 0xFFFF);
        AddUInt32(bytes, (uint)(dataStart + data.Count));
        bytes.AddRange(data);
        return [.. bytes];
    }

    /// <summary>Builds a book image from raw page bytes (include the 0xF6 terminator yourself).</summary>
    public static byte[] Book(string title, string author, string flag, uint price, ushort unknown1, params byte[][] pages)
    {
        var header = new byte[236];
        Encoding.Latin1.GetBytes(title).CopyTo(header, 0);
        Encoding.Latin1.GetBytes(author).CopyTo(header, 64);
        Encoding.Latin1.GetBytes(flag).CopyTo(header, 128);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(224), price);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(228), unknown1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(230), 1234);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(232), 2345);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(234), (ushort)pages.Length);

        var table = new byte[pages.Length * 4];
        var offset = header.Length + table.Length;
        for (var i = 0; i < pages.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(i * 4), (uint)offset);
            offset += pages[i].Length;
        }

        return [.. header, .. table, .. pages.SelectMany(p => p)];
    }

    /// <summary>Latin-1 bytes of a string.</summary>
    public static byte[] Bytes(string text)
    {
        return Encoding.Latin1.GetBytes(text);
    }

    /// <summary>A page: the text followed by the end-of-page byte.</summary>
    public static byte[] Page(string text)
    {
        return [.. Bytes(text), 0xF6];
    }

    private static void AddUInt16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)(value & 0xFF));
        bytes.Add((byte)(value >> 8));
    }

    private static void AddUInt32(List<byte> bytes, uint value)
    {
        for (var shift = 0; shift < 32; shift += 8)
        {
            bytes.Add((byte)((value >> shift) & 0xFF));
        }
    }
}
