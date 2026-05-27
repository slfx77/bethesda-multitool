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
    public void CollectFromCsv_RewritesPackPathForRemappedInfoFormId()
    {
        using var csv = new TempFile();
        File.WriteAllText(csv.Path,
            """
            File,FormID,VoiceType,Speaker,Quest,Source,Text
            sound\voice\falloutnv.esm\maleadult01default\tempvdialogueulysses_greeting_00133fcd_1.xma,00133FCD,maleadult01default,,,whisper,about time
            """);

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // INFO with source FormID 0x00133FCD was remapped to allocated 0x01001234 — the
        // engine looks for the file under v37-xex43.esp\... with the allocated bottom-24-bit
        // FormID 001234 in the filename.
        var remap = new Dictionary<uint, uint> { [0x00133FCDu] = 0x01001234u };
        var result = DialogueAudioCsvAssetCollector.CollectFromCsv(
            csv.Path,
            new HashSet<uint> { 0x00133FCD },
            paths,
            remap,
            "v37-xex43.esp",
            renames);

        Assert.Equal(1, result.RowsRead);
        Assert.Equal(1, result.RowsMatched);
        Assert.Equal(1, result.PathsRewrittenForNewEsp);

        // The resolveAs path stays master-shaped so the data-folder resolver still finds
        // the .xma bytes via extension swap.
        Assert.Contains(
            "sound\\voice\\falloutnv.esm\\maleadult01default\\tempvdialogueulysses_greeting_00133fcd_1.ogg",
            paths);

        // The pack-path rename redirects the BSA entry to the engine-shaped path.
        Assert.True(renames.TryGetValue(
            "sound\\voice\\falloutnv.esm\\maleadult01default\\tempvdialogueulysses_greeting_00133fcd_1.ogg",
            out var renamedOgg));
        Assert.Equal(
            "sound\\voice\\v37-xex43.esp\\maleadult01default\\tempvdialogueulysses_greeting_00001234_1.ogg",
            renamedOgg);
    }

    [Fact]
    public void RewritePathForNewEsp_PreservesNonRemappedPath()
    {
        // No remap, no ESP filename: paths pass through unchanged.
        var input = "sound\\voice\\falloutnv.esm\\maleadult01default\\tempvdialogueulysses_greeting_00133fcd_1.xma";
        var requests = DialogueAudioCsvAssetCollector.ExpandDialogueAudioRequests(input).ToList();

        Assert.Equal(2, requests.Count);
        foreach (var (resolveAs, packAs) in requests)
        {
            Assert.Equal(resolveAs, packAs);
        }
    }

    [Fact]
    public void RewritePathForNewEsp_OnlySwapsRecognizedFormIdToken()
    {
        // The filename's formid token must exactly match the source FormID — defensive
        // guard against rewriting filenames with a different shape (no underscore,
        // mismatched hex). Here the source FormID is 0xCAFE0BAD but the filename has
        // 0x00133FCD, so the rewriter should leave the filename alone (no rewrite).
        var result = DialogueAudioCsvAssetCollector.RewritePathForNewEsp(
            "sound\\voice\\falloutnv.esm\\voicetype\\stem_00133fcd_1.xma",
            sourceFormId: 0xCAFE0BADu,
            allocatedFormId: 0x01005678u,
            outputEspFileName: "out.esp");

        // The dir gets rewritten (esm → esp), but the filename stays the same since the
        // source token doesn't match.
        Assert.Equal(
            "sound\\voice\\out.esp\\voicetype\\stem_00133fcd_1.xma",
            result);
    }

    [Fact]
    public void TryExtractTripleFromPath_ParsesCanonicalCsvPath()
    {
        // April-CSV shape: sound\voice\falloutnv.esm\<vt>\<topic_edid>_<fid8>_<resp>.xma
        var ok = DialogueAudioCsvAssetCollector.TryExtractTripleFromPath(
            "sound\\voice\\falloutnv.esm\\maleadult01default\\tempvdialogueulysses_greeting_00133fcd_1.xma",
            out var triple);

        Assert.True(ok);
        Assert.Equal("maleadult01default", triple.Voice);
        Assert.Equal("tempvdialogueulysses_greeting", triple.Topic);
        Assert.Equal((byte)1, triple.Resp);
    }

    [Fact]
    public void TryExtractTripleFromPath_RejectsMissingResponseNumber()
    {
        var ok = DialogueAudioCsvAssetCollector.TryExtractTripleFromPath(
            "sound\\voice\\falloutnv.esm\\maleadult01default\\stem.xma",
            out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryExtractTripleFromPath_RejectsNonVoicePath()
    {
        var ok = DialogueAudioCsvAssetCollector.TryExtractTripleFromPath(
            "meshes\\armor\\armor_00133fcd_1.nif",
            out _);

        Assert.False(ok);
    }

    [Fact]
    public void CollectFromCsv_MatchesViaTripleKeyFallbackWhenFormIdDrifted()
    {
        // CSV references an April-era FormID (0x00133FCD) that doesn't exist in our remap
        // (no xex21-runtime ID matches). The triple-key index says the same line was
        // emitted under our allocated FormID 0x010040AB. The pack-path rewrite should
        // redirect onto the new ESP shape using the binding's allocated FormID.
        using var csv = new TempFile();
        File.WriteAllText(csv.Path,
            """
            File,FormID,VoiceType,Speaker,Quest,Source,Text
            sound\voice\falloutnv.esm\maleadult01default\tempvdialogueulysses_greeting_00133fcd_1.xma,00133FCD,maleadult01default,,,whisper,About time.
            """);

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Empty dialogue FormID set + empty remap → the FormID-first path can't match.
        var dialogueFormIds = new HashSet<uint>();
        var bindings = new List<EmittedDialogueAudioBinding>
        {
            new()
            {
                AllocatedInfoFormId = 0x010040ABu,
                ParentDialEditorId = "tempvdialogueulysses_greeting",
                VoiceTypeEditorId = "maleadult01default",
                ResponseNumber = 1
            }
        };
        var tripleIndex = DialogueAudioCsvAssetCollector.BuildAudioBindingTripleIndex(bindings);

        var result = DialogueAudioCsvAssetCollector.CollectFromCsv(
            csv.Path, dialogueFormIds, paths,
            newRecordSourceToAllocated: null,
            outputEspFileName: "v39-xex21.esp",
            packPathRenames: renames,
            bindingsByTriple: tripleIndex);

        Assert.Equal(1, result.RowsRead);
        Assert.Equal(1, result.RowsMatched);
        Assert.Equal(1, result.PathsRewrittenViaTriple);

        // resolveAs path stays master-shaped so the data-folder resolver finds bytes.
        Assert.Contains(
            "sound\\voice\\falloutnv.esm\\maleadult01default\\tempvdialogueulysses_greeting_00133fcd_1.ogg",
            paths);

        // pack rename redirects onto v39-xex21.esp using the binding's allocated FormID
        // (bottom 24 bits = 0x0040AB → "000040ab").
        Assert.True(renames.TryGetValue(
            "sound\\voice\\falloutnv.esm\\maleadult01default\\tempvdialogueulysses_greeting_00133fcd_1.ogg",
            out var renamed));
        Assert.Equal(
            "sound\\voice\\v39-xex21.esp\\maleadult01default\\tempvdialogueulysses_greeting_000040ab_1.ogg",
            renamed);
    }

    [Fact]
    public void CollectFromCsv_PrefersFormIdMatchOverTripleFallback()
    {
        // When the CSV's FormID matches an emitted INFO directly via remap, we use that
        // path — the triple-key fallback only kicks in on FormID miss.
        using var csv = new TempFile();
        File.WriteAllText(csv.Path,
            """
            File,FormID,VoiceType,Speaker,Quest,Source,Text
            sound\voice\falloutnv.esm\maleadult01default\stem_aaaabbbb_1.xma,AAAABBBB,maleadult01default,,,,,
            """);

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dialogueFormIds = new HashSet<uint> { 0xAAAABBBBu };
        var remap = new Dictionary<uint, uint> { [0xAAAABBBBu] = 0x01001111u };
        // A binding exists for the same (vt, topic, resp) but with a DIFFERENT allocated
        // FormID. The FormID-first path must win and use 0x01001111, not 0x02002222.
        var bindings = new List<EmittedDialogueAudioBinding>
        {
            new()
            {
                AllocatedInfoFormId = 0x02002222u,
                ParentDialEditorId = "stem",
                VoiceTypeEditorId = "maleadult01default",
                ResponseNumber = 1
            }
        };

        var result = DialogueAudioCsvAssetCollector.CollectFromCsv(
            csv.Path, dialogueFormIds, paths,
            remap,
            "v39-out.esp",
            renames,
            DialogueAudioCsvAssetCollector.BuildAudioBindingTripleIndex(bindings));

        Assert.Equal(1, result.RowsMatched);
        Assert.Equal(0, result.PathsRewrittenViaTriple);
        Assert.True(renames.TryGetValue(
            "sound\\voice\\falloutnv.esm\\maleadult01default\\stem_aaaabbbb_1.ogg",
            out var renamed));
        // FormID-first remap used the allocated FormID 0x01001111 (bottom 24 = 0x001111).
        Assert.Contains("v39-out.esp", renamed);
        Assert.Contains("00001111", renamed);
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
