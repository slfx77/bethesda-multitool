using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Papyrus;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Papyrus;

public sealed class PexParserTests
{
    private static readonly string[] Strings =
    [
        "", "ExampleScript", "ParentScript", "Documentation", "Auto", "Int", "Value", "OnInit",
        "None", "::temp", "Hello", "Hidden", "MyStruct", "Member", "Group", "AutoProperty",
        "Bool", "SomeType", "SomeMethod", "Result"
    ];

    [Fact]
    public void Parse_Skyrim31BigEndian_PreservesLegacyLayoutAndInstructions()
    {
        var bytes = BuildFixture(PexGameId.Skyrim, 1);

        var file = PexParser.Parse(bytes);

        Assert.Equal(PexEndianness.Big, file.Header.Endianness);
        Assert.Equal((byte)3, file.Header.MajorVersion);
        Assert.Equal((byte)1, file.Header.MinorVersion);
        Assert.Equal(PexGameId.Skyrim, file.Header.GameId);
        Assert.Equal(bytes.Length, file.BytesConsumed);
        Assert.Equal(Strings, file.StringTable);

        var debug = Assert.IsType<PexDebugInfo>(file.DebugInfo);
        Assert.Single(debug.Functions);
        Assert.Empty(debug.PropertyGroups);
        Assert.Empty(debug.StructOrders);
        Assert.Equal((ushort)123, Assert.Single(debug.Functions[0].InstructionLineMap));

        var obj = Assert.Single(file.Objects);
        Assert.False(obj.IsConst);
        Assert.Empty(obj.Structs);
        var variable = Assert.Single(obj.Variables);
        Assert.False(variable.IsConst);
        Assert.Equal(42, Assert.IsType<PexIntegerValue>(variable.DefaultValue).Value);

        var function = Assert.Single(Assert.Single(obj.States).Functions);
        Assert.Equal("OnInit", function.Name?.Value);
        Assert.Equal(PexOpCode.IAdd, function.Instructions[0].OpCode);
        Assert.Equal(3, function.Instructions[0].Arguments.Length);
        Assert.Equal(PexOpCode.Return, function.Instructions[1].OpCode);
    }

    [Fact]
    public void Parse_Fallout4LittleEndian_ReadsStructsConstsAndExtendedDebugInfo()
    {
        var bytes = BuildFixture(PexGameId.Fallout4, 9);

        var file = PexParser.Parse(bytes);

        Assert.Equal(PexEndianness.Little, file.Header.Endianness);
        Assert.Equal(PexGameId.Fallout4, file.Header.GameId);
        var debug = Assert.IsType<PexDebugInfo>(file.DebugInfo);
        Assert.Single(debug.PropertyGroups);
        Assert.Equal("AutoProperty", Assert.Single(debug.PropertyGroups[0].Properties).Value);
        Assert.Single(debug.StructOrders);

        var obj = Assert.Single(file.Objects);
        Assert.True(obj.IsConst);
        var member = Assert.Single(Assert.Single(obj.Structs).Members);
        Assert.True(member.IsConst);
        Assert.Equal("Member", member.Name.Value);
        Assert.True(Assert.Single(obj.Variables).IsConst);
        var property = Assert.Single(obj.Properties);
        Assert.Equal(
            PexPropertyFlags.Readable | PexPropertyFlags.Writable | PexPropertyFlags.Auto,
            property.Flags);
        Assert.Equal("Value", property.AutoVariableName?.Value);
        Assert.Null(property.ReadFunction);

        var instruction = Assert.Single(Assert.Single(Assert.Single(obj.States).Functions).Instructions);
        Assert.Equal(PexOpCode.ArrayClear, instruction.OpCode);
    }

    [Fact]
    public void Parse_Fallout7615_ReadsOpcodeTailAndCallVariadicArguments()
    {
        var bytes = BuildFixture(PexGameId.Fallout76, 15);

        var file = PexParser.Parse(bytes);

        Assert.Equal(PexGameId.Fallout76, file.Header.GameId);
        var instructions = Assert.Single(Assert.Single(Assert.Single(file.Objects).States).Functions).Instructions;
        Assert.Equal(3, instructions.Length);
        Assert.Equal(PexOpCode.ArrayGetAllMatchingStructs, instructions[0].OpCode);
        Assert.Equal(6, instructions[0].Arguments.Length);
        Assert.Equal(PexOpCode.CallStatic, instructions[1].OpCode);
        Assert.Equal(3, instructions[1].Arguments.Length);
        Assert.Collection(
            instructions[1].VariadicArguments,
            value => Assert.Equal("Hello", Assert.IsType<PexStringValue>(value).String.Value),
            value => Assert.True(Assert.IsType<PexBooleanValue>(value).Value));
    }

    [Fact]
    public void Parse_Fallout7615_PreservesObjectTailAndUnmappedFunctionFlagBits()
    {
        var bytes = BuildFixture(
            PexGameId.Fallout76,
            15,
            functionFlags: 0x28,
            stateNameIndex: 4,
            trailingStringReferences: [4]);

        var file = PexParser.Parse(bytes);

        var obj = Assert.Single(file.Objects);
        var trailingReference = Assert.Single(obj.Fallout76TrailingStateReferences);
        Assert.Equal((ushort)4, trailingReference.Index);
        Assert.Equal("Auto", trailingReference.Value);
        Assert.True(obj.HasFallout76TrailingStateReferenceTable);
        Assert.Equal("Auto", Assert.Single(obj.States).Name.Value);
        var function = Assert.Single(Assert.Single(obj.States).Functions);
        Assert.Equal((byte)0x28, function.RawFlags);
        Assert.Equal((byte)0x28, function.UnmappedFlags);
        Assert.False(function.Flags.HasFlag(PexFunctionFlags.Global));
        Assert.False(function.Flags.HasFlag(PexFunctionFlags.Native));
        Assert.Equal(bytes.Length, file.BytesConsumed);
    }

    [Theory]
    [InlineData(PexGameId.Skyrim, (byte)2, (byte)0xFF)]
    [InlineData(PexGameId.Fallout4, (byte)9, (byte)0x80)]
    [InlineData(PexGameId.Fallout76, (byte)15, (byte)0x40)]
    [InlineData(PexGameId.Fallout76, (byte)15, (byte)0xFF)]
    public void Parse_FunctionFlags_PreservesCompleteByteWithoutInventingSemantics(
        PexGameId gameId,
        byte minorVersion,
        byte rawFlags)
    {
        var file = PexParser.Parse(BuildFixture(
            gameId,
            minorVersion,
            functionFlags: rawFlags));

        var function = Assert.Single(Assert.Single(Assert.Single(file.Objects).States).Functions);
        Assert.Equal(rawFlags, function.RawFlags);
        Assert.Equal((byte)(rawFlags & 0xFC), function.UnmappedFlags);
    }

    [Fact]
    public void Parse_Fallout76CapricaSizedObjectWithoutRetailTail_IsAccepted()
    {
        var bytes = BuildFixture(
            PexGameId.Fallout76,
            15,
            capricaObjectSize: true,
            omitFallout76Tail: true);

        var file = PexParser.Parse(bytes);

        var obj = Assert.Single(file.Objects);
        Assert.False(obj.HasFallout76TrailingStateReferenceTable);
        Assert.Empty(obj.Fallout76TrailingStateReferences);
        Assert.Equal(bytes.Length, file.BytesConsumed);
    }

    [Fact]
    public void Parse_TwoFallout76ObjectsWithoutTails_DoesNotReadNextObjectAsTail()
    {
        var bytes = BuildFixture(
            PexGameId.Fallout76,
            15,
            omitFallout76Tail: true,
            objectCount: 2);

        var file = PexParser.Parse(bytes);

        Assert.Equal(2, file.Objects.Length);
        Assert.All(file.Objects, obj =>
        {
            Assert.False(obj.HasFallout76TrailingStateReferenceTable);
            Assert.Empty(obj.Fallout76TrailingStateReferences);
        });
        Assert.Equal(bytes.Length, file.BytesConsumed);
    }

    [Fact]
    public void Parse_Fallout76ExplicitEmptyTail_RemainsDistinctFromOmittedTail()
    {
        var file = PexParser.Parse(BuildFixture(PexGameId.Fallout76, 15));

        var obj = Assert.Single(file.Objects);
        Assert.True(obj.HasFallout76TrailingStateReferenceTable);
        Assert.Empty(obj.Fallout76TrailingStateReferences);
    }

    [Fact]
    public void Parse_Fallout76ObjectTailWithOutOfRangeStringReference_IsRejected()
    {
        var bytes = BuildFixture(
            PexGameId.Fallout76,
            15,
            stateNameIndex: 4,
            trailingStringReferences: [ushort.MaxValue]);

        var exception = Assert.Throws<PexParseException>(() => PexParser.Parse(bytes));

        Assert.Contains("object-tail string reference string index 65535", exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_TruncatedFallout76ObjectTail_IsRejectedWithinObjectBoundary()
    {
        var bytes = BuildFixture(
            PexGameId.Fallout76,
            15,
            stateNameIndex: 4,
            trailingStringReferences: [4]);

        var exception = Assert.Throws<PexParseException>(() =>
            PexParser.Parse(bytes.AsMemory(0, bytes.Length - 1)));

        Assert.Contains("object ExampleScript body", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_TamperedFallout76ObjectSizeCannotHidePartOfTail()
    {
        var bytes = BuildFixture(
            PexGameId.Fallout76,
            15,
            objectSizeAdjustment: -2,
            stateNameIndex: 4,
            trailingStringReferences: [4]);

        var exception = Assert.Throws<PexParseException>(() => PexParser.Parse(bytes));

        Assert.Contains("Fallout 76 object-tail string references", exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_EveryTruncatedPrefix_IsRejectedWithoutReadingPastEnd()
    {
        var bytes = BuildFixture(PexGameId.Fallout76, 15);

        for (var length = 0; length < bytes.Length; length++)
        {
            var exception = Record.Exception(() => PexParser.Parse(bytes.AsMemory(0, length)));
            Assert.IsType<PexParseException>(exception);
        }
    }

    [Fact]
    public void Parse_OutOfRangeStringReference_IsRejectedAtReference()
    {
        var bytes = BuildFixture(PexGameId.Fallout4, 9, true);

        var exception = Assert.Throws<PexParseException>(() => PexParser.Parse(bytes));

        Assert.Contains("string index 65535", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ObjectSizeThatExceedsRemainingBytes_IsRejected()
    {
        var bytes = BuildFixture(PexGameId.Fallout4, 9, objectSizeAdjustment: 1);

        var exception = Assert.Throws<PexParseException>(() => PexParser.Parse(bytes));

        Assert.Contains("object ExampleScript body", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_CapricaBodyByteObjectSize_IsAccepted()
    {
        // Caprica's PexWriter::beginObject/endObject writes the byte count after the size field,
        // unlike Bethesda's compiler, which includes the four-byte field in the declared size.
        var bytes = BuildFixture(PexGameId.Fallout4, 9, capricaObjectSize: true);

        var file = PexParser.Parse(bytes);

        var obj = Assert.Single(file.Objects);
        Assert.Equal("ExampleScript", obj.Name.Value);
        Assert.Equal(bytes.Length, file.BytesConsumed);
    }

    [Fact]
    public void Parse_NegativeCallVariadicCount_IsRejectedBeforeAllocation()
    {
        var bytes = BuildFixture(PexGameId.Fallout76, 15, negativeVariadicCount: true);

        var exception = Assert.Throws<PexParseException>(() => PexParser.Parse(bytes));

        Assert.Contains("non-negative integer", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_CumulativeItemBudgetRejectsVariadicExpansionBeforeAllocation()
    {
        // The count itself is at the ceiling, but earlier string/debug/object collections have
        // already consumed budget. Supplying enough one-byte None values proves the wire-size
        // check passes and rejection comes from the parser-wide cumulative budget.
        var bytes = BuildFixture(
            PexGameId.Fallout76,
            15,
            variadicCountOverride: PexParser.MaximumParsedItems);

        var exception = Assert.Throws<PexParseException>(() => PexParser.Parse(bytes));

        Assert.Contains("cumulative", exception.Message, StringComparison.Ordinal);
        Assert.Contains("parse safety budget", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_GameAndVersionMismatch_IsRejected()
    {
        var bytes = BuildFixture(PexGameId.Fallout4, 9);
        bytes[5] = 15;

        var exception = Assert.Throws<PexParseException>(() => PexParser.Parse(bytes));

        Assert.Contains("unsupported Fallout4 PEX variant v3.15", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ForgedHeaderStringLength_IsRejected()
    {
        var bytes = BuildFixture(PexGameId.Skyrim, 2);
        // Header is magic(4), version(2), game(2), time(8), then a big-endian u16 source length.
        bytes[16] = 0xFF;
        bytes[17] = 0xFF;

        var exception = Assert.Throws<PexParseException>(() => PexParser.Parse(bytes));

        Assert.Contains("truncated source file name", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"E:\SteamLibrary\SteamApps\common\Skyrim Special Edition\Data\Scripts\actor.pex", PexGameId.Skyrim)]
    [InlineData(@"E:\SteamLibrary\SteamApps\common\Fallout 4\Data\Scripts\Actor.pex", PexGameId.Fallout4)]
    [InlineData(@"E:\SteamLibrary\SteamApps\common\Fallout 4\Data\Scripts\ObjectReference.pex", PexGameId.Fallout4)]
    public void Parse_InstalledBethesdaFixture_ConsumesCompleteFile(string path, PexGameId expectedGame)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(File.Exists(path), $"Installed PEX fixture not available: {path}");

        var file = PexParser.Parse(path);

        Assert.Equal(expectedGame, file.Header.GameId);
        Assert.EndsWith(".psc", file.Header.SourceFileName, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(file.StringTable);
        Assert.NotEmpty(file.Objects);
        Assert.Equal(new FileInfo(path).Length, file.BytesConsumed);
    }

    [Theory]
    [InlineData(@"E:\SteamLibrary\SteamApps\common\Skyrim Special Edition\Data\Scripts", PexGameId.Skyrim)]
    [InlineData(@"E:\SteamLibrary\SteamApps\common\Fallout 4\Data\Scripts", PexGameId.Fallout4)]
    public void Parse_AllInstalledLooseBethesdaScripts(string directory, PexGameId expectedGame)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(Directory.Exists(directory), $"Installed script directory not available: {directory}");
        var paths = Directory.GetFiles(directory, "*.pex", SearchOption.AllDirectories);
        Assert.SkipWhen(paths.Length == 0, $"No installed PEX fixtures found under: {directory}");

        foreach (var path in paths)
        {
            var file = PexParser.Parse(path);
            Assert.Equal(expectedGame, file.Header.GameId);
            Assert.Equal(new FileInfo(path).Length, file.BytesConsumed);
        }
    }

    internal static byte[] BuildFixture(
        PexGameId gameId,
        byte minorVersion,
        bool invalidObjectName = false,
        int objectSizeAdjustment = 0,
        bool negativeVariadicCount = false,
        bool capricaObjectSize = false,
        int? variadicCountOverride = null,
        byte functionFlags = 0,
        ushort stateNameIndex = 0,
        ushort[]? trailingStringReferences = null,
        bool omitFallout76Tail = false,
        ushort objectCount = 1)
    {
        var bigEndian = gameId == PexGameId.Skyrim;
        using var writer = new EndianWriter(bigEndian);
        writer.WriteBytes(bigEndian ? [0xFA, 0x57, 0xC0, 0xDE] : [0xDE, 0xC0, 0x57, 0xFA]);
        writer.WriteByte(3);
        writer.WriteByte(minorVersion);
        writer.WriteUInt16((ushort)gameId);
        writer.WriteInt64(1_700_000_000);
        writer.WriteString("Example.psc");
        writer.WriteString("UnitTest");
        writer.WriteString("TestHost");

        writer.WriteUInt16((ushort)Strings.Length);
        foreach (var value in Strings)
        {
            writer.WriteString(value);
        }

        writer.WriteByte(1); // has debug info
        writer.WriteInt64(1_700_000_001);
        writer.WriteUInt16(1); // debug functions
        writer.WriteUInt16(1); // object name
        writer.WriteUInt16(0); // state name
        writer.WriteUInt16(7); // function name
        writer.WriteByte(0); // normal function
        writer.WriteUInt16(1);
        writer.WriteUInt16(123);
        if (!bigEndian)
        {
            writer.WriteUInt16(1); // property groups
            writer.WriteUInt16(1);
            writer.WriteUInt16(14);
            writer.WriteUInt16(3);
            writer.WriteUInt32(4);
            writer.WriteUInt16(1);
            writer.WriteUInt16(15);

            writer.WriteUInt16(1); // struct orders
            writer.WriteUInt16(1);
            writer.WriteUInt16(12);
            writer.WriteUInt16(1);
            writer.WriteUInt16(13);
        }

        writer.WriteUInt16(1); // user flags
        writer.WriteUInt16(11);
        writer.WriteByte(0);

        writer.WriteUInt16(objectCount);
        for (var objectIndex = 0; objectIndex < objectCount; objectIndex++)
        {
            writer.WriteUInt16(invalidObjectName && objectIndex == 0 ? ushort.MaxValue : (ushort)1);
            using var body = new EndianWriter(bigEndian);
            WriteObjectBody(
                body,
                gameId,
                negativeVariadicCount,
                variadicCountOverride,
                functionFlags,
                stateNameIndex,
                trailingStringReferences,
                omitFallout76Tail);
            var bodyBytes = body.ToArray();
            var declaredObjectSize = bodyBytes.Length + (capricaObjectSize ? 0 : sizeof(uint));
            writer.WriteUInt32(checked((uint)(declaredObjectSize + objectSizeAdjustment)));
            writer.WriteBytes(bodyBytes);
        }

        return writer.ToArray();
    }

    private static void WriteObjectBody(
        EndianWriter writer,
        PexGameId gameId,
        bool negativeVariadicCount,
        int? variadicCountOverride,
        byte functionFlags,
        ushort stateNameIndex,
        ushort[]? trailingStringReferences,
        bool omitFallout76Tail)
    {
        var modern = gameId != PexGameId.Skyrim;
        writer.WriteUInt16(2); // parent
        writer.WriteUInt16(3); // documentation
        if (modern)
        {
            writer.WriteByte(1); // const
        }

        writer.WriteUInt32(1); // user flags
        writer.WriteUInt16(4); // auto state

        if (modern)
        {
            writer.WriteUInt16(1); // structs
            writer.WriteUInt16(12); // struct name
            writer.WriteUInt16(1); // members
            writer.WriteUInt16(13); // member name
            writer.WriteUInt16(5); // type
            writer.WriteUInt32(0);
            WriteIntegerValue(writer, 99);
            writer.WriteByte(1); // const
            writer.WriteUInt16(3); // docs
        }

        writer.WriteUInt16(1); // variables
        writer.WriteUInt16(6); // name
        writer.WriteUInt16(5); // type
        writer.WriteUInt32(1); // flags
        WriteIntegerValue(writer, 42);
        if (modern)
        {
            writer.WriteByte(1); // const
        }

        if (modern)
        {
            writer.WriteUInt16(1); // properties
            writer.WriteUInt16(15); // name
            writer.WriteUInt16(16); // type
            writer.WriteUInt16(3); // docs
            writer.WriteUInt32(0);
            writer.WriteByte(7); // read + write + auto
            writer.WriteUInt16(6); // auto variable
        }
        else
        {
            writer.WriteUInt16(0); // properties
        }

        writer.WriteUInt16(1); // states
        writer.WriteUInt16(stateNameIndex);
        writer.WriteUInt16(1); // functions
        writer.WriteUInt16(7); // function name
        writer.WriteUInt16(8); // return type
        writer.WriteUInt16(3); // docs
        writer.WriteUInt32(0); // user flags
        writer.WriteByte(functionFlags);
        writer.WriteUInt16(0); // params
        writer.WriteUInt16(1); // locals
        writer.WriteUInt16(9);
        writer.WriteUInt16(5);

        switch (gameId)
        {
            case PexGameId.Skyrim:
                writer.WriteUInt16(2);
                writer.WriteByte((byte)PexOpCode.IAdd);
                WriteIdentifierValue(writer, 9);
                WriteIntegerValue(writer, 1);
                WriteIntegerValue(writer, 2);
                writer.WriteByte((byte)PexOpCode.Return);
                writer.WriteByte((byte)PexValueType.None);
                break;

            case PexGameId.Fallout4:
                writer.WriteUInt16(1);
                writer.WriteByte((byte)PexOpCode.ArrayClear);
                WriteIdentifierValue(writer, 9);
                break;

            case PexGameId.Fallout76:
                writer.WriteUInt16(3);
                writer.WriteByte((byte)PexOpCode.ArrayGetAllMatchingStructs);
                for (var i = 0; i < 6; i++)
                {
                    WriteIdentifierValue(writer, 9);
                }

                writer.WriteByte((byte)PexOpCode.CallStatic);
                WriteStringValue(writer, 17);
                WriteStringValue(writer, 18);
                WriteIdentifierValue(writer, 19);
                var variadicCount = negativeVariadicCount
                    ? -1
                    : variadicCountOverride ?? 2;
                WriteIntegerValue(writer, variadicCount);
                if (variadicCountOverride is int expandedCount)
                {
                    writer.WriteBytes(new byte[expandedCount]);
                }
                else if (!negativeVariadicCount)
                {
                    WriteStringValue(writer, 10);
                    writer.WriteByte((byte)PexValueType.Boolean);
                    writer.WriteByte(1);
                }

                writer.WriteByte((byte)PexOpCode.Return);
                writer.WriteByte((byte)PexValueType.None);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(gameId));
        }

        if (gameId == PexGameId.Fallout76 && !omitFallout76Tail)
        {
            var references = trailingStringReferences ?? [];
            writer.WriteUInt16(checked((ushort)references.Length));
            foreach (var reference in references)
            {
                writer.WriteUInt16(reference);
            }
        }
    }

    private static void WriteIdentifierValue(EndianWriter writer, ushort index)
    {
        writer.WriteByte((byte)PexValueType.Identifier);
        writer.WriteUInt16(index);
    }

    private static void WriteStringValue(EndianWriter writer, ushort index)
    {
        writer.WriteByte((byte)PexValueType.String);
        writer.WriteUInt16(index);
    }

    private static void WriteIntegerValue(EndianWriter writer, int value)
    {
        writer.WriteByte((byte)PexValueType.Integer);
        writer.WriteUInt32(unchecked((uint)value));
    }

    private sealed class EndianWriter(bool bigEndian) : IDisposable
    {
        private readonly MemoryStream _stream = new();

        public void Dispose()
        {
            _stream.Dispose();
        }

        public void WriteByte(byte value)
        {
            _stream.WriteByte(value);
        }

        public void WriteBytes(ReadOnlySpan<byte> value)
        {
            _stream.Write(value);
        }

        public void WriteUInt16(ushort value)
        {
            Span<byte> bytes = stackalloc byte[2];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
            }

            _stream.Write(bytes);
        }

        public void WriteUInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            }

            _stream.Write(bytes);
        }

        public void WriteInt64(long value)
        {
            Span<byte> bytes = stackalloc byte[8];
            if (bigEndian)
            {
                BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            }
            else
            {
                BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            }

            _stream.Write(bytes);
        }

        public void WriteString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteUInt16(checked((ushort)bytes.Length));
            WriteBytes(bytes);
        }

        public byte[] ToArray()
        {
            return _stream.ToArray();
        }
    }
}
