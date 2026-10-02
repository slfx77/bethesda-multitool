using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.CLI.Formatters;
using BethesdaMultitool.Core.Games;
using Spectre.Console;
using Xunit;
using static BethesdaMultitool.Tests.CLI.SemdiffTestRecords;

namespace BethesdaMultitool.Tests.CLI;

/// <summary>
///     Semdiff across byte orders: an Xbox 360 (big-endian) record against a PC (little-endian) one.
///     Every payload is encoded by hand in its own file's byte order, so the two sides' subrecord bytes
///     differ wherever a multi-byte value sits and equal values must still compare equal. Before the
///     comparer swapped the big-endian side with the converter's schema, every such pair was Different
///     over a table whose rows all read MATCH. The controls pin that equality is still decided on exact
///     bytes, never on the rounded or truncated display strings.
/// </summary>
public sealed class SemdiffByteOrderTests
{
    private const uint AchrFormId = 0x000E739E;

    private static readonly SemdiffTypes.SemdiffCompareOptions X360VsPc = new()
    {
        GameA = BethesdaGame.FalloutNewVegas,
        GameB = BethesdaGame.FalloutNewVegas,
        BigEndianA = true,
        BigEndianB = false
    };

    private static readonly SemdiffTypes.SemdiffCompareOptions SameByteOrder = new()
    {
        GameA = BethesdaGame.FalloutNewVegas,
        GameB = BethesdaGame.FalloutNewVegas
    };

    [Fact]
    public void Compare_SameAchrInBothByteOrders_IsIdentical()
    {
        var x360 = ParseAchr(true, 15, 25119.5f);
        var pc = ParseAchr(false, 15, 25119.5f);

        // Control: the payloads really differ byte-wise, so equality needs the swap.
        Assert.NotEqual(Payload(x360[0], "DATA"), Payload(pc[0], "DATA"));
        Assert.NotEqual(Payload(x360[0], "NAME"), Payload(pc[0], "NAME"));

        var result = SemdiffComparer.Compare(x360, pc, X360VsPc with { ShowAll = true });

        var diff = Assert.Single(result.Records);
        Assert.Equal(SemdiffTypes.DiffType.Identical, diff.DiffType);
        Assert.Empty(diff.FieldDiffs!);
        Assert.Null(diff.SubrecordOrderNote);
        Assert.Equal(0, result.Summary.WithDifferences);
        Assert.Equal(1, result.Summary.Identical);
    }

    [Fact]
    public void Compare_SameAchrInBothByteOrders_DifferingOnlyInFormVersion_IsFormVersionOnly()
    {
        // The Xbox 360 build stamps form version 15; PC 1.0 kept 14 on a record it never re-saved.
        var result = SemdiffComparer.Compare(ParseAchr(true, 15, 25119.5f), ParseAchr(false, 14, 25119.5f),
            X360VsPc);

        Assert.Empty(result.Records);
        Assert.Equal(1, result.Summary.FormVersionOnly);
        Assert.Equal(0, result.Summary.WithDifferences);
        Assert.Equal(1, result.Summary.Compared);
    }

    [Fact]
    public void Compare_SameInfoInBothByteOrders_IsIdentical()
    {
        var result = SemdiffComparer.Compare(ParseOne(true, "INFO", 0x000E9476, InfoSubrecords(true)),
            ParseOne(false, "INFO", 0x000E9476, InfoSubrecords(false)), X360VsPc with { ShowAll = true });

        var diff = Assert.Single(result.Records);
        Assert.Equal(SemdiffTypes.DiffType.Identical, diff.DiffType);
        Assert.Equal(0, result.Summary.WithDifferences);
    }

    [Fact]
    public void Compare_QuestIndexStoredLittleEndianOnXbox_IsNotSwapped()
    {
        // QUST INDX is already little-endian on Xbox 360 (the converter's schema preserves it), so the
        // same two bytes appear in both files; swapping every multi-byte field would break the match.
        (string, byte[])[] Subrecords(bool bigEndian) =>
        [
            ("EDID", Encoding.ASCII.GetBytes("VMS01\0")),
            ("INDX", [0x0A, 0x00]),
            ("QSDT", [0x01]),
            ("CTDA", Condition(bigEndian))
        ];

        var result = SemdiffComparer.Compare(ParseOne(true, "QUST", 0x00001000, Subrecords(true)),
            ParseOne(false, "QUST", 0x00001000, Subrecords(false)), X360VsPc with { ShowAll = true });

        Assert.Equal(SemdiffTypes.DiffType.Identical, Assert.Single(result.Records).DiffType);
    }

    [Fact]
    public void Compare_PerkTopLevelDataIsCopiedAndEntryDataIsSwapped_AsTheConverterDoes()
    {
        // A PERK's top-level DATA(4) is four UInt8 fields and the converter copies it; between PRKE and
        // PRKF the same size is an ability FormID and is swapped. [1, 0, 1, 1] is not a palindrome, so
        // swapping the top-level one would report a difference.
        (string, byte[])[] Subrecords(bool bigEndian) =>
        [
            ("EDID", Encoding.ASCII.GetBytes("Toughness\0")),
            ("DATA", [0x01, 0x00, 0x01, 0x01]),
            ("PRKE", [0x01, 0x00, 0x00]),
            ("DATA", new FieldWriter(bigEndian).U32(0x0003D3A5).ToArray()),
            ("PRKF", [])
        ];

        var result = SemdiffComparer.Compare(ParseOne(true, "PERK", 0x0003D3A6, Subrecords(true)),
            ParseOne(false, "PERK", 0x0003D3A6, Subrecords(false)), X360VsPc with { ShowAll = true });

        Assert.Equal(SemdiffTypes.DiffType.Identical, Assert.Single(result.Records).DiffType);
    }

    [Fact]
    public void Compare_FloatDifferingBelowDisplayPrecision_IsDifferent()
    {
        // 25119.50 and 25119.51 are about five float ulps apart: only the lowest mantissa byte changes,
        // which in PC order is byte 0. The PosRot display rounds both to 25119.5.
        var result = SemdiffComparer.Compare(ParseAchr(true, 15, 25119.50f), ParseAchr(false, 15, 25119.51f),
            X360VsPc);

        var diff = Assert.Single(result.Records);
        Assert.Equal(SemdiffTypes.DiffType.Different, diff.DiffType);
        var data = Assert.Single(diff.FieldDiffs!);
        Assert.Equal("DATA", data.Signature);
        Assert.False(data.ByteOrderUnresolved);
        Assert.Equal(0, data.FirstDifferingOffset);

        var rows = SemdiffFieldFormatter.BuildFieldRows(data);
        Assert.True(Assert.Single(rows, r => r.Field == "PosRot").Equal);
        var bytesRow = Assert.Single(rows, r => r.Field == "(bytes)");
        Assert.False(bytesRow.Equal);
        Assert.Contains("bytes differ from offset 0", bytesRow.Message);
    }

    [Fact]
    public void Compare_SameByteOrder_BytesPastTheSchemasLastField_StayDifferent()
    {
        // The NAME schema decodes one FormID; the second dword is never decoded, so the display alone
        // would read MATCH.
        var result = SemdiffComparer.Compare(
            [Record("ACHR", AchrFormId, Sub("NAME", [.. U32(0x00123456), .. U32(0)]))],
            [Record("ACHR", AchrFormId, Sub("NAME", [.. U32(0x00123456), .. U32(1)]))],
            SameByteOrder);

        var diff = Assert.Single(result.Records);
        Assert.Equal(SemdiffTypes.DiffType.Different, diff.DiffType);
        var name = Assert.Single(diff.FieldDiffs!);
        Assert.Equal(4, name.FirstDifferingOffset);
        var rows = SemdiffFieldFormatter.BuildFieldRows(name);
        Assert.Contains(rows, r => r.Field == "(bytes)" && r.Message!.Contains("offset 4"));
    }

    [Fact]
    public void Compare_SameByteOrder_LongUnschemadPayloadDifferingAtItsEnd_StaysDifferent()
    {
        // No schema: the raw display shows only the first four bytes and the length.
        var bytesA = new byte[12];
        var bytesB = new byte[12];
        bytesB[10] = 0x7F;

        var result = SemdiffComparer.Compare(
            [Record("ACHR", AchrFormId, Sub("ZZZZ", bytesA))],
            [Record("ACHR", AchrFormId, Sub("ZZZZ", bytesB))],
            SameByteOrder);

        var diff = Assert.Single(result.Records);
        Assert.Equal(SemdiffTypes.DiffType.Different, diff.DiffType);
        var raw = Assert.Single(diff.FieldDiffs!);
        Assert.Equal(10, raw.FirstDifferingOffset);
        Assert.Contains(SemdiffFieldFormatter.BuildFieldRows(raw),
            r => r.Field == "(bytes)" && r.Message!.Contains("offset 10"));
    }

    [Fact]
    public void Compare_CrossByteOrder_UnschemadMultiByteSubrecord_IsUnresolvedNeverEqual()
    {
        // No schema says how to swap ZZZZ, so even byte-identical payloads cannot be called equal.
        var result = SemdiffComparer.Compare(
            ParseOne(true, "ACHR", AchrFormId, [("ZZZZ", [0x01, 0x02, 0x03, 0x04])]),
            ParseOne(false, "ACHR", AchrFormId, [("ZZZZ", [0x01, 0x02, 0x03, 0x04])]),
            X360VsPc);

        var diff = Assert.Single(result.Records);
        Assert.Equal(SemdiffTypes.DiffType.Different, diff.DiffType);
        var unresolved = Assert.Single(diff.FieldDiffs!);
        Assert.True(unresolved.ByteOrderUnresolved);
        Assert.Null(unresolved.FirstDifferingOffset);
        var row = Assert.Single(SemdiffFieldFormatter.BuildFieldRows(unresolved));
        Assert.False(row.Equal);
        Assert.Equal("byte order unresolved (no schema)", row.Message);
    }

    [Fact]
    public void Compare_CrossByteOrder_UnschemadSingleByte_HasNoByteOrderToResolve()
    {
        var result = SemdiffComparer.Compare(
            ParseOne(true, "ACHR", AchrFormId, [("ZZZZ", [0x07])]),
            ParseOne(false, "ACHR", AchrFormId, [("ZZZZ", [0x07])]),
            X360VsPc with { ShowAll = true });

        Assert.Equal(SemdiffTypes.DiffType.Identical, Assert.Single(result.Records).DiffType);
    }

    [Fact]
    public void Compare_CrossByteOrder_SubrecordsReordered_IsDifferentWithOrderNote()
    {
        (string, byte[])[] Ordered(bool bigEndian, bool xclrFirst)
        {
            var edid = ("EDID", Encoding.ASCII.GetBytes("Cell\0"));
            var xclr = ("XCLR", new FieldWriter(bigEndian).U32(0x00012345).ToArray());
            var xcas = ("XCAS", new FieldWriter(bigEndian).U32(0x00054321).ToArray());
            if (xclrFirst)
            {
                return [edid, xclr, xcas];
            }

            return [edid, xcas, xclr];
        }

        var reordered = SemdiffComparer.Compare(ParseOne(true, "CELL", 0x000845F4, Ordered(true, true)),
            ParseOne(false, "CELL", 0x000845F4, Ordered(false, false)), X360VsPc);
        var diff = Assert.Single(reordered.Records);
        Assert.Equal(SemdiffTypes.DiffType.Different, diff.DiffType);
        Assert.Empty(diff.FieldDiffs!);
        Assert.Contains("where File A has XCLR and File B has XCAS", diff.SubrecordOrderNote);

        var sameOrder = SemdiffComparer.Compare(ParseOne(true, "CELL", 0x000845F4, Ordered(true, true)),
            ParseOne(false, "CELL", 0x000845F4, Ordered(false, true)), X360VsPc);
        Assert.Empty(sameOrder.Records);
        Assert.Equal(1, sameOrder.Summary.Identical);
    }

    [Fact]
    public void DisplayResult_SameAchrInBothByteOrders_ReportsNoDifferences()
    {
        var result = SemdiffComparer.Compare(ParseAchr(true, 15, 25119.5f), ParseAchr(false, 15, 25119.5f),
            X360VsPc);

        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No
        });
        console.Profile.Width = 240;
        SemdiffFieldFormatter.DisplayResult(console, result, "File A", "File B", 10, false);

        var output = writer.ToString();
        Assert.Contains("No differences found.", output);
        Assert.DoesNotContain("with differences", output);
    }

    /// <summary>A placed actor with a string, a FormID, six floats and a scale, in one byte order.</summary>
    private static List<SemdiffTypes.ParsedRecord> ParseAchr(bool bigEndian, ushort formVersion, float x)
    {
        return ParseOne(bigEndian, "ACHR", AchrFormId,
        [
            ("EDID", Encoding.ASCII.GetBytes("VMS01Guard\0")),
            ("NAME", new FieldWriter(bigEndian).U32(0x00123456).ToArray()),
            ("DATA", new FieldWriter(bigEndian).F32(x).F32(-1309.1f).F32(-652.2f).F32(0f).F32(0f).F32(1.5708f)
                .ToArray()),
            ("XSCL", new FieldWriter(bigEndian).F32(1.04f).ToArray())
        ], formVersion);
    }

    /// <summary>A dialogue response: response data, text, quest, one condition and the flag bytes.</summary>
    private static (string, byte[])[] InfoSubrecords(bool bigEndian)
    {
        return
        [
            ("TRDT", new FieldWriter(bigEndian).U32(2).U32(50).U32(0x000E9475).U8(1).Pad(3).U32(0x00154A20).U8(1)
                .Pad(3).ToArray()),
            ("NAM1", Encoding.ASCII.GetBytes("Hello there.\0")),
            ("QSTI", new FieldWriter(bigEndian).U32(0x000E9474).ToArray()),
            ("CTDA", Condition(bigEndian)),
            ("DATA", [0x00, 0x01, 0x02, 0x00])
        ];
    }

    /// <summary>CTDA(28): type, comparison value, function index, two parameters, run-on, reference.</summary>
    private static byte[] Condition(bool bigEndian)
    {
        return new FieldWriter(bigEndian).U8(0).Pad(3).F32(1.0f).U16(72).Pad(2).U32(0x000E9473).U32(0).U32(0).U32(0)
            .ToArray();
    }

    private static List<SemdiffTypes.ParsedRecord> ParseOne(bool bigEndian, string type, uint formId,
        (string Signature, byte[] Data)[] subrecords, ushort formVersion = 15)
    {
        var bytes = RecordBytes(bigEndian, type, formId, 0, 0, formVersion, 0, false, subrecords);
        var parsed = SemdiffRecordParser.ParseRecordsWithSubrecords(bytes, bigEndian, null, null);
        Assert.Single(parsed);
        return parsed;
    }

    private static byte[] Payload(SemdiffTypes.ParsedRecord record, string signature)
    {
        return record.Subrecords.First(s => s.Signature == signature).Data;
    }

    /// <summary>Writes subrecord fields in one byte order.</summary>
    private sealed class FieldWriter(bool bigEndian)
    {
        private readonly List<byte> _bytes = [];

        public FieldWriter U8(byte value)
        {
            _bytes.Add(value);
            return this;
        }

        public FieldWriter Pad(int count)
        {
            _bytes.AddRange(new byte[count]);
            return this;
        }

        public FieldWriter U16(ushort value)
        {
            var bytes = new byte[2];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
            }

            _bytes.AddRange(bytes);
            return this;
        }

        public FieldWriter U32(uint value)
        {
            var bytes = new byte[4];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            }

            _bytes.AddRange(bytes);
            return this;
        }

        public FieldWriter F32(float value)
        {
            return U32(BitConverter.SingleToUInt32Bits(value));
        }

        public byte[] ToArray()
        {
            return [.. _bytes];
        }
    }
}
