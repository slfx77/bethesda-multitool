using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class ModernDialogueExtractorEndiannessTests
{
    private const uint GlobalFormId = 0x00123456;
    private const uint Parameter1 = 0x01020304;
    private const uint Parameter2 = 0xA1B2C3D4;
    private const uint ReferenceStorage = 0x0BADF00D;
    private const int Parameter3 = -123456789;

    [Theory]
    [InlineData(BethesdaGame.Skyrim, false)]
    [InlineData(BethesdaGame.Skyrim, true)]
    [InlineData(BethesdaGame.Fallout4, false)]
    [InlineData(BethesdaGame.Fallout4, true)]
    [InlineData(BethesdaGame.Fallout76, false)]
    [InlineData(BethesdaGame.Fallout76, true)]
    public void BuildInfo_CompleteModernCtdaHonorsRecordByteOrder(
        BethesdaGame game,
        bool bigEndian)
    {
        var context = CreateContext(game);
        var info = DialogueExtractors.For(game).BuildInfo(
            0x100,
            null,
            null,
            0,
            [new RawSubrecord("CTDA", BuildCtda(bigEndian))],
            bigEndian,
            context);

        var condition = Assert.Single(info.Conditions);
        Assert.True(condition.UsesGlobalComparison);
        Assert.Equal(GlobalFormId, condition.ComparisonGlobalFormId);
        Assert.Equal((ushort)0x48, condition.FunctionIndex);
        Assert.Equal(Parameter1, condition.Parameter1);
        Assert.Equal(Parameter2, condition.Parameter2);
        Assert.Equal(7u, condition.RunOn);
        Assert.Equal(ReferenceStorage, condition.Reference);
        Assert.Equal(Parameter3, condition.Parameter3);
        Assert.Null(info.SpeakerFormId);
    }

    [Theory]
    [InlineData(BethesdaGame.Skyrim, false)]
    [InlineData(BethesdaGame.Skyrim, true)]
    [InlineData(BethesdaGame.Fallout4, false)]
    [InlineData(BethesdaGame.Fallout4, true)]
    [InlineData(BethesdaGame.Fallout76, false)]
    [InlineData(BethesdaGame.Fallout76, true)]
    public void BuildTopic_MultibyteFieldsHonorRecordByteOrder(
        BethesdaGame game,
        bool bigEndian)
    {
        const uint quest = 0x10203040;
        const float priority = 37.25f;
        var context = CreateContext(game);

        var topic = DialogueExtractors.For(game).BuildTopic(
            0x100,
            "TopicEditorId",
            [
                new RawSubrecord("QNAM", EncodeUInt32(quest, bigEndian)),
                new RawSubrecord("PNAM", EncodeSingle(priority, bigEndian)),
                new RawSubrecord("DATA", [0, 3, 0, 0])
            ],
            bigEndian,
            context);

        Assert.Equal(quest, topic.QuestFormId);
        Assert.Equal(priority, topic.Priority);
        Assert.Equal((byte)3, topic.TopicType);
        Assert.Equal(bigEndian, topic.IsBigEndian);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkyrimBuildInfo_NonConditionFieldsHonorRecordByteOrder(bool bigEndian)
    {
        const uint previousInfo = 0x11223344;
        const uint linkedTopic = 0x22334455;
        const uint speaker = 0x33445566;
        const uint emotionType = 5;
        const int emotionValue = -73;
        const uint sound = 0x44556677;

        var flags = new byte[4];
        WriteUInt16(flags, 0, 0x0026, bigEndian);
        var trdt = new byte[20];
        WriteUInt32(trdt, 0, emotionType, bigEndian);
        WriteInt32(trdt, 4, emotionValue, bigEndian);
        trdt[12] = 9;
        WriteUInt32(trdt, 16, sound, bigEndian);

        var context = CreateContext(BethesdaGame.Skyrim);
        var info = SkyrimDialogueExtractor.Instance.BuildInfo(
            0x100,
            null,
            null,
            0,
            [
                new RawSubrecord("ENAM", flags),
                new RawSubrecord("PNAM", EncodeUInt32(previousInfo, bigEndian)),
                new RawSubrecord("TCLT", EncodeUInt32(linkedTopic, bigEndian)),
                new RawSubrecord("ANAM", EncodeUInt32(speaker, bigEndian)),
                new RawSubrecord("TRDT", trdt),
                new RawSubrecord("NAM1", Encoding.ASCII.GetBytes("Line\0"))
            ],
            bigEndian,
            context);

        Assert.Equal(previousInfo, info.PreviousInfo);
        Assert.Equal(linkedTopic, Assert.Single(info.LinkToTopics));
        Assert.Equal(speaker, info.SpeakerFormId);
        Assert.True(info.IsRandom);
        Assert.True(info.IsSayOnce);
        Assert.True(info.IsRandomEnd);
        var response = Assert.Single(info.Responses);
        Assert.Equal("Line", response.Text);
        Assert.Equal(emotionType, response.EmotionType);
        Assert.Equal(emotionValue, response.EmotionValue);
        Assert.Equal((byte)9, response.ResponseNumber);
        Assert.Equal(sound, response.SoundFormId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkyrimBuildInfo_DataFallbackReadsLowByteOfEndianAwareFlagsWord(bool bigEndian)
    {
        var data = new byte[8];
        WriteUInt16(data, 2, 0x0026, bigEndian);

        var info = SkyrimDialogueExtractor.Instance.BuildInfo(
            0x100,
            null,
            null,
            0,
            [new RawSubrecord("DATA", data)],
            bigEndian,
            CreateContext(BethesdaGame.Skyrim));

        Assert.True(info.IsRandom);
        Assert.True(info.IsSayOnce);
        Assert.True(info.IsRandomEnd);
    }

    [Theory]
    [InlineData(BethesdaGame.Fallout4, false)]
    [InlineData(BethesdaGame.Fallout4, true)]
    [InlineData(BethesdaGame.Fallout76, false)]
    [InlineData(BethesdaGame.Fallout76, true)]
    public void Fallout4FamilyBuildInfo_NonConditionFieldsHonorRecordByteOrder(
        BethesdaGame game,
        bool bigEndian)
    {
        const uint previousInfo = 0x11223344;
        const uint speaker = 0x22334455;
        const uint sound = 0x33445566;

        var flags = new byte[4];
        WriteUInt16(flags, 0, 0x0026, bigEndian);
        var trda = new byte[9];
        WriteUInt32(trda, 0, 0x01020304, bigEndian);
        trda[4] = 11;
        WriteUInt32(trda, 5, sound, bigEndian);

        var info = DialogueExtractors.For(game).BuildInfo(
            0x100,
            null,
            null,
            0,
            [
                new RawSubrecord("ENAM", flags),
                new RawSubrecord("PNAM", EncodeUInt32(previousInfo, bigEndian)),
                new RawSubrecord("ANAM", EncodeUInt32(speaker, bigEndian)),
                new RawSubrecord("TRDA", trda),
                new RawSubrecord("NAM1", Encoding.ASCII.GetBytes("Line\0"))
            ],
            bigEndian,
            CreateContext(game));

        Assert.Equal(previousInfo, info.PreviousInfo);
        Assert.Equal(speaker, info.SpeakerFormId);
        Assert.True(info.IsRandom);
        Assert.True(info.IsSayOnce);
        Assert.True(info.IsRandomEnd);
        var response = Assert.Single(info.Responses);
        Assert.Equal("Line", response.Text);
        Assert.Equal((byte)11, response.ResponseNumber);
        Assert.Equal(sound, response.SoundFormId);
    }

    [Fact]
    public void SchemaDrivenParser_ThreadsDetectedRecordByteOrderIntoTypedDialogue()
    {
        const uint infoFormId = 0x0100ABCD;
        var data = BuildSubrecords(
            true,
            new RawSubrecord("CTDA", BuildCtda(true)));
        var file = new byte[24 + data.Length];
        data.CopyTo(file, 24);
        var record = new DetectedMainRecord(
            "INFO", (uint)data.Length, 0, infoFormId, 0, true);
        var scan = new EsmRecordScanResult
        {
            Game = BethesdaGame.Skyrim,
            MainRecords = [record]
        };
        var context = new RecordParserContext(
            scan,
            null,
            new ByteArrayMemoryAccessor(file),
            file.Length,
            null);

        var result = new SchemaDrivenRecordParser(context, []).ParseAll();

        var info = Assert.Single(result.Dialogues);
        Assert.True(info.IsBigEndian);
        var condition = Assert.Single(info.Conditions);
        Assert.Equal(GlobalFormId, condition.ComparisonGlobalFormId);
        Assert.Equal(Parameter1, condition.Parameter1);
        Assert.Equal(7u, condition.RunOn);
        Assert.Equal(ReferenceStorage, condition.Reference);
        Assert.Equal(Parameter3, condition.Parameter3);
    }

    private static RecordParserContext CreateContext(BethesdaGame game) =>
        new(new EsmRecordScanResult { Game = game });

    private static byte[] BuildCtda(bool bigEndian)
    {
        var data = new byte[32];
        data[0] = 0x24; // Use Global + !=: the raw comparison bits must not infer a speaker.
        WriteUInt32(data, 4, GlobalFormId, bigEndian);
        WriteUInt16(data, 8, 0x48, bigEndian);
        WriteUInt32(data, 12, Parameter1, bigEndian);
        WriteUInt32(data, 16, Parameter2, bigEndian);
        WriteUInt32(data, 20, 7, bigEndian);
        WriteUInt32(data, 24, ReferenceStorage, bigEndian);
        WriteInt32(data, 28, Parameter3, bigEndian);
        return data;
    }

    private static byte[] BuildSubrecords(bool bigEndian, params RawSubrecord[] subrecords)
    {
        var result = new List<byte>();
        foreach (var subrecord in subrecords)
        {
            var signature = Encoding.ASCII.GetBytes(subrecord.Signature);
            if (bigEndian)
            {
                Array.Reverse(signature);
            }

            result.AddRange(signature);
            var size = new byte[2];
            WriteUInt16(size, 0, checked((ushort)subrecord.Data.Length), bigEndian);
            result.AddRange(size);
            result.AddRange(subrecord.Data);
        }

        return [.. result];
    }

    private static byte[] EncodeUInt32(uint value, bool bigEndian)
    {
        var data = new byte[4];
        WriteUInt32(data, 0, value, bigEndian);
        return data;
    }

    private static byte[] EncodeSingle(float value, bool bigEndian)
    {
        var data = new byte[4];
        if (bigEndian)
        {
            BinaryPrimitives.WriteSingleBigEndian(data, value);
        }
        else
        {
            BinaryPrimitives.WriteSingleLittleEndian(data, value);
        }

        return data;
    }

    private static void WriteUInt16(byte[] data, int offset, ushort value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
        }
    }

    private static void WriteUInt32(byte[] data, int offset, uint value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
        }
    }

    private static void WriteInt32(byte[] data, int offset, int value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), value);
        }
    }
}
