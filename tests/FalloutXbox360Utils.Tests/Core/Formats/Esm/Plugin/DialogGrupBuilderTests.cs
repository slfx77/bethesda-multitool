using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Merge;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

public sealed class DialogGrupBuilderTests
{
    [Fact]
    public void BuildDialogSection_EmitsNewInfoUnderMasterDialAnchor()
    {
        const uint masterGoodbyeDial = 0x000000D4;
        var masterDial = ParsedRecord("DIAL", masterGoodbyeDial,
            new ParsedSubrecord
            {
                Signature = "EDID",
                Data = Encoding.Latin1.GetBytes("GOODBYE\0")
            });
        var masters = new Dictionary<uint, ParsedMainRecord>
        {
            [masterGoodbyeDial] = masterDial
        };
        var info = new DialogueRecord
        {
            FormId = 0x00133FCB,
            TopicFormId = masterGoodbyeDial,
            Responses =
            [
                new DialogueResponse
                {
                    Text = "Later.",
                    ResponseNumber = 1
                }
            ]
        };
        var stats = new ConversionPipelineStats();

        var result = DialogGrupBuilder.BuildDialogSection(
            topics: [],
            infos: [info],
            new NewVsOverrideClassifier(masters.Keys),
            new FormIdAllocator(),
            masters.Keys,
            masters,
            stats,
            NullConversionProgressSink.Instance);

        Assert.NotEmpty(result.DialogSection);
        Assert.Contains("DIAL"u8.ToArray(), result.DialogSection);
        Assert.Contains("INFO"u8.ToArray(), result.DialogSection);
        Assert.True(ContainsTopicChildrenGrup(result.DialogSection, masterGoodbyeDial));
        Assert.Equal(1, stats.EmittedByType["DIAL"]);
        Assert.Equal(1, stats.EmittedByType["INFO"]);
        Assert.Equal(1, stats.OverridesEmitted);
        Assert.Equal(1, stats.NewRecordsEmitted);
    }

    private static ParsedMainRecord ParsedRecord(
        string signature,
        uint formId,
        params ParsedSubrecord[] subrecords)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = signature,
                FormId = formId
            },
            Subrecords = subrecords.ToList()
        };
    }

    private static bool ContainsTopicChildrenGrup(ReadOnlySpan<byte> bytes, uint topicFormId)
    {
        for (var i = 0; i <= bytes.Length - 16; i++)
        {
            if (bytes.Slice(i, 4).SequenceEqual("GRUP"u8) &&
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i + 8, 4)) == topicFormId &&
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(i + 12, 4)) == 7)
            {
                return true;
            }
        }

        return false;
    }
}
