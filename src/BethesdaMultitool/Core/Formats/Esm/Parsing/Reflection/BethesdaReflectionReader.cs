using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Reflection;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Reflection;

/// <summary>
///     Strict reader for the self-describing version-4 <c>BETH</c> reflection streams embedded in
///     Starfield records. Class runtime offsets are diagnostic metadata only; OBJT values serialize
///     sequentially in field order, while DIFF values prefix each changed field with a 16-bit index
///     and terminate every nested class independently with <c>0xFFFF</c>.
/// </summary>
internal static class BethesdaReflectionReader
{
    private const uint ChunkBeth = 0x48544542; // BETH
    private const uint ChunkStrt = 0x54525453; // STRT
    private const uint ChunkType = 0x45505954; // TYPE
    private const uint ChunkClas = 0x53414C43; // CLAS
    private const uint ChunkList = 0x5453494C; // LIST
    private const uint ChunkObjt = 0x544A424F; // OBJT
    private const uint ChunkDiff = 0x46464944; // DIFF
    private const uint ChunkUser = 0x52455355; // USER
    private const uint ChunkUsrd = 0x44525355; // USRD
    private const uint SupportedVersion = 4;
    private const uint BuiltInTypeBase = 0xFFFFFF01;
    private const int HeaderSize = 24;

    internal static bool TryReadObject(
        ReadOnlySpan<byte> data,
        bool expectDiff,
        string expectedRootType,
        out BethesdaReflectionObject? value,
        out string? error)
    {
        value = null;
        error = null;
        if (!TryReadEnvelope(data, out var strings, out var chunks, out error))
        {
            return false;
        }

        var classes = new Dictionary<string, ClassDefinition>(StringComparer.Ordinal);
        var objectChunkIndex = -1;
        int? declaredClassCount = null;
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            var body = data.Slice(chunk.BodyOffset, chunk.BodyLength);
            switch (chunk.Type)
            {
                case ChunkType:
                    if (objectChunkIndex >= 0 || declaredClassCount.HasValue ||
                        body.Length != sizeof(uint))
                    {
                        error = "TYPE chunk is duplicate, misplaced, or malformed.";
                        return false;
                    }

                    var classCount = BinaryPrimitives.ReadUInt32LittleEndian(body);
                    if (classCount > int.MaxValue)
                    {
                        error = "TYPE class count exceeds supported bounds.";
                        return false;
                    }

                    declaredClassCount = (int)classCount;
                    break;
                case ChunkClas:
                    if (objectChunkIndex >= 0 || !declaredClassCount.HasValue ||
                        !TryReadClassDefinition(body, strings, out var definition, out error) ||
                        !classes.TryAdd(definition!.Name, definition))
                    {
                        error ??= "Duplicate or misplaced CLAS definition.";
                        return false;
                    }

                    break;
                case ChunkObjt:
                case ChunkDiff:
                    if (objectChunkIndex >= 0 || (chunk.Type == ChunkDiff) != expectDiff)
                    {
                        error = "Reflection stream has an unexpected or duplicate object chunk.";
                        return false;
                    }

                    objectChunkIndex = index;
                    break;
                case ChunkList:
                case ChunkUser:
                case ChunkUsrd:
                    if (objectChunkIndex < 0)
                    {
                        error = "Out-of-line reflection data appeared before the reflected object.";
                        return false;
                    }

                    break;
                default:
                    error = $"Unsupported reflection chunk 0x{chunk.Type:X8}.";
                    return false;
            }
        }

        if (objectChunkIndex < 0)
        {
            error = "Reflection stream contains no object payload.";
            return false;
        }

        if (!declaredClassCount.HasValue || classes.Count != declaredClassCount.Value)
        {
            error = "TYPE class count does not match the CLAS definitions.";
            return false;
        }

        var objectChunk = chunks[objectChunkIndex];
        var objectBody = data.Slice(objectChunk.BodyOffset, objectChunk.BodyLength);
        if (objectBody.Length < sizeof(uint) ||
            !TryResolveNamedType(
                BinaryPrimitives.ReadUInt32LittleEndian(objectBody), strings, out var rootType) ||
            !string.Equals(rootType, expectedRootType, StringComparison.Ordinal))
        {
            error = "Reflection root type is missing or does not match the expected class.";
            return false;
        }

        var decoder = new ValueDecoder(data, strings, classes, chunks, objectChunkIndex + 1);
        var position = sizeof(uint);
        if (!decoder.TryReadClass(
                objectBody, ref position, rootType, expectDiff, out value, out error) ||
            position != objectBody.Length ||
            decoder.NextSideChunkIndex != chunks.Count)
        {
            error ??= position != objectBody.Length
                ? "Reflected object payload has trailing or unread bytes."
                : "Reflection stream has unconsumed out-of-line chunks.";
            value = null;
            return false;
        }

        return true;
    }

    internal static bool TryMerge(
        BethesdaReflectionObject inherited,
        BethesdaReflectionObject overlay,
        out BethesdaReflectionObject? merged)
    {
        merged = null;
        if (!string.Equals(inherited.TypeName, overlay.TypeName, StringComparison.Ordinal))
        {
            return false;
        }

        var fields = new Dictionary<string, BethesdaReflectionValue>(inherited.Fields, StringComparer.Ordinal);
        foreach (var (name, replacement) in overlay.Fields)
        {
            if (fields.TryGetValue(name, out var current) &&
                current is BethesdaReflectionObjectValue currentObject &&
                replacement is BethesdaReflectionObjectValue replacementObject)
            {
                if (!TryMerge(currentObject.Value, replacementObject.Value, out var nested))
                {
                    return false;
                }

                fields[name] = new BethesdaReflectionObjectValue(nested!);
            }
            else
            {
                fields[name] = replacement;
            }
        }

        merged = new BethesdaReflectionObject(inherited.TypeName, fields);
        return true;
    }

    private static bool TryReadEnvelope(
        ReadOnlySpan<byte> data,
        out Dictionary<uint, string> strings,
        out List<ChunkDescriptor> chunks,
        out string? error)
    {
        strings = [];
        chunks = [];
        error = null;
        if (data.Length < HeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(data) != ChunkBeth ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 8 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != SupportedVersion ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[16..]) != ChunkStrt)
        {
            error = "Invalid version-4 BETH reflection header.";
            return false;
        }

        var totalChunks = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        var stringBytes = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
        if (totalChunks < 3 || stringBytes > (uint)(data.Length - HeaderSize) ||
            !TryReadStringTable(data.Slice(HeaderSize, (int)stringBytes), strings))
        {
            error = "Invalid or unterminated reflection string table.";
            return false;
        }

        var remainingChunks = totalChunks - 2; // BETH and STRT are included in the declared count.
        if (remainingChunks > int.MaxValue)
        {
            error = "Reflection chunk count exceeds supported bounds.";
            return false;
        }

        var position = HeaderSize + (int)stringBytes;
        for (var index = 0u; index < remainingChunks; index++)
        {
            if (position > data.Length - 8)
            {
                error = "Truncated reflection chunk header.";
                return false;
            }

            var type = BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data[(position + 4)..]);
            if (size > (uint)(data.Length - position - 8))
            {
                error = "Truncated reflection chunk body.";
                return false;
            }

            chunks.Add(new ChunkDescriptor(type, position + 8, (int)size));
            position += 8 + (int)size;
        }

        if (position != data.Length)
        {
            error = "Reflection stream has trailing bytes outside its declared chunks.";
            return false;
        }

        return true;
    }

    private static bool TryReadStringTable(
        ReadOnlySpan<byte> payload,
        Dictionary<uint, string> strings)
    {
        var start = 0;
        while (start < payload.Length)
        {
            var terminator = payload[start..].IndexOf((byte)0);
            if (terminator < 0)
            {
                return false;
            }

            strings[(uint)start] = Encoding.ASCII.GetString(payload.Slice(start, terminator));
            start += terminator + 1;
        }

        return start == payload.Length;
    }

    private static bool TryReadClassDefinition(
        ReadOnlySpan<byte> body,
        IReadOnlyDictionary<uint, string> strings,
        out ClassDefinition? definition,
        out string? error)
    {
        definition = null;
        error = null;
        if (body.Length < 12 ||
            !TryResolveNamedType(BinaryPrimitives.ReadUInt32LittleEndian(body), strings, out var className))
        {
            error = "CLAS has no valid class name.";
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(body[8..]);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(body[10..]);
        if (body.Length != 12 + (count * 12))
        {
            error = "CLAS field count does not match its body length.";
            return false;
        }

        var fields = new List<FieldDefinition>(count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var field = body.Slice(12 + (index * 12), 12);
            if (!TryResolveNamedType(
                    BinaryPrimitives.ReadUInt32LittleEndian(field), strings, out var fieldName) ||
                !TryResolveType(
                    BinaryPrimitives.ReadUInt32LittleEndian(field[4..]), strings, out var fieldType) ||
                !names.Add(fieldName))
            {
                error = "CLAS contains an invalid or duplicate field descriptor.";
                return false;
            }

            fields.Add(new FieldDefinition(fieldName, fieldType));
        }

        definition = new ClassDefinition(className, (flags & 4) != 0, fields);
        return true;
    }

    private static bool TryResolveNamedType(
        uint token,
        IReadOnlyDictionary<uint, string> strings,
        out string name)
    {
        return strings.TryGetValue(token, out name!);
    }

    private static bool TryResolveType(
        uint token,
        IReadOnlyDictionary<uint, string> strings,
        out ReflectedType type)
    {
        if (strings.TryGetValue(token, out var name))
        {
            type = new ReflectedType(name, null);
            return true;
        }

        var builtInIndex = token >= BuiltInTypeBase ? token - BuiltInTypeBase : uint.MaxValue;
        if (builtInIndex <= (uint)BuiltInType.Unknown &&
            Enum.IsDefined((BuiltInType)builtInIndex))
        {
            type = new ReflectedType(BuiltInName((BuiltInType)builtInIndex), (BuiltInType)builtInIndex);
            return true;
        }

        type = default;
        return false;
    }

    private static string BuiltInName(BuiltInType type)
    {
        return type switch
        {
            BuiltInType.None => "null",
            BuiltInType.String => "String",
            BuiltInType.List => "List",
            BuiltInType.Map => "Map",
            BuiltInType.Ref => "Ref",
            BuiltInType.Int8 => "Int8",
            BuiltInType.UInt8 => "UInt8",
            BuiltInType.Int16 => "Int16",
            BuiltInType.UInt16 => "UInt16",
            BuiltInType.Int32 => "Int32",
            BuiltInType.UInt32 => "UInt32",
            BuiltInType.Int64 => "Int64",
            BuiltInType.UInt64 => "UInt64",
            BuiltInType.Bool => "Bool",
            BuiltInType.Float => "Float",
            BuiltInType.Double => "Double",
            _ => "Unknown"
        };
    }

    private readonly record struct ChunkDescriptor(uint Type, int BodyOffset, int BodyLength);

    private sealed record FieldDefinition(string Name, ReflectedType Type);

    private sealed record ClassDefinition(
        string Name,
        bool IsUser,
        IReadOnlyList<FieldDefinition> Fields);

    private readonly record struct ReflectedType(string Name, BuiltInType? BuiltIn);

    private enum BuiltInType : uint
    {
        None = 0,
        String = 1,
        List = 2,
        Map = 3,
        Ref = 4,
        Int8 = 7,
        UInt8 = 8,
        Int16 = 9,
        UInt16 = 10,
        Int32 = 11,
        UInt32 = 12,
        Int64 = 13,
        UInt64 = 14,
        Bool = 15,
        Float = 16,
        Double = 17,
        Unknown = 18
    }

    private ref struct ValueDecoder(
        ReadOnlySpan<byte> stream,
        IReadOnlyDictionary<uint, string> strings,
        IReadOnlyDictionary<string, ClassDefinition> classes,
        IReadOnlyList<ChunkDescriptor> chunks,
        int nextSideChunkIndex)
    {
        private readonly ReadOnlySpan<byte> _stream = stream;
        private readonly IReadOnlyDictionary<uint, string> _strings = strings;
        private readonly IReadOnlyDictionary<string, ClassDefinition> _classes = classes;
        private readonly IReadOnlyList<ChunkDescriptor> _chunks = chunks;

        internal int NextSideChunkIndex { get; private set; } = nextSideChunkIndex;

        internal bool TryReadClass(
            ReadOnlySpan<byte> source,
            ref int position,
            string typeName,
            bool isDiff,
            out BethesdaReflectionObject? value,
            out string? error)
        {
            value = null;
            error = null;
            if (!_classes.TryGetValue(typeName, out var definition) || definition.IsUser)
            {
                error = $"Missing or unsupported user class definition '{typeName}'.";
                return false;
            }

            var fields = new Dictionary<string, BethesdaReflectionValue>(StringComparer.Ordinal);
            if (!isDiff)
            {
                foreach (var field in definition.Fields)
                {
                    if (!TryReadValue(source, ref position, field.Type, false, out var fieldValue, out error))
                    {
                        return false;
                    }

                    fields.Add(field.Name, fieldValue!);
                }
            }
            else
            {
                while (true)
                {
                    if (!TryReadUInt16(source, ref position, out var fieldIndex))
                    {
                        error = $"Truncated DIFF index list in '{typeName}'.";
                        return false;
                    }

                    if (fieldIndex == ushort.MaxValue)
                    {
                        break;
                    }

                    if (fieldIndex >= definition.Fields.Count)
                    {
                        error = $"DIFF field index {fieldIndex} exceeds '{typeName}'.";
                        return false;
                    }

                    var field = definition.Fields[fieldIndex];
                    if (fields.ContainsKey(field.Name) ||
                        !TryReadValue(source, ref position, field.Type, true, out var fieldValue, out error))
                    {
                        error ??= $"Duplicate DIFF field '{field.Name}'.";
                        return false;
                    }

                    fields.Add(field.Name, fieldValue!);
                }
            }

            value = new BethesdaReflectionObject(typeName, fields);
            return true;
        }

        private bool TryReadValue(
            ReadOnlySpan<byte> source,
            ref int position,
            ReflectedType type,
            bool isDiff,
            out BethesdaReflectionValue? value,
            out string? error)
        {
            value = null;
            error = null;
            if (type.BuiltIn is null)
            {
                if (_classes.TryGetValue(type.Name, out var definition) && definition.IsUser)
                {
                    return TryReadUserValue(type.Name, out value, out error);
                }

                if (!TryReadClass(source, ref position, type.Name, isDiff, out var nested, out error))
                {
                    return false;
                }

                value = new BethesdaReflectionObjectValue(nested!);
                return true;
            }

            switch (type.BuiltIn.Value)
            {
                case BuiltInType.None:
                    value = new BethesdaReflectionNullValue();
                    return true;
                case BuiltInType.String:
                    if (!TryReadUInt16(source, ref position, out var stringLength) ||
                        position > source.Length - stringLength)
                    {
                        error = "Truncated reflected string.";
                        return false;
                    }

                    var stringBytes = source.Slice(position, stringLength);
                    var terminator = stringBytes.IndexOf((byte)0);
                    if (terminator >= 0)
                    {
                        stringBytes = stringBytes[..terminator];
                    }

                    value = new BethesdaReflectionStringValue(Encoding.ASCII.GetString(stringBytes));
                    position += stringLength;
                    return true;
                case BuiltInType.List:
                    return TryReadList(isDiff, out value, out error);
                case BuiltInType.Map:
                    error = "MAP reflection values are not supported by the bounded WTHS reader.";
                    return false;
                case BuiltInType.Ref:
                    if (!TryReadUInt32(source, ref position, out var referenceTypeToken) ||
                        !TryResolveType(referenceTypeToken, _strings, out var referenceType) ||
                        !TryReadValue(source, ref position, referenceType, isDiff, out var reference, out error))
                    {
                        error ??= "Malformed reflected reference.";
                        return false;
                    }

                    value = new BethesdaReflectionReferenceValue(referenceType.Name, reference!);
                    return true;
                case BuiltInType.Int8:
                    if (!TryReadByte(source, ref position, out var int8)) return Fail("Truncated Int8.", out error);
                    value = new BethesdaReflectionSignedValue((sbyte)int8);
                    return true;
                case BuiltInType.UInt8:
                    if (!TryReadByte(source, ref position, out var uint8)) return Fail("Truncated UInt8.", out error);
                    value = new BethesdaReflectionUnsignedValue(uint8);
                    return true;
                case BuiltInType.Int16:
                    if (!TryReadUInt16(source, ref position, out var int16)) return Fail("Truncated Int16.", out error);
                    value = new BethesdaReflectionSignedValue((short)int16);
                    return true;
                case BuiltInType.UInt16:
                    if (!TryReadUInt16(source, ref position, out var uint16)) return Fail("Truncated UInt16.", out error);
                    value = new BethesdaReflectionUnsignedValue(uint16);
                    return true;
                case BuiltInType.Int32:
                    if (!TryReadUInt32(source, ref position, out var int32)) return Fail("Truncated Int32.", out error);
                    value = new BethesdaReflectionSignedValue((int)int32);
                    return true;
                case BuiltInType.UInt32:
                    if (!TryReadUInt32(source, ref position, out var uint32)) return Fail("Truncated UInt32.", out error);
                    value = new BethesdaReflectionUnsignedValue(uint32);
                    return true;
                case BuiltInType.Int64:
                    if (!TryReadUInt64(source, ref position, out var int64)) return Fail("Truncated Int64.", out error);
                    value = new BethesdaReflectionSignedValue((long)int64);
                    return true;
                case BuiltInType.UInt64:
                    if (!TryReadUInt64(source, ref position, out var uint64)) return Fail("Truncated UInt64.", out error);
                    value = new BethesdaReflectionUnsignedValue(uint64);
                    return true;
                case BuiltInType.Bool:
                    if (!TryReadByte(source, ref position, out var boolean)) return Fail("Truncated Bool.", out error);
                    if (boolean > 1) return Fail("Reflected Bool is not 0 or 1.", out error);
                    value = new BethesdaReflectionBoolValue(boolean != 0);
                    return true;
                case BuiltInType.Float:
                    if (!TryReadUInt32(source, ref position, out var floatBits)) return Fail("Truncated Float.", out error);
                    var single = BitConverter.UInt32BitsToSingle(floatBits);
                    if (!float.IsFinite(single)) return Fail("Reflected Float is non-finite.", out error);
                    value = new BethesdaReflectionFloatValue(single);
                    return true;
                case BuiltInType.Double:
                    if (!TryReadUInt64(source, ref position, out var doubleBits)) return Fail("Truncated Double.", out error);
                    var doubleValue = BitConverter.UInt64BitsToDouble(doubleBits);
                    if (!double.IsFinite(doubleValue)) return Fail("Reflected Double is non-finite.", out error);
                    value = new BethesdaReflectionFloatValue(doubleValue);
                    return true;
                default:
                    error = $"Unsupported reflected type '{type.Name}'.";
                    return false;
            }
        }

        private bool TryReadUserValue(
            string declaredType,
            out BethesdaReflectionValue? value,
            out string? error)
        {
            value = null;
            error = null;
            if (NextSideChunkIndex >= _chunks.Count ||
                _chunks[NextSideChunkIndex].Type is not (ChunkUser or ChunkUsrd))
            {
                error = $"Reflected user class '{declaredType}' has no matching USER/USRD chunk.";
                return false;
            }

            var descriptor = _chunks[NextSideChunkIndex++];
            var body = _stream.Slice(descriptor.BodyOffset, descriptor.BodyLength);
            var position = 0;
            if (!TryReadUInt32(body, ref position, out var declaredTypeToken) ||
                !TryResolveNamedType(declaredTypeToken, _strings, out var chunkDeclaredType) ||
                !string.Equals(chunkDeclaredType, declaredType, StringComparison.Ordinal))
            {
                error = $"USER/USRD chunk does not declare expected class '{declaredType}'.";
                return false;
            }

            if (!TryReadUInt32(body, ref position, out var serializedTypeToken) ||
                !TryResolveType(serializedTypeToken, _strings, out var serializedType) ||
                serializedType.BuiltIn == BuiltInType.Unknown ||
                (serializedType.BuiltIn is null &&
                 !string.Equals(serializedType.Name, declaredType, StringComparison.Ordinal)))
            {
                error = $"USER/USRD chunk for '{declaredType}' has an unsupported serialized type.";
                return false;
            }

            value = new BethesdaReflectionUserValue(
                declaredType,
                serializedType.Name,
                descriptor.Type == ChunkUsrd,
                body[position..].ToArray());
            return true;
        }

        private bool TryReadList(
            bool isDiff,
            out BethesdaReflectionValue? value,
            out string? error)
        {
            value = null;
            error = null;
            if (NextSideChunkIndex >= _chunks.Count ||
                _chunks[NextSideChunkIndex].Type != ChunkList)
            {
                error = "Reflected List has no matching out-of-line LIST chunk.";
                return false;
            }

            var descriptor = _chunks[NextSideChunkIndex++];
            var body = _stream.Slice(descriptor.BodyOffset, descriptor.BodyLength);
            var position = 0;
            if (!TryReadUInt32(body, ref position, out var elementTypeToken) ||
                !TryResolveType(elementTypeToken, _strings, out var elementType) ||
                !TryReadUInt32(body, ref position, out var count) ||
                count > int.MaxValue)
            {
                error = "Malformed LIST header.";
                return false;
            }

            var values = new List<BethesdaReflectionValue>((int)count);
            for (var index = 0u; index < count; index++)
            {
                if (!TryReadValue(body, ref position, elementType, isDiff, out var item, out error))
                {
                    return false;
                }

                values.Add(item!);
            }

            if (position != body.Length)
            {
                error = "LIST body has trailing or unread bytes.";
                return false;
            }

            value = new BethesdaReflectionListValue(elementType.Name, values);
            return true;
        }

        private static bool TryReadByte(ReadOnlySpan<byte> source, ref int position, out byte value)
        {
            value = 0;
            if ((uint)position >= (uint)source.Length) return false;
            value = source[position++];
            return true;
        }

        private static bool TryReadUInt16(ReadOnlySpan<byte> source, ref int position, out ushort value)
        {
            value = 0;
            if (position < 0 || position > source.Length - sizeof(ushort)) return false;
            value = BinaryPrimitives.ReadUInt16LittleEndian(source[position..]);
            position += sizeof(ushort);
            return true;
        }

        private static bool TryReadUInt32(ReadOnlySpan<byte> source, ref int position, out uint value)
        {
            value = 0;
            if (position < 0 || position > source.Length - sizeof(uint)) return false;
            value = BinaryPrimitives.ReadUInt32LittleEndian(source[position..]);
            position += sizeof(uint);
            return true;
        }

        private static bool TryReadUInt64(ReadOnlySpan<byte> source, ref int position, out ulong value)
        {
            value = 0;
            if (position < 0 || position > source.Length - sizeof(ulong)) return false;
            value = BinaryPrimitives.ReadUInt64LittleEndian(source[position..]);
            position += sizeof(ulong);
            return true;
        }

        private static bool Fail(string message, out string? error)
        {
            error = message;
            return false;
        }
    }
}
