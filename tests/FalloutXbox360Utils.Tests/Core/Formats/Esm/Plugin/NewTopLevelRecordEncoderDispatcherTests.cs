using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

public class NewTopLevelRecordEncoderDispatcherTests
{
    [Fact]
    public void TryEncode_UnknownRecordType_ReturnsNull()
    {
        var encoded = NewTopLevelRecordEncoderDispatcher.TryEncode(
            "NOPE",
            new object(),
            EmptyContext());

        Assert.Null(encoded);
    }

    [Fact]
    public void TryEncode_Gmst_DispatchesToGmstEncoder()
    {
        var encoded = NewTopLevelRecordEncoderDispatcher.TryEncode(
            "GMST",
            new GameSettingRecord
            {
                FormId = 0x100,
                EditorId = "iTestSetting",
                ValueType = GameSettingType.Integer,
                IntValue = 7
            },
            EmptyContext());

        Assert.NotNull(encoded);
        Assert.Equal(["EDID", "DATA"], encoded.Subrecords.Select(s => s.Signature));
    }

    [Theory]
    [InlineData("LVLI")]
    [InlineData("LVLN")]
    [InlineData("LVLC")]
    public void TryEncode_LeveledListAliases_DispatchToSharedEncoder(string recordType)
    {
        var encoded = NewTopLevelRecordEncoderDispatcher.TryEncode(
            recordType,
            new LeveledListRecord { FormId = 0x100, EditorId = "TestList", ListType = recordType },
            EmptyContext());

        Assert.NotNull(encoded);
        Assert.Contains(encoded.Subrecords, subrecord => subrecord.Signature == "EDID");
    }

    [Fact]
    public void TryEncode_Scol_UsesEncodingContextForNewStatReferences()
    {
        var encoded = NewTopLevelRecordEncoderDispatcher.TryEncode(
            "SCOL",
            new StaticCollectionRecord
            {
                FormId = 0x100,
                EditorId = "TestScol",
                Parts =
                [
                    new StaticCollectionPart
                    {
                        OnamFormId = 0xFF000801,
                        Placements = [new StaticCollectionPlacement(1f, 2f, 3f, 0f, 0f, 0f, 1f)]
                    }
                ]
            },
            new NewTopLevelRecordEncodingContext(new HashSet<uint>(), new HashSet<uint> { 0xFF000801 }));

        Assert.NotNull(encoded);
        Assert.Equal(["EDID", "ONAM", "DATA"], encoded.Subrecords.Select(s => s.Signature));
    }

    [Fact]
    public void SupportedRecordTypes_ContainsLeveledListAliases()
    {
        var supported = NewTopLevelRecordEncoderDispatcher.GetSupportedRecordTypes();

        Assert.Contains("LVLI", supported);
        Assert.Contains("LVLN", supported);
        Assert.Contains("LVLC", supported);
    }

    private static NewTopLevelRecordEncodingContext EmptyContext()
    {
        return new NewTopLevelRecordEncodingContext(new HashSet<uint>(), new HashSet<uint>());
    }
}
