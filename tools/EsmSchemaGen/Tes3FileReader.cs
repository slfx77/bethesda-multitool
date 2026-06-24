using System.Text;

namespace EsmSchemaGen;

/// <summary>One TES3 main record: its 4-char type, header flags, and its raw subrecords in file order.</summary>
public sealed record Tes3RawRecord(string Type, uint Flags, IReadOnlyList<Tes3RawSubrecord> Subrecords);

/// <summary>One TES3 subrecord: 4-char signature + its raw little-endian data bytes.</summary>
public sealed record Tes3RawSubrecord(string Signature, byte[] Data);

/// <summary>
///     Minimal reader for the TES3 (Morrowind) plugin layout: a flat record stream (no GRUP groups).
///     Each record is a 16-byte header — type[4], bodySize[u32], header1[u32], flags[u32] — followed by
///     <c>bodySize</c> bytes of subrecords, each framed as signature[4] + dataSize[u32] + data. All
///     little-endian (PC). This is just the byte-framing layer; the schema interpreter
///     (<see cref="SchemaInterpreter" />) gives the bytes meaning.
/// </summary>
public static class Tes3FileReader
{
    public static IEnumerable<Tes3RawRecord> Read(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        var length = fs.Length;

        while (fs.Position + 16 <= length)
        {
            var type = ReadSignature(br);
            var bodySize = br.ReadUInt32();
            br.ReadUInt32(); // header1 (unused / debug / deleted)
            var flags = br.ReadUInt32();

            var bodyEnd = fs.Position + bodySize;
            if (bodyEnd > length)
            {
                yield break; // truncated tail
            }

            var subrecords = new List<Tes3RawSubrecord>();
            while (fs.Position + 8 <= bodyEnd)
            {
                var sig = ReadSignature(br);
                var dataSize = br.ReadUInt32();
                if (fs.Position + dataSize > bodyEnd)
                {
                    break; // malformed subrecord length; skip to next record
                }

                subrecords.Add(new Tes3RawSubrecord(sig, br.ReadBytes((int)dataSize)));
            }

            fs.Position = bodyEnd;
            yield return new Tes3RawRecord(type, flags, subrecords);
        }
    }

    private static string ReadSignature(BinaryReader br)
    {
        var bytes = br.ReadBytes(4);
        var len = bytes.Length;
        while (len > 0 && bytes[len - 1] == 0)
        {
            len--;
        }

        return Encoding.ASCII.GetString(bytes, 0, len);
    }
}
