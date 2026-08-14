using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class DialogueConditionUseGlobalTests
{
    private const uint GlobalFormId = 0x00123456;
    private const uint SpeakerFormId = 0x000ED239;

    [Theory]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.Fallout4)]
    [InlineData(BethesdaGame.Fallout76)]
    public void ModernTypedExtractors_PreserveGlobalBitsWithoutInferringSpeaker(BethesdaGame game)
    {
        var context = new RecordParserContext(new EsmRecordScanResult { Game = game });
        var info = DialogueExtractors.For(game).BuildInfo(
            0x100,
            null,
            null,
            0,
            [new RawSubrecord("CTDA", BuildLittleEndianCtda(32))],
            false,
            context);

        var condition = Assert.Single(info.Conditions);
        Assert.True(condition.UsesGlobalComparison);
        Assert.Equal(GlobalFormId, condition.ComparisonGlobalFormId);
        Assert.Null(info.SpeakerFormId);
    }

    [Fact]
    public void OblivionTypedExtractor_PreservesGlobalBitsWithoutInferringSpeaker()
    {
        var game = BethesdaGame.Oblivion;
        var context = new RecordParserContext(new EsmRecordScanResult { Game = game });
        var info = DialogueExtractors.For(game).BuildInfo(
            0x100,
            null,
            null,
            0,
            [new RawSubrecord("CTDA", BuildLittleEndianCtda(20))],
            false,
            context);

        var condition = Assert.Single(info.Conditions);
        Assert.Equal(GlobalFormId, condition.ComparisonGlobalFormId);
        Assert.Null(info.SpeakerFormId);
    }

    [Fact]
    public void FalloutTypedHandler_PreservesBigEndianGlobalBitsWithoutInferringSpeaker()
    {
        var data = BuildBigEndianCtda();
        var functions = new List<ushort>();
        uint? speaker = null;
        uint? faction = null;
        uint? race = null;
        uint? voiceType = null;

        var condition = DialogueConditionParser.ParseCtdaCondition(
            data,
            true,
            functions,
            ref speaker,
            ref faction,
            ref race,
            ref voiceType);

        Assert.NotNull(condition);
        Assert.Equal(GlobalFormId, condition!.ComparisonGlobalFormId);
        Assert.Null(speaker);
        Assert.Equal((ushort)0x48, Assert.Single(functions));
    }

    [Fact]
    public void SharedTypedHandler_PreservesModernParameter3()
    {
        var data = BuildLittleEndianCtda(32);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), -42);
        var functions = new List<ushort>();
        uint? speaker = null;
        uint? faction = null;
        uint? race = null;
        uint? voiceType = null;

        var condition = DialogueConditionParser.ParseCtdaCondition(
            data,
            false,
            functions,
            ref speaker,
            ref faction,
            ref race,
            ref voiceType);

        Assert.NotNull(condition);
        Assert.Equal(-42, condition!.Parameter3);
        Assert.Equal((ushort)0x48, Assert.Single(functions));
    }

    private static byte[] BuildLittleEndianCtda(int length)
    {
        var data = new byte[length];
        data[0] = 0x24; // Use Global + !=; raw FormID bits would otherwise look like false.
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), GlobalFormId);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), 0x48); // GetIsID
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), SpeakerFormId);
        return data;
    }

    private static byte[] BuildBigEndianCtda()
    {
        var data = new byte[28];
        data[0] = 0x24;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), GlobalFormId);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8), 0x48);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), SpeakerFormId);
        return data;
    }
}
