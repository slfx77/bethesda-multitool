using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Games;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     CIS1/CIS2 are optional physical siblings of one CTDA, not free-floating references to the most
///     recently parsed condition. These end-to-end fixtures cover every typed handler that preserves
///     condition strings.
/// </summary>
public sealed class TypedConditionStringAdjacencyTests
{
    [Fact]
    public void Binder_EndsAssociationOnInterveningOrCompletedSiblingSequence()
    {
        var conditions = new List<DialogueCondition>
        {
            new() { FunctionIndex = 1 }
        };
        var binder = new ConditionStringSiblingBinder();

        binder.Begin(conditions);
        Assert.True(binder.TryConsume("CIS1", NullTermString(string.Empty)));
        Assert.False(binder.TryConsume("FULL", NullTermString("intervening")));
        Assert.False(binder.TryConsume("CIS2", NullTermString("stale")));

        conditions.Add(new DialogueCondition { FunctionIndex = 2 });
        binder.Begin(conditions);
        Assert.True(binder.TryConsume("CIS2", NullTermString("second-p2")));
        Assert.False(binder.TryConsume("CIS1", NullTermString("too late")));

        Assert.Equal(string.Empty, conditions[0].Parameter1String);
        Assert.Null(conditions[0].Parameter2String);
        Assert.Null(conditions[1].Parameter1String);
        Assert.Equal("second-p2", conditions[1].Parameter2String);
    }

    [Theory]
    [InlineData("INFO")]
    [InlineData("PACK")]
    [InlineData("QUST")]
    [InlineData("COBJ")]
    [InlineData("TERM")]
    [InlineData("ALCH")]
    [InlineData("ENCH")]
    [InlineData("SPEL")]
    public void TypedHandlers_BindOnlyImmediateCisSiblings(string recordType)
    {
        var subrecords = new List<(string Sig, byte[] Data)>();
        if (recordType == "TERM")
        {
            subrecords.Add(("ITXT", NullTermString("Menu item")));
        }
        else if (recordType is "ALCH" or "ENCH" or "SPEL")
        {
            var effectFormId = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(effectFormId, 0x01000001);
            subrecords.Add(("EFID", effectFormId));
            subrecords.Add(("EFIT", new byte[20]));
        }

        subrecords.AddRange([
            ("CTDA", BuildCtda(1)),
            ("CIS1", NullTermString("first-p1")),
            ("ZZZZ", Array.Empty<byte>()),
            ("CIS2", NullTermString("stale-p2")),
            ("CTDA", BuildCtda(2)),
            ("CIS2", NullTermString(string.Empty)),
            ("CIS1", NullTermString("too-late-p1")),
            ("CTDA", BuildCtda(3)),
            ("CIS1", NullTermString(string.Empty)),
            ("CIS2", NullTermString("third-p2"))
        ]);

        var conditions = ParseConditions(recordType, [.. subrecords]);

        Assert.Collection(conditions,
            first =>
            {
                Assert.Equal("first-p1", first.Parameter1String);
                Assert.Null(first.Parameter2String);
            },
            second =>
            {
                Assert.Null(second.Parameter1String);
                Assert.Equal(string.Empty, second.Parameter2String);
            },
            third =>
            {
                Assert.Equal(string.Empty, third.Parameter1String);
                Assert.Equal("third-p2", third.Parameter2String);
            });
    }

    [Theory]
    [InlineData("INFO")]
    [InlineData("PACK")]
    [InlineData("QUST")]
    [InlineData("COBJ")]
    [InlineData("TERM")]
    [InlineData("ALCH")]
    [InlineData("ENCH")]
    [InlineData("SPEL")]
    public void TypedHandlers_AcceptExactCtdaWidthsAndSkipMalformedTails(string recordType)
    {
        var subrecords = new List<(string Sig, byte[] Data)>();
        if (recordType == "TERM")
        {
            subrecords.Add(("ITXT", NullTermString("Menu item")));
        }
        else if (recordType is "ALCH" or "ENCH" or "SPEL")
        {
            var effectFormId = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(effectFormId, 0x01000001);
            subrecords.Add(("EFID", effectFormId));
            subrecords.Add(("EFIT", new byte[20]));
        }

        subrecords.AddRange([
            ("CTDA", BuildCtda(1, 20)),
            ("CTDA", BuildCtda(2, 24)),
            ("CTDA", BuildCtda(3, 29)),
            ("CTDA", BuildCtda(4, 28)),
            ("CTDA", BuildCtda(5, 31)),
            ("CTDA", BuildCtda(6, 32))
        ]);

        var conditions = ParseConditions(recordType, [.. subrecords]);

        Assert.Equal<ushort>([1, 2, 4, 6], conditions.Select(condition => condition.FunctionIndex));
        Assert.All(conditions.Take(3), condition => Assert.Null(condition.Parameter3));
        Assert.Equal<int?>(0, conditions[3].Parameter3);
    }

    private static List<DialogueCondition> ParseConditions(
        string recordType,
        params (string Sig, byte[] Data)[] subrecords)
    {
        const uint formId = 0x01020304;
        var recordBytes = BuildRecordBytes(formId, recordType, false, subrecords);
        var mainRecord = new DetectedMainRecord(
            recordType, (uint)(recordBytes.Length - 24), 0, formId, 0, false);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        var scanResult = MakeScanResult([mainRecord]);
        if (recordType is "ALCH" or "ENCH" or "SPEL")
        {
            // Modern xEdit grammars place wbConditions (including CIS1/CIS2) inside each effect.
            scanResult.Game = BethesdaGame.Skyrim;
        }

        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length);
        return recordType switch
        {
            "INFO" => Assert.Single(parser.ParseDialogue()).Conditions,
            "PACK" => Assert.Single(parser.ParsePackages()).Conditions,
            "QUST" => Assert.Single(parser.ParseQuests()).Conditions,
            "COBJ" => Assert.Single(parser.ParseAll().ConstructibleObjects).Conditions,
            "TERM" => Assert.Single(Assert.Single(parser.ParseTerminals()).MenuItems).Conditions,
            "ALCH" => Assert.Single(Assert.Single(parser.ParseConsumables()).Effects).Conditions,
            "ENCH" => Assert.Single(Assert.Single(parser.ParseEnchantments()).Effects).Conditions,
            "SPEL" => Assert.Single(Assert.Single(parser.ParseSpells()).Effects).Conditions,
            _ => throw new ArgumentOutOfRangeException(nameof(recordType), recordType, null)
        };
    }

    private static byte[] BuildCtda(ushort functionIndex, int length = 28)
    {
        var data = new byte[length];
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), 1f);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), functionIndex);
        return data;
    }
}
