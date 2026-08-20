using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class Fallout4DialogueExtractorConditionTests
{
    [Theory]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.Fallout4)]
    [InlineData(BethesdaGame.Fallout76)]
    public void ModernBuildInfo_AcceptsOnlyComplete32ByteCtda(BethesdaGame game)
    {
        var context = new RecordParserContext(new EsmRecordScanResult { Game = game });
        var info = DialogueExtractors.For(game).BuildInfo(
            0x100,
            null,
            null,
            0,
            [
                new RawSubrecord("CTDA", new byte[20]),
                new RawSubrecord("CTDA", new byte[29]),
                new RawSubrecord("CTDA", BuildCtda(0, 1, 0, 0, 0)),
                new RawSubrecord("CTDA", new byte[31])
            ],
            false,
            context);

        Assert.Equal((ushort)1, Assert.Single(info.Conditions).FunctionIndex);
    }

    [Fact]
    public void OblivionBuildInfo_AcceptsOnlyComplete20ByteCtda()
    {
        var body = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8), 1);
        var context = new RecordParserContext(new EsmRecordScanResult { Game = BethesdaGame.Oblivion });
        var info = OblivionDialogueExtractor.Instance.BuildInfo(
            0x100,
            null,
            null,
            0,
            [
                new RawSubrecord("CTDA", new byte[16]),
                new RawSubrecord("CTDA", body),
                new RawSubrecord("CTDA", new byte[21])
            ],
            false,
            context);

        Assert.Equal((ushort)1, Assert.Single(info.Conditions).FunctionIndex);
    }

    [Theory]
    [InlineData(BethesdaGame.Fallout4)]
    [InlineData(BethesdaGame.Fallout76)]
    public void BuildInfo_PreservesRawRunOnReferenceStorageAndParameter3(BethesdaGame game)
    {
        var questAlias = BuildCtda(
            0x02,
            0x0A1,
            0x00123456,
            5,
            0xDEADBEEF,
            -17);
        var explicitReference = BuildCtda(
            0,
            0x001,
            0x00000007,
            2,
            0x00ABCDEF);

        var context = new RecordParserContext(
            new EsmRecordScanResult { Game = game });
        var info = DialogueExtractors.For(game).BuildInfo(
            0x100,
            null,
            null,
            0,
            [new RawSubrecord("CTDA", questAlias), new RawSubrecord("CTDA", explicitReference)],
            false,
            context);

        Assert.Collection(info.Conditions,
            condition =>
            {
                Assert.Equal(5u, condition.RunOn);
                Assert.Equal(0xDEADBEEFu, condition.Reference);
                Assert.Equal(-17, condition.Parameter3);
            },
            condition =>
            {
                Assert.Equal(2u, condition.RunOn);
                Assert.Equal(0x00ABCDEFu, condition.Reference);
                Assert.Equal(-1, condition.Parameter3);
            });
    }

    [Fact]
    public void SkyrimBuildInfo_PreservesSemanticReferenceAndIgnoredStorage()
    {
        var explicitReference = BuildCtda(
            0,
            0x001,
            0x00000007,
            2,
            0x00ABCDEF);
        var subjectStorage = BuildCtda(
            0,
            0x048,
            0x00123456,
            0,
            0xDEADBEEF,
            int.MinValue);

        var context = new RecordParserContext(
            new EsmRecordScanResult { Game = BethesdaGame.Skyrim });
        var info = SkyrimDialogueExtractor.Instance.BuildInfo(
            0x100,
            null,
            null,
            0,
            [new RawSubrecord("CTDA", explicitReference), new RawSubrecord("CTDA", subjectStorage)],
            false,
            context);

        Assert.Collection(info.Conditions,
            condition =>
            {
                Assert.Equal(2u, condition.RunOn);
                Assert.Equal(0x00ABCDEFu, condition.Reference);
                Assert.Equal(-1, condition.Parameter3);
            },
            condition =>
            {
                Assert.Equal(0u, condition.RunOn);
                Assert.Equal(0xDEADBEEFu, condition.Reference);
                Assert.Equal(int.MinValue, condition.Parameter3);
            });
    }

    private static byte[] BuildCtda(
        byte type,
        ushort functionIndex,
        uint param1,
        uint runOn,
        uint referenceStorage,
        int parameter3 = -1)
    {
        var data = new byte[32];
        data[0] = type;
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), 1f);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), functionIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), param1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), runOn);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), referenceStorage);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), parameter3);
        return data;
    }
}