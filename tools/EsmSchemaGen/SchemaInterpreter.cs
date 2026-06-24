using System.Buffers.Binary;
using System.Text;
using EsmSchemaGen.Ir;

namespace EsmSchemaGen;

/// <summary>One decoded value: a backslash-delimited field path and the decoded .NET value.</summary>
public sealed record DecodedField(string Path, object? Value);

/// <summary>
///     The result of applying a record schema to a raw record: the decoded fields plus coverage counts
///     (how many of the record's subrecords the schema recognized).
/// </summary>
public sealed record DecodedRecord(
    string Type,
    bool HasSchema,
    IReadOnlyList<DecodedField> Fields,
    int MatchedSubrecords,
    int TotalSubrecords);

/// <summary>
///     A deliberately small, read-only interpreter that gives raw TES3 record bytes meaning via the
///     generated <see cref="RecordDef" /> schema. It matches each subrecord to a top-level schema member
///     by signature and decodes scalar fields and the leading (sequentially-decodable) portion of
///     structs. Anything it cannot model yet (nested structs, arrays, unions, unknown members) is left
///     as a raw note rather than guessed — it is a proof-of-correspondence for the schema, not the final
///     byte-parity reader. Little-endian (PC / TES3).
/// </summary>
public static class SchemaInterpreter
{
    public static DecodedRecord Decode(RecordDef? schema, Tes3RawRecord raw)
    {
        var fields = new List<DecodedField>();
        if (schema is null)
        {
            return new DecodedRecord(raw.Type, false, fields, 0, raw.Subrecords.Count);
        }

        var bySignature = new Dictionary<string, MemberDef>(StringComparer.Ordinal);
        foreach (var member in schema.Members)
        {
            if (member.Signature is { Length: > 0 } sig)
            {
                bySignature[sig] = member;
            }
        }

        var matched = 0;
        foreach (var sub in raw.Subrecords)
        {
            if (!bySignature.TryGetValue(sub.Signature, out var member))
            {
                continue;
            }

            matched++;
            DecodeMember(member, sub.Data, sub.Signature, fields);
        }

        return new DecodedRecord(raw.Type, true, fields, matched, raw.Subrecords.Count);
    }

    private static void DecodeMember(MemberDef member, byte[] data, string path, List<DecodedField> output)
    {
        switch (member)
        {
            case FieldDef field:
                output.Add(new DecodedField(path, DecodeScalar(field, data, 0, data.Length, out _)));
                break;
            case StructDef structDef:
                DecodeStruct(structDef, data, path, output);
                break;
            default:
                output.Add(new DecodedField(path, $"<{member.GetType().Name}: {data.Length} bytes>"));
                break;
        }
    }

    private static void DecodeStruct(StructDef structDef, byte[] data, string path, List<DecodedField> output)
    {
        var offset = 0;
        foreach (var member in structDef.Members)
        {
            switch (member)
            {
                case FieldDef field:
                {
                    var value = DecodeScalar(field, data, offset, data.Length, out var size);
                    if (size < 0)
                    {
                        return; // undecodable / variable width inside a struct — stop here
                    }

                    output.Add(new DecodedField($"{path}\\{field.Name}", value));
                    offset += size;
                    break;
                }
                case UnusedDef unused:
                    offset += unused.Size;
                    break;
                default:
                    return; // nested struct/array/union/unknown — beyond this proof-of-correspondence
            }

            if (offset > data.Length)
            {
                return;
            }
        }
    }

    private static object? DecodeScalar(FieldDef field, byte[] data, int offset, int limit, out int size)
    {
        var available = limit - offset;
        switch (field.Type)
        {
            case PrimType.U8:
                size = 1;
                return available >= 1 ? data[offset] : null;
            case PrimType.S8:
                size = 1;
                return available >= 1 ? (sbyte)data[offset] : null;
            case PrimType.U16:
                size = 2;
                return available >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(Span(data, offset, 2)) : null;
            case PrimType.S16:
                size = 2;
                return available >= 2 ? BinaryPrimitives.ReadInt16LittleEndian(Span(data, offset, 2)) : null;
            case PrimType.U24:
                size = 3;
                return available >= 3 ? data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) : null;
            case PrimType.U32:
            case PrimType.FormId:
                size = 4;
                return available >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(Span(data, offset, 4)) : null;
            case PrimType.S32:
                size = 4;
                return available >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(Span(data, offset, 4)) : null;
            case PrimType.U64:
                size = 8;
                return available >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(Span(data, offset, 8)) : null;
            case PrimType.S64:
                size = 8;
                return available >= 8 ? BinaryPrimitives.ReadInt64LittleEndian(Span(data, offset, 8)) : null;
            case PrimType.Float:
                size = 4;
                return available >= 4 ? BinaryPrimitives.ReadSingleLittleEndian(Span(data, offset, 4)) : null;
            case PrimType.Double:
                size = 8;
                return available >= 8 ? BinaryPrimitives.ReadDoubleLittleEndian(Span(data, offset, 8)) : null;
            case PrimType.Half:
                size = 2;
                return available >= 2 ? BinaryPrimitives.ReadHalfLittleEndian(Span(data, offset, 2)) : null;
            case PrimType.ZString:
            case PrimType.StringKC:
                // A fixed-width string inside a struct, else a whole-subrecord string takes the remainder.
                size = field.FixedSize ?? available;
                return DecodeString(data, offset, Math.Min(size, Math.Max(available, 0)));
            case PrimType.ByteArray:
                size = field.FixedSize ?? available;
                return $"<{Math.Min(size, Math.Max(available, 0))} bytes>";
            default:
                size = -1; // LString and anything else: not modeled in this bootstrap interpreter
                return null;
        }
    }

    private static ReadOnlySpan<byte> Span(byte[] data, int offset, int count) => data.AsSpan(offset, count);

    private static string DecodeString(byte[] data, int offset, int count)
    {
        if (count <= 0 || offset >= data.Length)
        {
            return string.Empty;
        }

        count = Math.Min(count, data.Length - offset);
        var end = offset + count;
        var len = count;
        while (len > 0 && data[offset + len - 1] == 0)
        {
            len--; // trim trailing NUL padding
        }

        _ = end;
        return Encoding.ASCII.GetString(data, offset, len);
    }
}
