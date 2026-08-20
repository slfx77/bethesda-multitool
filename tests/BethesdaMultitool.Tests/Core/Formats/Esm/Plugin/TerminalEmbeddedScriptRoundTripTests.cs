using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public sealed class TerminalEmbeddedScriptRoundTripTests
{
    private const uint TermFormId = 0x01000700;
    private const uint FirstObjectRef = 0x00123456;
    private const uint SecondObjectRef = 0x00654321;
    private const uint DisplayNote = 0x000B1234;
    private const uint SubTerminal = 0x000C5678;

    private static readonly byte[] BigEndianBytecode =
    [
        0x00, 0x1D, 0x00, 0x00,
        0x00, 0x10, 0x00, 0x08,
        0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00,
        0x00, 0x15, 0x00, 0x09,
        0x66,
        0x00, 0x07,
        0x00, 0x06,
        0x20, 0x6E, 0x00, 0x00, 0x00, 0x01,
        0x00, 0x11, 0x00, 0x00
    ];

    private static readonly byte[] ExpectedLittleEndianBytecode =
    [
        0x1D, 0x00, 0x00, 0x00,
        0x10, 0x00, 0x08, 0x00,
        0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00,
        0x15, 0x00, 0x09, 0x00,
        0x66,
        0x07, 0x00,
        0x06, 0x00,
        0x20, 0x6E, 0x01, 0x00, 0x00, 0x00,
        0x11, 0x00, 0x00, 0x00
    ];

    [Fact]
    public void EncodeNew_EmbeddedScriptUsesCanonicalHeaderAndPreservesTableOrder()
    {
        var encoded = TermEncoder.EncodeNew(
            CreateTerminal(),
            new HashSet<uint>
            {
                FirstObjectRef, SecondObjectRef, DisplayNote, SubTerminal
            });

        Assert.Equal(
            [
                "EDID", "DNAM", "ITXT", "RNAM", "ANAM", "INAM", "TNAM",
                "SCHR", "SCDA", "SCTX",
                "SLSD", "SCVR", "SLSD", "SCVR", "SCRO", "SCRV", "SCRO", "SCRV"
            ],
            encoded.Subrecords.Select(static subrecord => subrecord.Signature));

        var schr = Assert.Single(encoded.Subrecords, static subrecord => subrecord.Signature == "SCHR").Bytes;
        Assert.Equal(20, schr.Length);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(schr.AsSpan(0, 4))); // Padding
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(schr.AsSpan(4, 4))); // RefCount
        Assert.Equal((uint)ExpectedLittleEndianBytecode.Length,
            BinaryPrimitives.ReadUInt32LittleEndian(schr.AsSpan(8, 4))); // CompiledSize
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(schr.AsSpan(12, 4))); // VariableCount
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(schr.AsSpan(16, 2))); // Object
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16LittleEndian(schr.AsSpan(18, 2))); // Enabled

        var scda = Assert.Single(encoded.Subrecords, static subrecord => subrecord.Signature == "SCDA").Bytes;
        Assert.Equal(ExpectedLittleEndianBytecode, scda);

        var variableData = encoded.Subrecords
            .Where(static subrecord => subrecord.Signature == "SLSD")
            .Select(static subrecord => subrecord.Bytes)
            .ToArray();
        Assert.Equal(2, variableData.Length);
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(variableData[0]));
        Assert.Equal(1, variableData[0][16]);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(variableData[1]));
        Assert.Equal(0, variableData[1][16]);

        var referenceTable = encoded.Subrecords
            .Where(static subrecord => subrecord.Signature is "SCRO" or "SCRV")
            .Select(static subrecord =>
                (subrecord.Signature, BinaryPrimitives.ReadUInt32LittleEndian(subrecord.Bytes)))
            .ToArray();
        Assert.Equal(
            [
                ("SCRO", FirstObjectRef),
                ("SCRV", 7u),
                ("SCRO", SecondObjectRef),
                ("SCRV", 2u)
            ],
            referenceTable);
    }

    [Fact]
    public void EncodeThenParse_EmbeddedVariablesAndMixedReferenceTableRoundTrip()
    {
        var encoded = TermEncoder.EncodeNew(
            CreateTerminal(),
            new HashSet<uint>
            {
                FirstObjectRef, SecondObjectRef, DisplayNote, SubTerminal
            });

        var parsed = ParseEncodedTerminal(encoded);

        var item = Assert.Single(parsed.MenuItems);
        Assert.Equal("Run diagnostics", item.Text);
        // "abc\0" is exactly four bytes on disk; RNAM must still parse as text, not FormID.
        Assert.Equal("abc", item.ResultText);
        Assert.Equal(DisplayNote, item.DisplayNoteFormId);
        Assert.Equal(SubTerminal, item.SubTerminal);
        Assert.Equal((byte)2, item.ActionType);
        Assert.Equal(ExpectedLittleEndianBytecode, item.CompiledData);
        Assert.Equal("set localCount to 1", item.SourceText);
        Assert.False(item.IsBigEndianBytecode);
        Assert.Equal(
            [new ScriptVariableInfo(7, "localCount", 1), new ScriptVariableInfo(2, "localRatio", 0)],
            item.Variables);
        Assert.Equal(
            [FirstObjectRef, 0x80000007u, SecondObjectRef, 0x80000002u],
            item.ReferencedObjects);
    }

    [Fact]
    public void EncodeThenParse_RemapsQuestVariableOwnerFormIdInMenuCondition()
    {
        const uint sourceQuest = 0x01999001;
        const uint targetQuest = 0x01000901;
        var terminal = new TerminalRecord
        {
            FormId = TermFormId,
            EditorId = "ConditionalTerminal",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "Ask about the prototype",
                    Conditions =
                    [
                        new DialogueCondition
                        {
                            FunctionIndex = 79, // GetQuestVariable
                            Parameter1 = sourceQuest,
                            Parameter2 = 17
                        }
                    ]
                }
            ]
        };

        var encoded = TermEncoder.EncodeNew(
            terminal,
            new HashSet<uint> { targetQuest },
            new Dictionary<uint, uint> { [sourceQuest] = targetQuest });

        var condition = Assert.Single(Assert.Single(ParseEncodedTerminal(encoded).MenuItems).Conditions);
        Assert.Equal((ushort)79, condition.FunctionIndex);
        Assert.Equal(targetQuest, condition.Parameter1);
        Assert.Equal(17u, condition.Parameter2);
        Assert.Contains(encoded.Warnings, warning => warning.Contains(
            "remapped 1 CTDA FormID parameter", StringComparison.Ordinal));
    }

    [Fact]
    public void EncodeThenParse_DropsWholeUnsafeMenuItemAndPreservesSibling()
    {
        const uint danglingQuest = 0x01999002;
        var terminal = new TerminalRecord
        {
            FormId = TermFormId,
            EditorId = "ConditionalTerminal",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "Unsafe prototype option",
                    ResultText = "Must not survive without its condition",
                    Conditions =
                    [
                        new DialogueCondition
                        {
                            FunctionIndex = 79, // GetQuestVariable
                            Parameter1 = danglingQuest,
                            Parameter2 = 17
                        }
                    ]
                },
                new TerminalMenuItem
                {
                    Text = "Safe sibling",
                    ResultText = "Still available"
                }
            ]
        };

        var encoded = TermEncoder.EncodeNew(
            terminal,
            new HashSet<uint> { 0x00000001 });

        var item = Assert.Single(ParseEncodedTerminal(encoded).MenuItems);
        Assert.Equal("Safe sibling", item.Text);
        Assert.Equal("Still available", item.ResultText);
        Assert.Single(encoded.Subrecords, static subrecord => subrecord.Signature == "ITXT");
        Assert.Contains(encoded.Warnings, warning => warning.Contains(
            "whole menu item is atomic", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0x0002)] // AddItem is a real script command with no condition callback.
    [InlineData(0x5102)] // Corrupt high raw index.
    public void EncodeThenParse_DropsMenuItemAbsentFromFnvCallbackTableAndPreservesSibling(
        ushort functionIndex)
    {
        var terminal = new TerminalRecord
        {
            FormId = TermFormId,
            EditorId = "CorruptConditionalTerminal",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "Misread runtime bytes",
                    Conditions =
                    [
                        new DialogueCondition
                        {
                            FunctionIndex = functionIndex
                        }
                    ]
                },
                new TerminalMenuItem
                {
                    Text = "Safe sibling",
                    ResultText = "Still available"
                }
            ]
        };

        var encoded = TermEncoder.EncodeNew(terminal);

        var item = Assert.Single(ParseEncodedTerminal(encoded).MenuItems);
        Assert.Equal("Safe sibling", item.Text);
        Assert.DoesNotContain(encoded.Subrecords, static subrecord => subrecord.Signature == "CTDA");
        Assert.Contains(encoded.Warnings, warning =>
            warning.Contains($"0x{functionIndex:X4}", StringComparison.Ordinal)
            && warning.Contains("absent from the exact retail FNV condition-callback table",
                StringComparison.Ordinal));
    }

    private static TerminalRecord CreateTerminal()
    {
        return new TerminalRecord
        {
            FormId = TermFormId,
            EditorId = "VariableTerminal",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "Run diagnostics",
                    ResultText = "abc",
                    DisplayNoteFormId = DisplayNote,
                    SubTerminal = SubTerminal,
                    ActionType = 2,
                    CompiledData = BigEndianBytecode,
                    SourceText = "set localCount to 1",
                    Variables =
                    [
                        new ScriptVariableInfo(7, "localCount", 1),
                        new ScriptVariableInfo(2, "localRatio", 0)
                    ],
                    ReferencedObjects =
                    [
                        FirstObjectRef,
                        0x80000007u,
                        SecondObjectRef,
                        0x80000002u
                    ],
                    IsBigEndianBytecode = true
                }
            ]
        };
    }

    private static TerminalRecord ParseEncodedTerminal(EncodedRecord encoded)
    {
        var recordBytes = BuildRecordBytes(
            TermFormId,
            "TERM",
            false,
            encoded.Subrecords
                .Select(static subrecord => (subrecord.Signature, subrecord.Bytes))
                .ToArray());
        var mainRecord = new DetectedMainRecord(
            "TERM",
            (uint)(recordBytes.Length - 24),
            0,
            TermFormId,
            0,
            false);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);
        var parser = new RecordParser(
            MakeScanResult([mainRecord]),
            accessor: accessor,
            fileSize: recordBytes.Length);

        return Assert.Single(parser.ParseTerminals());
    }
}