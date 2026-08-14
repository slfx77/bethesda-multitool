using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class ModernDialogueExtractorConditionStringTests
{
    [Theory]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.Fallout4)]
    [InlineData(BethesdaGame.Fallout76)]
    public void BuildInfo_PairsOrderedCisSiblingsIncludingEmptyStrings(BethesdaGame game)
    {
        var context = new RecordParserContext(new EsmRecordScanResult { Game = game });
        var info = DialogueExtractors.For(game).BuildInfo(
            0x100,
            null,
            null,
            0,
            [
                new RawSubrecord("CTDA", BuildCtda(660, 0xDEADBEEF, 0)),
                new RawSubrecord("CIS1", NullTerminated("TestScript")),
                new RawSubrecord("CIS2", NullTerminated(string.Empty)),
                new RawSubrecord("CTDA", BuildCtda(629, 0x00123456, 42)),
                new RawSubrecord("CIS2", NullTerminated("::questVariable_var")),
                new RawSubrecord("CTDA", BuildCtda(675, 7, 0)),
                new RawSubrecord("ENAM", [0]),
                new RawSubrecord("CIS1", NullTerminated("must-not-attach"))
            ],
            false,
            context);

        Assert.Collection(info.Conditions,
            first =>
            {
                Assert.Equal("TestScript", first.Parameter1String);
                Assert.Equal(string.Empty, first.Parameter2String);
            },
            second =>
            {
                Assert.Null(second.Parameter1String);
                Assert.Equal("::questVariable_var", second.Parameter2String);
            },
            third =>
            {
                Assert.Null(third.Parameter1String);
                Assert.Null(third.Parameter2String);
            });
    }

    private static byte[] BuildCtda(ushort functionIndex, uint parameter1, uint parameter2)
    {
        var data = new byte[32];
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), 1f);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), functionIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), parameter1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), parameter2);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), -1);
        return data;
    }

    private static byte[] NullTerminated(string value) => Encoding.ASCII.GetBytes(value + '\0');
}
