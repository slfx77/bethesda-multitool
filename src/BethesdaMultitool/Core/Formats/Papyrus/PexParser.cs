// Layout authority:
// - Champollion Pex/FileReader.cpp and Pex/FileReader.hpp
//   https://github.com/Orvid/Champollion/tree/main/Pex
// - Caprica Caprica/pex/PexFile.cpp, PexObject.cpp, PexInstruction.cpp, and related model readers
//   https://github.com/Orvid/Caprica/tree/master/Caprica/pex
//
// Both projects read the same source-derived layout: endian-selecting magic, a version/game header,
// a u16-length-prefixed string table, optional debug data, user flags, and size-delimited objects.
// Skyrim 3.1/3.2 is big-endian. Fallout 4 3.9 and Fallout 76 3.15 are little-endian and add const
// flags, structs, extended debug tables, and game-specific opcode tails. This is a clean parser
// implementation of that layout; no decompiler behavior is included here.

using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Papyrus;

/// <summary>Describes a structurally invalid or unsupported PEX binary.</summary>
#pragma warning disable RCS1194 // standard ctor trio is present; the rule additionally expects the obsolete serialization ctor
public sealed class PexParseException : IOException
#pragma warning restore RCS1194
{
    public PexParseException()
        : base()
    {
    }

    public PexParseException(string message)
        : base(message)
    {
    }

    public PexParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PexParseException(string message, int offset, Exception? innerException = null)
        : base($"PEX parse error at 0x{offset:X}: {message}", innerException)
    {
        Offset = offset;
    }

    public int Offset { get; }
}

/// <summary>
///     Bounded parser for the shipped Skyrim, Fallout 4, and Fallout 76 PEX variants. All string
///     references are validated, object sizes are enforced, and every count is checked against the
///     bytes remaining before a collection is allocated.
/// </summary>
public static class PexParser
{
    // PEX files are normally KB-sized. The ceiling keeps non-seekable/malicious streams from
    // becoming an unbounded allocation while leaving ample room for generated script libraries.
    public const int MaximumFileSize = 256 * 1024 * 1024;

    // The wire format permits 32-bit call-vararg counts, and a one-byte None value can otherwise
    // expand into a much larger managed object graph. This cumulative budget is charged before
    // every collection allocation, including fixed and variadic instruction arguments.
    public const int MaximumParsedItems = 1_000_000;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static ReadOnlySpan<byte> LittleEndianMagic => [0xDE, 0xC0, 0x57, 0xFA];
    private static ReadOnlySpan<byte> BigEndianMagic => [0xFA, 0x57, 0xC0, 0xDE];

    public static PexFile Parse(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        using var stream = File.OpenRead(filePath);
        return Parse(stream);
    }

    /// <summary>Parses from the stream's current position without closing the stream.</summary>
    public static PexFile Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The PEX stream must be readable.", nameof(stream));
        }

        if (stream.CanSeek)
        {
            var remaining = stream.Length - stream.Position;
            if (remaining < 0 || remaining > MaximumFileSize)
            {
                throw new PexParseException(
                    $"file length {remaining} is outside the supported 0..{MaximumFileSize} byte range",
                    0);
            }
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = stream.Read(chunk, 0, chunk.Length);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaximumFileSize)
            {
                throw new PexParseException(
                    $"file exceeds the {MaximumFileSize}-byte safety limit",
                    checked((int)buffer.Length));
            }

            buffer.Write(chunk, 0, read);
        }

        return Parse(buffer.ToArray());
    }

    public static PexFile Parse(ReadOnlyMemory<byte> data)
    {
        if (data.Length > MaximumFileSize)
        {
            throw new PexParseException(
                $"file exceeds the {MaximumFileSize}-byte safety limit",
                0);
        }

        return ParseCore(data.Span);
    }

    private static PexFile ParseCore(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            throw new PexParseException("truncated magic number", data.Length);
        }

        PexEndianness endianness;
        if (data[..4].SequenceEqual(LittleEndianMagic))
        {
            endianness = PexEndianness.Little;
        }
        else if (data[..4].SequenceEqual(BigEndianMagic))
        {
            endianness = PexEndianness.Big;
        }
        else
        {
            throw new PexParseException("invalid magic number", 0);
        }

        var reader = new PexCursor(
            data,
            endianness,
            new PexParseBudget(MaximumParsedItems));
        reader.Skip(4, "magic number");

        var major = reader.ReadByte("major version");
        var minor = reader.ReadByte("minor version");
        var rawGameId = reader.ReadUInt16("game identifier");
        if (!Enum.IsDefined(typeof(PexGameId), rawGameId))
        {
            throw reader.Invalid($"unsupported game identifier {rawGameId}", reader.AbsolutePosition - 2);
        }

        var gameId = (PexGameId)rawGameId;
        ValidateVariant(ref reader, endianness, major, minor, gameId);

        var compilationTime = reader.ReadInt64("compilation time");
        var sourceFileName = reader.ReadString(StrictUtf8, "source file name");
        var userName = reader.ReadString(StrictUtf8, "user name");
        var computerName = reader.ReadString(StrictUtf8, "computer name");
        var header = new PexHeader(
            endianness,
            major,
            minor,
            gameId,
            compilationTime,
            sourceFileName,
            userName,
            computerName);

        var stringTable = ReadStringTable(ref reader);
        var hasDebugInfo = reader.ReadByte("debug-info presence flag") != 0;
        var debugInfo = hasDebugInfo ? ReadDebugInfo(ref reader, gameId, stringTable) : null;
        var userFlags = ReadUserFlags(ref reader, stringTable);
        var objects = ReadObjects(ref reader, gameId, stringTable);

        reader.RequireEnd("PEX file");
        return new PexFile(header, stringTable, debugInfo, userFlags, objects, reader.AbsolutePosition);
    }

    private static void ValidateVariant(
        ref PexCursor reader,
        PexEndianness endianness,
        byte major,
        byte minor,
        PexGameId gameId)
    {
        var valid = gameId switch
        {
            PexGameId.Skyrim => endianness == PexEndianness.Big && major == 3 && minor is 1 or 2,
            PexGameId.Fallout4 => endianness == PexEndianness.Little && major == 3 && minor == 9,
            PexGameId.Fallout76 => endianness == PexEndianness.Little && major == 3 && minor == 15,
            _ => false
        };

        if (!valid)
        {
            throw reader.Invalid(
                $"unsupported {gameId} PEX variant v{major}.{minor} ({endianness}-endian)",
                4);
        }
    }

    private static ImmutableArray<string> ReadStringTable(ref PexCursor reader)
    {
        var count = reader.ReadUInt16("string-table count");
        reader.ValidateCount(count, 2, "string-table entries");
        var strings = ImmutableArray.CreateBuilder<string>(count);
        for (var i = 0; i < count; i++)
        {
            strings.Add(reader.ReadString(StrictUtf8, $"string-table entry {i}"));
        }

        return strings.MoveToImmutable();
    }

    private static PexDebugInfo ReadDebugInfo(
        ref PexCursor reader,
        PexGameId gameId,
        ImmutableArray<string> strings)
    {
        var modificationTime = reader.ReadInt64("debug modification time");
        var functionCount = reader.ReadUInt16("debug-function count");
        reader.ValidateCount(functionCount, 9, "debug functions");
        var functions = ImmutableArray.CreateBuilder<PexDebugFunction>(functionCount);
        for (var i = 0; i < functionCount; i++)
        {
            var objectName = ReadStringReference(ref reader, strings, "debug object name");
            var stateName = ReadStringReference(ref reader, strings, "debug state name");
            var functionName = ReadStringReference(ref reader, strings, "debug function name");
            var rawType = reader.ReadByte("debug function type");
            if (rawType > (byte)PexDebugFunctionType.Setter)
            {
                throw reader.Invalid($"invalid debug function type {rawType}");
            }

            var lineCount = reader.ReadUInt16("debug line-map count");
            reader.ValidateCount(lineCount, 2, "debug line-map entries");
            var lines = ImmutableArray.CreateBuilder<ushort>(lineCount);
            for (var line = 0; line < lineCount; line++)
            {
                lines.Add(reader.ReadUInt16("debug line number"));
            }

            functions.Add(new PexDebugFunction(
                objectName,
                stateName,
                functionName,
                (PexDebugFunctionType)rawType,
                lines.MoveToImmutable()));
        }

        var propertyGroups = ImmutableArray<PexDebugPropertyGroup>.Empty;
        var structOrders = ImmutableArray<PexDebugStructOrder>.Empty;
        if (gameId != PexGameId.Skyrim)
        {
            propertyGroups = ReadDebugPropertyGroups(ref reader, strings);
            structOrders = ReadDebugStructOrders(ref reader, strings);
        }

        return new PexDebugInfo(
            modificationTime,
            functions.MoveToImmutable(),
            propertyGroups,
            structOrders);
    }

    private static ImmutableArray<PexDebugPropertyGroup> ReadDebugPropertyGroups(
        ref PexCursor reader,
        ImmutableArray<string> strings)
    {
        var count = reader.ReadUInt16("debug property-group count");
        reader.ValidateCount(count, 12, "debug property groups");
        var groups = ImmutableArray.CreateBuilder<PexDebugPropertyGroup>(count);
        for (var i = 0; i < count; i++)
        {
            var objectName = ReadStringReference(ref reader, strings, "property-group object name");
            var groupName = ReadStringReference(ref reader, strings, "property-group name");
            var documentation = ReadStringReference(ref reader, strings, "property-group documentation");
            var userFlags = reader.ReadUInt32("property-group user flags");
            var propertyCount = reader.ReadUInt16("property-group property count");
            reader.ValidateCount(propertyCount, 2, "property-group properties");
            var properties = ImmutableArray.CreateBuilder<PexStringReference>(propertyCount);
            for (var property = 0; property < propertyCount; property++)
            {
                properties.Add(ReadStringReference(ref reader, strings, "property-group property"));
            }

            groups.Add(new PexDebugPropertyGroup(
                objectName,
                groupName,
                documentation,
                userFlags,
                properties.MoveToImmutable()));
        }

        return groups.MoveToImmutable();
    }

    private static ImmutableArray<PexDebugStructOrder> ReadDebugStructOrders(
        ref PexCursor reader,
        ImmutableArray<string> strings)
    {
        var count = reader.ReadUInt16("debug struct-order count");
        reader.ValidateCount(count, 6, "debug struct orders");
        var orders = ImmutableArray.CreateBuilder<PexDebugStructOrder>(count);
        for (var i = 0; i < count; i++)
        {
            var objectName = ReadStringReference(ref reader, strings, "struct-order object name");
            var structName = ReadStringReference(ref reader, strings, "struct-order struct name");
            var memberCount = reader.ReadUInt16("struct-order member count");
            reader.ValidateCount(memberCount, 2, "struct-order members");
            var members = ImmutableArray.CreateBuilder<PexStringReference>(memberCount);
            for (var member = 0; member < memberCount; member++)
            {
                members.Add(ReadStringReference(ref reader, strings, "struct-order member"));
            }

            orders.Add(new PexDebugStructOrder(objectName, structName, members.MoveToImmutable()));
        }

        return orders.MoveToImmutable();
    }

    private static ImmutableArray<PexUserFlag> ReadUserFlags(
        ref PexCursor reader,
        ImmutableArray<string> strings)
    {
        var count = reader.ReadUInt16("user-flag count");
        reader.ValidateCount(count, 3, "user flags");
        var flags = ImmutableArray.CreateBuilder<PexUserFlag>(count);
        for (var i = 0; i < count; i++)
        {
            var name = ReadStringReference(ref reader, strings, "user-flag name");
            var bitIndex = reader.ReadByte("user-flag bit index");
            flags.Add(new PexUserFlag(name, bitIndex));
        }

        return flags.MoveToImmutable();
    }

    private static ImmutableArray<PexObject> ReadObjects(
        ref PexCursor reader,
        PexGameId gameId,
        ImmutableArray<string> strings)
    {
        var count = reader.ReadUInt16("object count");
        reader.ValidateCount(count, 6, "objects");
        var objects = ImmutableArray.CreateBuilder<PexObject>(count);
        for (var i = 0; i < count; i++)
        {
            var name = ReadStringReference(ref reader, strings, "object name");
            var declaredSize = reader.ReadUInt32("object size");
            // Bethesda's compiler includes this four-byte field in the size. Caprica records only
            // the body byte count, while both reference readers otherwise ignore the field. Give
            // the schema reader a window large enough for either convention, then require its
            // actual consumption to match one of those two exact interpretations.
            if (declaredSize < 4)
            {
                throw reader.Invalid($"object size {declaredSize} is smaller than its size field");
            }

            if (declaredSize > int.MaxValue)
            {
                throw reader.Invalid($"object size {declaredSize} exceeds the parser range");
            }

            var bethesdaBodySize = checked((int)declaredSize - sizeof(uint));
            var capricaBodySize = checked((int)declaredSize);
            var parseWindowSize = reader.CanRead(capricaBodySize)
                ? capricaBodySize
                : bethesdaBodySize;
            var body = reader.PeekSubCursor(parseWindowSize, $"object {name.Value} body");
            var bodyStart = body.AbsolutePosition;
            var parsedObject = ReadObjectBody(ref body, gameId, strings, name, declaredSize);
            var consumedBodySize = body.AbsolutePosition - bodyStart;
            if (consumedBodySize != bethesdaBodySize && consumedBodySize != capricaBodySize)
            {
                throw body.Invalid(
                    $"object {name.Value} consumed {consumedBodySize} body bytes; " +
                    $"declared size {declaredSize} permits {bethesdaBodySize} bytes under the " +
                    $"Bethesda convention or {capricaBodySize} bytes under the Caprica convention",
                    bodyStart);
            }

            reader.Skip(consumedBodySize, $"object {name.Value} body");
            objects.Add(parsedObject);
        }

        return objects.MoveToImmutable();
    }

    private static PexObject ReadObjectBody(
        ref PexCursor reader,
        PexGameId gameId,
        ImmutableArray<string> strings,
        PexStringReference name,
        uint declaredSize)
    {
        var modern = gameId != PexGameId.Skyrim;
        var parentName = ReadStringReference(ref reader, strings, "parent class name");
        var documentation = ReadStringReference(ref reader, strings, "object documentation");
        var isConst = modern && reader.ReadByte("object const flag") != 0;
        var userFlags = reader.ReadUInt32("object user flags");
        var autoStateName = ReadStringReference(ref reader, strings, "auto-state name");
        var structs = modern
            ? ReadStructs(ref reader, strings)
            : ImmutableArray<PexStruct>.Empty;
        var variables = ReadVariables(ref reader, gameId, strings);
        var properties = ReadProperties(ref reader, gameId, strings);
        var states = ReadStates(ref reader, gameId, strings);

        return new PexObject(
            name,
            declaredSize,
            parentName,
            documentation,
            isConst,
            userFlags,
            autoStateName,
            structs,
            variables,
            properties,
            states);
    }

    private static ImmutableArray<PexStruct> ReadStructs(
        ref PexCursor reader,
        ImmutableArray<string> strings)
    {
        var count = reader.ReadUInt16("struct count");
        reader.ValidateCount(count, 4, "structs");
        var structs = ImmutableArray.CreateBuilder<PexStruct>(count);
        for (var i = 0; i < count; i++)
        {
            var name = ReadStringReference(ref reader, strings, "struct name");
            var memberCount = reader.ReadUInt16("struct member count");
            reader.ValidateCount(memberCount, 12, "struct members");
            var members = ImmutableArray.CreateBuilder<PexStructMember>(memberCount);
            for (var member = 0; member < memberCount; member++)
            {
                members.Add(new PexStructMember(
                    ReadStringReference(ref reader, strings, "struct-member name"),
                    ReadStringReference(ref reader, strings, "struct-member type"),
                    reader.ReadUInt32("struct-member user flags"),
                    ReadValue(ref reader, strings),
                    reader.ReadByte("struct-member const flag") != 0,
                    ReadStringReference(ref reader, strings, "struct-member documentation")));
            }

            structs.Add(new PexStruct(name, members.MoveToImmutable()));
        }

        return structs.MoveToImmutable();
    }

    private static ImmutableArray<PexVariable> ReadVariables(
        ref PexCursor reader,
        PexGameId gameId,
        ImmutableArray<string> strings)
    {
        var modern = gameId != PexGameId.Skyrim;
        var count = reader.ReadUInt16("variable count");
        reader.ValidateCount(count, modern ? 10 : 9, "variables");
        var variables = ImmutableArray.CreateBuilder<PexVariable>(count);
        for (var i = 0; i < count; i++)
        {
            var name = ReadStringReference(ref reader, strings, "variable name");
            var typeName = ReadStringReference(ref reader, strings, "variable type");
            var userFlags = reader.ReadUInt32("variable user flags");
            var defaultValue = ReadValue(ref reader, strings);
            var isConst = modern && reader.ReadByte("variable const flag") != 0;
            variables.Add(new PexVariable(name, typeName, userFlags, defaultValue, isConst));
        }

        return variables.MoveToImmutable();
    }

    private static ImmutableArray<PexProperty> ReadProperties(
        ref PexCursor reader,
        PexGameId gameId,
        ImmutableArray<string> strings)
    {
        var count = reader.ReadUInt16("property count");
        reader.ValidateCount(count, 11, "properties");
        var properties = ImmutableArray.CreateBuilder<PexProperty>(count);
        for (var i = 0; i < count; i++)
        {
            var name = ReadStringReference(ref reader, strings, "property name");
            var typeName = ReadStringReference(ref reader, strings, "property type");
            var documentation = ReadStringReference(ref reader, strings, "property documentation");
            var userFlags = reader.ReadUInt32("property user flags");
            var rawFlags = reader.ReadByte("property flags");
            if ((rawFlags & ~(byte)(PexPropertyFlags.Readable | PexPropertyFlags.Writable | PexPropertyFlags.Auto)) != 0)
            {
                throw reader.Invalid($"property has unknown flags 0x{rawFlags:X2}");
            }

            var flags = (PexPropertyFlags)rawFlags;
            PexStringReference? autoVariableName = null;
            PexFunction? readFunction = null;
            PexFunction? writeFunction = null;
            if ((flags & PexPropertyFlags.Auto) != 0)
            {
                autoVariableName = ReadStringReference(ref reader, strings, "auto-property variable");
            }
            else
            {
                if ((flags & PexPropertyFlags.Readable) != 0)
                {
                    readFunction = ReadFunction(ref reader, gameId, strings, false);
                }

                if ((flags & PexPropertyFlags.Writable) != 0)
                {
                    writeFunction = ReadFunction(ref reader, gameId, strings, false);
                }
            }

            properties.Add(new PexProperty(
                name,
                typeName,
                documentation,
                userFlags,
                flags,
                autoVariableName,
                readFunction,
                writeFunction));
        }

        return properties.MoveToImmutable();
    }

    private static ImmutableArray<PexState> ReadStates(
        ref PexCursor reader,
        PexGameId gameId,
        ImmutableArray<string> strings)
    {
        var count = reader.ReadUInt16("state count");
        reader.ValidateCount(count, 4, "states");
        var states = ImmutableArray.CreateBuilder<PexState>(count);
        for (var i = 0; i < count; i++)
        {
            var name = ReadStringReference(ref reader, strings, "state name");
            var functionCount = reader.ReadUInt16("state function count");
            reader.ValidateCount(functionCount, 17, "state functions");
            var functions = ImmutableArray.CreateBuilder<PexFunction>(functionCount);
            for (var function = 0; function < functionCount; function++)
            {
                functions.Add(ReadFunction(ref reader, gameId, strings, true));
            }

            states.Add(new PexState(name, functions.MoveToImmutable()));
        }

        return states.MoveToImmutable();
    }

    private static PexFunction ReadFunction(
        ref PexCursor reader,
        PexGameId gameId,
        ImmutableArray<string> strings,
        bool hasName)
    {
        PexStringReference? name = hasName
            ? ReadStringReference(ref reader, strings, "function name")
            : null;
        var returnTypeName = ReadStringReference(ref reader, strings, "function return type");
        var documentation = ReadStringReference(ref reader, strings, "function documentation");
        var userFlags = reader.ReadUInt32("function user flags");
        var rawFlags = reader.ReadByte("function flags");
        if ((rawFlags & ~(byte)(PexFunctionFlags.Global | PexFunctionFlags.Native)) != 0)
        {
            throw reader.Invalid($"function has unknown flags 0x{rawFlags:X2}");
        }

        var parameters = ReadTypedNames(ref reader, strings, "parameter");
        var locals = ReadTypedNames(ref reader, strings, "local variable");
        var instructionCount = reader.ReadUInt16("instruction count");
        reader.ValidateCount(instructionCount, 1, "instructions");
        var instructions = ImmutableArray.CreateBuilder<PexInstruction>(instructionCount);
        for (var i = 0; i < instructionCount; i++)
        {
            instructions.Add(ReadInstruction(ref reader, gameId, strings));
        }

        return new PexFunction(
            name,
            returnTypeName,
            documentation,
            userFlags,
            (PexFunctionFlags)rawFlags,
            parameters,
            locals,
            instructions.MoveToImmutable());
    }

    private static ImmutableArray<PexTypedName> ReadTypedNames(
        ref PexCursor reader,
        ImmutableArray<string> strings,
        string kind)
    {
        var count = reader.ReadUInt16($"{kind} count");
        reader.ValidateCount(count, 4, $"{kind}s");
        var names = ImmutableArray.CreateBuilder<PexTypedName>(count);
        for (var i = 0; i < count; i++)
        {
            names.Add(new PexTypedName(
                ReadStringReference(ref reader, strings, $"{kind} name"),
                ReadStringReference(ref reader, strings, $"{kind} type")));
        }

        return names.MoveToImmutable();
    }

    private static PexInstruction ReadInstruction(
        ref PexCursor reader,
        PexGameId gameId,
        ImmutableArray<string> strings)
    {
        var rawOpcode = reader.ReadByte("opcode");
        var maximum = gameId switch
        {
            PexGameId.Skyrim => (byte)PexOpCode.ArrayRFindElement,
            PexGameId.Fallout4 => (byte)PexOpCode.ArrayClear,
            PexGameId.Fallout76 => (byte)PexOpCode.ArrayGetAllMatchingStructs,
            _ => throw reader.Invalid($"unsupported game identifier {gameId}")
        };
        if (rawOpcode > maximum)
        {
            throw reader.Invalid($"opcode {rawOpcode} is invalid for {gameId}", reader.AbsolutePosition - 1);
        }

        var opcode = (PexOpCode)rawOpcode;
        var argumentCount = GetFixedArgumentCount(opcode);
        reader.ConsumeItems(argumentCount, "instruction arguments");
        var arguments = ImmutableArray.CreateBuilder<PexValue>(argumentCount);
        for (var i = 0; i < argumentCount; i++)
        {
            arguments.Add(ReadValue(ref reader, strings));
        }

        var variadicArguments = ImmutableArray<PexValue>.Empty;
        if (opcode is PexOpCode.CallMethod or PexOpCode.CallParent or PexOpCode.CallStatic)
        {
            var encodedCount = ReadValue(ref reader, strings);
            if (encodedCount is not PexIntegerValue { Value: >= 0 } integerCount)
            {
                throw reader.Invalid("call variadic count must be a non-negative integer value");
            }

            reader.ValidateCount(integerCount.Value, 1, "call variadic arguments");
            var variadic = ImmutableArray.CreateBuilder<PexValue>(integerCount.Value);
            for (var i = 0; i < integerCount.Value; i++)
            {
                variadic.Add(ReadValue(ref reader, strings));
            }

            variadicArguments = variadic.MoveToImmutable();
        }

        return new PexInstruction(opcode, arguments.MoveToImmutable(), variadicArguments);
    }

    private static int GetFixedArgumentCount(PexOpCode opcode) => opcode switch
    {
        PexOpCode.Nop => 0,
        >= PexOpCode.IAdd and <= PexOpCode.IMod => 3,
        >= PexOpCode.Not and <= PexOpCode.Cast => 2,
        >= PexOpCode.CompareEqual and <= PexOpCode.CompareGreaterThanOrEqual => 3,
        PexOpCode.Jump => 1,
        PexOpCode.JumpTrue or PexOpCode.JumpFalse => 2,
        PexOpCode.CallMethod or PexOpCode.CallStatic => 3,
        PexOpCode.CallParent => 2,
        PexOpCode.Return => 1,
        >= PexOpCode.StringConcat and <= PexOpCode.PropertySet => 3,
        PexOpCode.ArrayCreate or PexOpCode.ArrayLength => 2,
        PexOpCode.ArrayGetElement or PexOpCode.ArraySetElement => 3,
        PexOpCode.ArrayFindElement or PexOpCode.ArrayRFindElement => 4,
        PexOpCode.Is => 3,
        PexOpCode.StructCreate => 1,
        PexOpCode.StructGet or PexOpCode.StructSet => 3,
        PexOpCode.ArrayFindStruct or PexOpCode.ArrayRFindStruct => 5,
        PexOpCode.ArrayAdd or PexOpCode.ArrayInsert => 3,
        PexOpCode.ArrayRemoveLast => 1,
        PexOpCode.ArrayRemove => 3,
        PexOpCode.ArrayClear => 1,
        PexOpCode.ArrayGetAllMatchingStructs => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Unknown PEX opcode")
    };

    private static PexValue ReadValue(
        ref PexCursor reader,
        ImmutableArray<string> strings)
    {
        var rawType = reader.ReadByte("value type");
        return (PexValueType)rawType switch
        {
            PexValueType.None => new PexNoneValue(),
            PexValueType.Identifier => new PexIdentifierValue(
                ReadStringReference(ref reader, strings, "identifier value")),
            PexValueType.String => new PexStringValue(
                ReadStringReference(ref reader, strings, "string value")),
            PexValueType.Integer => new PexIntegerValue(reader.ReadInt32("integer value")),
            PexValueType.Float => new PexFloatValue(reader.ReadSingle("float value")),
            PexValueType.Boolean => new PexBooleanValue(reader.ReadByte("boolean value") != 0),
            _ => throw reader.Invalid($"unknown value type {rawType}", reader.AbsolutePosition - 1)
        };
    }

    private static PexStringReference ReadStringReference(
        ref PexCursor reader,
        ImmutableArray<string> strings,
        string field)
    {
        var offset = reader.AbsolutePosition;
        var index = reader.ReadUInt16(field);
        if (index >= strings.Length)
        {
            throw reader.Invalid(
                $"{field} string index {index} is outside the {strings.Length}-entry table",
                offset);
        }

        return new PexStringReference(index, strings[index]);
    }

    private sealed class PexParseBudget
    {
        private readonly int _maximumItems;
        private int _remainingItems;

        public PexParseBudget(int maximumItems)
        {
            _maximumItems = maximumItems;
            _remainingItems = maximumItems;
        }

        public void Consume(int count, string field, int offset)
        {
            if (count < 0 || count > _remainingItems)
            {
                throw new PexParseException(
                    $"{field} count {count} exceeds the cumulative " +
                    $"{_maximumItems}-item parse safety budget",
                    offset);
            }

            _remainingItems -= count;
        }
    }

    private ref struct PexCursor
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly PexEndianness _endianness;
        private readonly PexParseBudget _budget;
        private readonly int _baseOffset;
        private int _position;

        public PexCursor(
            ReadOnlySpan<byte> data,
            PexEndianness endianness,
            PexParseBudget budget,
            int baseOffset = 0)
        {
            _data = data;
            _endianness = endianness;
            _budget = budget;
            _baseOffset = baseOffset;
            _position = 0;
        }

        public readonly int AbsolutePosition => _baseOffset + _position;
        private readonly int Remaining => _data.Length - _position;

        public void Skip(int count, string field)
        {
            Ensure(count, field);
            _position += count;
        }

        public byte ReadByte(string field)
        {
            Ensure(1, field);
            return _data[_position++];
        }

        public ushort ReadUInt16(string field)
        {
            Ensure(2, field);
            var bytes = _data.Slice(_position, 2);
            _position += 2;
            return _endianness == PexEndianness.Little
                ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
                : BinaryPrimitives.ReadUInt16BigEndian(bytes);
        }

        public uint ReadUInt32(string field)
        {
            Ensure(4, field);
            var bytes = _data.Slice(_position, 4);
            _position += 4;
            return _endianness == PexEndianness.Little
                ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
                : BinaryPrimitives.ReadUInt32BigEndian(bytes);
        }

        public int ReadInt32(string field) => unchecked((int)ReadUInt32(field));

        public long ReadInt64(string field)
        {
            Ensure(8, field);
            var bytes = _data.Slice(_position, 8);
            _position += 8;
            return _endianness == PexEndianness.Little
                ? BinaryPrimitives.ReadInt64LittleEndian(bytes)
                : BinaryPrimitives.ReadInt64BigEndian(bytes);
        }

        public float ReadSingle(string field)
            => BitConverter.Int32BitsToSingle(ReadInt32(field));

        public string ReadString(Encoding encoding, string field)
        {
            var length = ReadUInt16($"{field} length");
            Ensure(length, field);
            var offset = AbsolutePosition;
            var bytes = _data.Slice(_position, length);
            _position += length;
            try
            {
                return encoding.GetString(bytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw Invalid($"{field} is not valid UTF-8", offset, ex);
            }
        }

        public readonly bool CanRead(int count) => count >= 0 && count <= Remaining;

        public readonly PexCursor PeekSubCursor(int length, string field)
        {
            Ensure(length, field);
            return new PexCursor(
                _data.Slice(_position, length),
                _endianness,
                _budget,
                AbsolutePosition);
        }

        public void ValidateCount(int count, int minimumItemBytes, string field)
        {
            if (count < 0 || minimumItemBytes <= 0 || count > Remaining / minimumItemBytes)
            {
                throw Invalid(
                    $"{field} count {count} cannot fit in the {Remaining} bytes remaining");
            }

            ConsumeItems(count, field);
        }

        public readonly void ConsumeItems(int count, string field)
            => _budget.Consume(count, field, AbsolutePosition);

        public readonly void RequireEnd(string field)
        {
            if (Remaining != 0)
            {
                throw Invalid($"{field} has {Remaining} unconsumed trailing bytes");
            }
        }

        public readonly PexParseException Invalid(
            string message,
            int? offset = null,
            Exception? innerException = null)
            => new(message, offset ?? AbsolutePosition, innerException);

        private readonly void Ensure(int count, string field)
        {
            if (count < 0 || count > Remaining)
            {
                throw Invalid($"truncated {field}: need {count} bytes, have {Remaining}");
            }
        }
    }
}
