using FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

public sealed class DialogueTextBackfillTests
{
    [Fact]
    public void ApplyOverridesToInfo_BackfillsPlaceholderResponseText()
    {
        var info = new DialogueRecord
        {
            FormId = 0x0008D1B4,
            Responses =
            [
                new DialogueResponse
                {
                    Text = DialogueTextBackfill.PlaceholderText,
                    ResponseNumber = 1
                }
            ]
        };
        var overrides = new Dictionary<byte, string>
        {
            [1] = "Hello, Wanderer."
        };

        var (result, filled, appended) = DialogueTextBackfill.ApplyOverridesToInfo(info, overrides);

        Assert.Equal(1, filled);
        Assert.Equal(0, appended);
        Assert.Single(result.Responses);
        Assert.Equal("Hello, Wanderer.", result.Responses[0].Text);
        Assert.Equal((byte)1, result.Responses[0].ResponseNumber);
    }

    [Fact]
    public void ApplyOverridesToInfo_DoesNotOverwriteCapturedRealText()
    {
        var info = new DialogueRecord
        {
            FormId = 0x0008D1B4,
            Responses =
            [
                new DialogueResponse
                {
                    Text = "Real captured dialogue.",
                    ResponseNumber = 1
                }
            ]
        };
        var overrides = new Dictionary<byte, string>
        {
            [1] = "CSV would say this instead."
        };

        var (result, filled, _) = DialogueTextBackfill.ApplyOverridesToInfo(info, overrides);

        Assert.Equal(0, filled);
        Assert.Same(info, result);
        Assert.Equal("Real captured dialogue.", result.Responses[0].Text);
    }

    [Fact]
    public void ApplyOverridesToInfo_AppendsResponsesBeyondExistingCount()
    {
        // DMP captured a single placeholder response, CSV declares 3 responses for this INFO.
        var info = new DialogueRecord
        {
            FormId = 0x0008D1B4,
            Responses =
            [
                new DialogueResponse
                {
                    Text = DialogueTextBackfill.PlaceholderText,
                    ResponseNumber = 1
                }
            ]
        };
        var overrides = new Dictionary<byte, string>
        {
            [1] = "First line.",
            [2] = "Second line.",
            [3] = "Third line."
        };

        var (result, filled, appended) = DialogueTextBackfill.ApplyOverridesToInfo(info, overrides);

        Assert.Equal(1, filled);
        Assert.Equal(2, appended);
        Assert.Equal(3, result.Responses.Count);
        Assert.Equal("First line.", result.Responses[0].Text);
        Assert.Equal("Second line.", result.Responses[1].Text);
        Assert.Equal((byte)2, result.Responses[1].ResponseNumber);
        Assert.Equal("Third line.", result.Responses[2].Text);
    }

    [Fact]
    public void ApplyOverridesToInfo_BackfillsEmptyResponseText()
    {
        var info = new DialogueRecord
        {
            FormId = 0x0008D1B4,
            Responses =
            [
                new DialogueResponse { Text = string.Empty, ResponseNumber = 1 }
            ]
        };
        var overrides = new Dictionary<byte, string> { [1] = "From CSV." };

        var (result, filled, _) = DialogueTextBackfill.ApplyOverridesToInfo(info, overrides);

        Assert.Equal(1, filled);
        Assert.Equal("From CSV.", result.Responses[0].Text);
    }

    [Theory]
    [InlineData("sound\\voice\\falloutnv.esm\\creatureghoul\\tempvdialoguej_greeting_0008d1b4_1.xma", 1)]
    [InlineData("sound\\voice\\falloutnv.esm\\creatureghoul\\tempvdialoguej_greeting_0008d1b4_2.xma", 2)]
    [InlineData("sound/voice/foo/bar_deadbeef_10.ogg", 10)]
    [InlineData("foo_deadbeef_3.lip", 3)]
    [InlineData("no_response_number.xma", null)]
    [InlineData("invalid_path", null)]
    public void ExtractResponseNumber_ParsesValidPaths(string path, int? expected)
    {
        var actual = DialogueTextBackfill.ExtractResponseNumber(path);
        Assert.Equal(expected.HasValue ? (byte?)expected.Value : null, actual);
    }

    [Fact]
    public void ApplyFromCsvs_LoadsAndAppliesMultipleRows()
    {
        var csvPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "_test_dialogue.csv");
        File.WriteAllText(csvPath,
            "File,FormID,VoiceType,Speaker,Quest,Source,Text\n" +
            "voice_0008d1b4_1.xma,0008D1B4,vt,sp,quest,src,\"Hello, Wanderer.\"\n" +
            "voice_0008d1b4_2.xma,0008D1B4,vt,sp,quest,src,Have you come to help us?\n");
        try
        {
            var dialogues = new List<DialogueRecord>
            {
                new()
                {
                    FormId = 0x0008D1B4,
                    Responses =
                    [
                        new DialogueResponse
                        {
                            Text = DialogueTextBackfill.PlaceholderText,
                            ResponseNumber = 1
                        }
                    ]
                }
            };

            var result = DialogueTextBackfill.ApplyFromCsvs(
                dialogues, [csvPath], NullConversionProgressSink.Instance);

            Assert.Equal(2, result.RowsParsed);
            Assert.Equal(1, result.InfosTouched);
            Assert.Equal(1, result.ResponsesFilled);
            Assert.Equal(1, result.ResponsesAppended);

            var updated = dialogues[0];
            Assert.Equal(2, updated.Responses.Count);
            Assert.Equal("Hello, Wanderer.", updated.Responses[0].Text);
            Assert.Equal("Have you come to help us?", updated.Responses[1].Text);
        }
        finally
        {
            File.Delete(csvPath);
        }
    }

    [Fact]
    public void ApplyFromCsvs_IgnoresRowsForUnknownFormIds()
    {
        var csvPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "_test_dialogue.csv");
        File.WriteAllText(csvPath,
            "File,FormID,VoiceType,Speaker,Quest,Source,Text\n" +
            "voice_DEADBEEF_1.xma,DEADBEEF,vt,sp,quest,src,Orphan line\n");
        try
        {
            var dialogues = new List<DialogueRecord>
            {
                new()
                {
                    FormId = 0x0008D1B4, // Not the CSV's FormID
                    Responses = [new DialogueResponse { Text = "Existing", ResponseNumber = 1 }]
                }
            };

            var result = DialogueTextBackfill.ApplyFromCsvs(
                dialogues, [csvPath], NullConversionProgressSink.Instance);

            Assert.Equal(1, result.RowsParsed);
            Assert.Equal(0, result.InfosTouched);
            Assert.Equal("Existing", dialogues[0].Responses[0].Text);
        }
        finally
        {
            File.Delete(csvPath);
        }
    }
}
