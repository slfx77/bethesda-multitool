using FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin.AssetPacking;

public class DialogueAudioCsvAssetCollectorTests
{
    [Fact]
    public void CollectFromCsv_AddsOggAndLipRequestsForMatchedInfoFormIds()
    {
        using var csv = new TempFile();
        File.WriteAllText(csv.Path,
            """
            File,FormID,VoiceType,Speaker,Quest,Source,Text
            sound\voice\falloutnv.esm\maleadult01default\tempvdialogueulysses_greeting_00133fcd_1.xma,00133FCD,maleadult01default,,,whisper,"about time, a new face."
            sound\voice\falloutnv.esm\maleadult01default\tempvdialogueulysses_greeting_00137139_1.xma,00137139,maleadult01default,,,whisper,Save the talk.
            """);

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = DialogueAudioCsvAssetCollector.CollectFromCsv(
            csv.Path,
            new HashSet<uint> { 0x00133FCD },
            paths);

        Assert.Equal(2, result.RowsRead);
        Assert.Equal(1, result.RowsMatched);
        Assert.Equal(2, result.PathsAdded);
        Assert.Contains(
            "sound\\voice\\falloutnv.esm\\maleadult01default\\tempvdialogueulysses_greeting_00133fcd_1.ogg",
            paths);
        Assert.Contains(
            "sound\\voice\\falloutnv.esm\\maleadult01default\\tempvdialogueulysses_greeting_00133fcd_1.lip",
            paths);
        Assert.DoesNotContain(
            "sound\\voice\\falloutnv.esm\\maleadult01default\\tempvdialogueulysses_greeting_00133fcd_1.xma",
            paths);
        Assert.DoesNotContain(
            "sound\\voice\\falloutnv.esm\\maleadult01default\\tempvdialogueulysses_greeting_00137139_1.ogg",
            paths);
    }

    [Fact]
    public void CollectFromCsv_HandlesQuotedMultilineText()
    {
        using var csv = new TempFile();
        File.WriteAllText(csv.Path,
            "File,FormID,VoiceType,Speaker,Quest,Source,Text\r\n" +
            "sound\\voice\\falloutnv.esm\\creatureghoul\\tempvdialoguej_greeting_0008d1b4_1.xma," +
            "0008D1B4,creatureghoul,,Jason Bright's Dialog,whisper,\"Hello,\r\nWanderer.\"\r\n");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = DialogueAudioCsvAssetCollector.CollectFromCsv(
            csv.Path,
            new HashSet<uint> { 0x0008D1B4 },
            paths);

        Assert.Equal(1, result.RowsRead);
        Assert.Equal(1, result.RowsMatched);
        Assert.Contains(
            "sound\\voice\\falloutnv.esm\\creatureghoul\\tempvdialoguej_greeting_0008d1b4_1.ogg",
            paths);
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"dialogue-audio-csv-{Guid.NewGuid():N}.csv");

        public void Dispose()
        {
            try
            {
                if (File.Exists(Path))
                {
                    File.Delete(Path);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
