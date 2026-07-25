using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class TruncatedSlsdParserTests
{
    private const uint FormId = 0x01000A00;
    private static readonly byte[] MinimalCompiledScript = [0x1D, 0x00, 0x00, 0x00];

    public static TheoryData<string, int> TruncatedSlsdCases => new()
    {
        { "SCPT", 16 },
        { "SCPT", 17 },
        { "SCPT", 23 },
        { "INFO", 16 },
        { "INFO", 17 },
        { "INFO", 23 },
        { "PACK", 16 },
        { "PACK", 17 },
        { "PACK", 23 },
        { "TERM", 16 },
        { "TERM", 17 },
        { "TERM", 23 }
    };

    public static TheoryData<string, string, uint?> MalformedSerializedLocalCases
    {
        get
        {
            var cases = new TheoryData<string, string, uint?>();
            foreach (var ownerType in new[] { "SCPT", "INFO", "PACK", "TERM" })
            {
                cases.Add(ownerType, "orphan-slsd", 10);
                cases.Add(ownerType, "trailing-slsd", 10);
                cases.Add(ownerType, "nonadjacent-slsd", null);
                cases.Add(ownerType, "orphan-scvr", null);
                cases.Add(ownerType, "zero-id", null);
                cases.Add(ownerType, "duplicate-id", 9);
                cases.Add(ownerType, "invalid-raw-type", null);
                cases.Add(ownerType, "empty-name", null);
                cases.Add(ownerType, "whitespace-name", null);
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(TruncatedSlsdCases))]
    public void ParseScriptOwner_TruncatedSlsdFailsClosedWithoutInventingVariable(
        string ownerType,
        int payloadLength)
    {
        var slsd = new byte[payloadLength];
        BinaryPrimitives.WriteUInt32LittleEndian(slsd, 9);
        if (payloadLength > 16)
        {
            slsd[16] = 1;
        }

        var subrecords = OwnerPrefix(ownerType);
        subrecords.Add(("SCHR", new byte[20]));
        subrecords.Add(("SLSD", slsd));
        subrecords.Add(("SCVR", NullTermString("mustNotBeRecovered")));

        var recordBytes = BuildRecordBytes(FormId, ownerType, false, [.. subrecords]);
        var mainRecord = new DetectedMainRecord(
            ownerType,
            (uint)(recordBytes.Length - 24),
            0,
            FormId,
            0,
            false);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);
        var parser = new RecordParser(
            MakeScanResult([mainRecord]),
            accessor: accessor,
            fileSize: recordBytes.Length);

        switch (ownerType)
        {
            case "SCPT":
            {
                var script = Assert.Single(parser.ParseScripts());
                Assert.True(script.HasMalformedSerializedTable);
                Assert.True(script.IsIncompleteExecutableBundle);
                Assert.Empty(script.Variables);
                break;
            }
            case "INFO":
            {
                var script = Assert.Single(Assert.Single(parser.ParseDialogue()).ResultScripts);
                Assert.True(script.IsIncompleteExecutableBundle);
                Assert.Empty(script.Variables);
                break;
            }
            case "PACK":
            {
                var package = Assert.Single(parser.ParsePackages());
                Assert.NotNull(package.OnBegin);
                var script = Assert.Single(package.OnBegin!.Scripts);
                Assert.True(script.IsIncompleteExecutableBundle);
                Assert.Empty(script.Variables);
                break;
            }
            case "TERM":
            {
                var item = Assert.Single(Assert.Single(parser.ParseTerminals()).MenuItems);
                Assert.True(item.IsIncompleteExecutableBundle);
                Assert.Empty(item.Variables);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(ownerType), ownerType, null);
        }
    }

    [Theory]
    [MemberData(nameof(MalformedSerializedLocalCases))]
    public void ParseScriptOwner_MalformedSerializedLocalFailsClosedWithoutNormalizingEntry(
        string ownerType,
        string malformedCase,
        uint? expectedSurvivingVariableId)
    {
        var subrecords = OwnerPrefix(ownerType);
        var header = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)MinimalCompiledScript.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(12),
            expectedSurvivingVariableId.HasValue ? 1u : 0u);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(18), 1);
        subrecords.Add(("SCHR", header));
        subrecords.Add(("SCDA", MinimalCompiledScript));
        subrecords.AddRange(MalformedLocalSubrecords(malformedCase));

        using var fixture = CreateParser(ownerType, subrecords);
        var parser = fixture.Parser;
        IReadOnlyList<ScriptVariableInfo> variables;
        switch (ownerType)
        {
            case "SCPT":
            {
                var script = Assert.Single(parser.ParseScripts());
                Assert.True(script.HasMalformedSerializedTable);
                Assert.True(script.IsIncompleteExecutableBundle);
                variables = script.Variables;
                break;
            }
            case "INFO":
            {
                var script = Assert.Single(Assert.Single(parser.ParseDialogue()).ResultScripts);
                Assert.True(script.IsIncompleteExecutableBundle);
                variables = script.Variables;
                break;
            }
            case "PACK":
            {
                var script = Assert.Single(Assert.Single(parser.ParsePackages()).OnBegin!.Scripts);
                Assert.True(script.IsIncompleteExecutableBundle);
                variables = script.Variables;
                break;
            }
            case "TERM":
            {
                var item = Assert.Single(Assert.Single(parser.ParseTerminals()).MenuItems);
                Assert.True(item.IsIncompleteExecutableBundle);
                variables = item.Variables;
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(ownerType), ownerType, null);
        }

        if (expectedSurvivingVariableId.HasValue)
        {
            var variable = Assert.Single(variables);
            Assert.Equal(expectedSurvivingVariableId.Value, variable.Index);
            Assert.Equal($"valid{expectedSurvivingVariableId.Value}", variable.Name);
        }
        else
        {
            Assert.Empty(variables);
        }
    }

    private static List<(string Signature, byte[] Data)> MalformedLocalSubrecords(string malformedCase)
    {
        return malformedCase switch
        {
            "orphan-slsd" =>
            [
                ("SLSD", ScriptLocal(9, 0)),
                ("SLSD", ScriptLocal(10, 1)),
                ("SCVR", NullTermString("valid10"))
            ],
            "trailing-slsd" =>
            [
                ("SLSD", ScriptLocal(10, 1)),
                ("SCVR", NullTermString("valid10")),
                ("SLSD", ScriptLocal(9, 0))
            ],
            "nonadjacent-slsd" =>
            [
                ("SLSD", ScriptLocal(9, 0)),
                ("XXXX", []),
                ("SCVR", NullTermString("mustNotBeRecovered"))
            ],
            "orphan-scvr" => [("SCVR", NullTermString("mustNotBeRecovered"))],
            "zero-id" =>
            [
                ("SLSD", ScriptLocal(0, 0)),
                ("SCVR", NullTermString("mustNotBeRecovered"))
            ],
            "duplicate-id" =>
            [
                ("SLSD", ScriptLocal(9, 0)),
                ("SCVR", NullTermString("valid9")),
                ("SLSD", ScriptLocal(9, 1)),
                ("SCVR", NullTermString("mustNotBeRecovered"))
            ],
            "invalid-raw-type" =>
            [
                ("SLSD", ScriptLocal(9, 2)),
                ("SCVR", NullTermString("mustNotBeRecovered"))
            ],
            "empty-name" =>
            [
                ("SLSD", ScriptLocal(9, 0)),
                ("SCVR", NullTermString(""))
            ],
            "whitespace-name" =>
            [
                ("SLSD", ScriptLocal(9, 0)),
                ("SCVR", NullTermString("   "))
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(malformedCase), malformedCase, null)
        };
    }

    private static byte[] ScriptLocal(uint variableId, byte rawType)
    {
        var data = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(data, variableId);
        data[16] = rawType;
        return data;
    }

    private static ParserFixture CreateParser(
        string ownerType,
        List<(string Signature, byte[] Data)> subrecords)
    {
        var recordBytes = BuildRecordBytes(FormId, ownerType, false, [.. subrecords]);
        var mainRecord = new DetectedMainRecord(
            ownerType,
            (uint)(recordBytes.Length - 24),
            0,
            FormId,
            0,
            false);

        return new ParserFixture(recordBytes, mainRecord);
    }

    private static List<(string Signature, byte[] Data)> OwnerPrefix(string ownerType)
    {
        return ownerType switch
        {
            "SCPT" => [("EDID", NullTermString("TruncatedLocalScript"))],
            "INFO" =>
            [
                ("EDID", NullTermString("TruncatedLocalInfo")),
                ("TRDT", new byte[24]),
                ("NAM1", NullTermString("Test response"))
            ],
            "PACK" =>
            [
                ("EDID", NullTermString("TruncatedLocalPackage")),
                ("POBA", [])
            ],
            "TERM" =>
            [
                ("EDID", NullTermString("TruncatedLocalTerminal")),
                ("ITXT", NullTermString("Run test"))
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(ownerType), ownerType, null)
        };
    }

    private sealed class ParserFixture : IDisposable
    {
        private readonly MemoryMappedViewAccessor _accessor;
        private readonly MemoryMappedFile _mmf;

        internal ParserFixture(byte[] recordBytes, DetectedMainRecord mainRecord)
        {
            _mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
            _accessor = _mmf.CreateViewAccessor(0, recordBytes.Length);
            _accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);
            Parser = new RecordParser(
                MakeScanResult([mainRecord]),
                accessor: _accessor,
                fileSize: recordBytes.Length);
        }

        internal RecordParser Parser { get; }

        public void Dispose()
        {
            _accessor.Dispose();
            _mmf.Dispose();
        }
    }
}