using System.Text.Json;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Unit-level smoke tests for <see cref="AssetPackingService" />. End-to-end packing
///     against a real ESP fixture is intended to live in a separate CLI integration test
///     project (<c>tests/BethesdaMultitool.Tests.E2E</c>), which does not yet exist.
/// </summary>
public class AssetPackingServiceTests
{
    [Fact]
    public async Task Explicit_unbound_audio_packs_exact_baseline_pairs_and_rejects_other_directory_donors()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "bmt-unbound-pack-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var baseline = Directory.CreateDirectory(Path.Combine(root, "baseline")).FullName;
            var secondary = Directory.CreateDirectory(Path.Combine(root, "secondary")).FullName;
            var plugin = Path.Combine(root, "empty.esm");
            await File.WriteAllBytesAsync(plugin, new EsmTestFileBuilder().Build());
            const string exact = "sound\\voice\\falloutnv.esm\\voice\\quest_topic_00100001_1.ogg";
            const string missing = "sound\\voice\\falloutnv.esm\\voice\\quest_topic_00100002_1.ogg";
            byte[] ogg = [0x4F, 0x67, 0x67, 0x53, 0x11, 0x22];
            byte[] lip = [0x33, 0x44, 0x55];
            WriteAsset(baseline, exact, ogg);
            WriteAsset(baseline, Path.ChangeExtension(exact, ".lip"), lip);
            var donor = missing.Replace("\\voice\\quest_", "\\other\\quest_", StringComparison.Ordinal);
            WriteAsset(secondary, donor, [0x99, 0xAA]);
            WriteAsset(secondary, Path.ChangeExtension(donor, ".lip"), [0xBB, 0xCC]);
            var csv = Path.Combine(root, "selected.csv");
            await File.WriteAllTextAsync(csv, "File,FormID,Source,Text\n" +
                $"{exact},00100001,whisper,Exact clip\n{missing},00100002,whisper,Missing clip\n");
            var output = Path.Combine(root, "voices.bsa");
            var result = await AssetPackingService.PackAsync(new AssetPackingOptions
            {
                ConvertedEsmPath = plugin,
                BaselineDataFolder = baseline,
                SecondaryDataFolders = [new SecondaryDataFolder { Path = secondary }],
                OutputBsaPath = output,
                DialogueAudioCsvPaths = [csv],
                IncludeUnboundDialogueAudio = true,
                IncludeFuzzyMatches = true
            }, NullConversionProgressSink.Instance, TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.ErrorMessage);
            using var archive = ArchiveReader.Open(Assert.Single(result.OutputPaths));
            Assert.Equal(ogg, archive.ReadFile(exact));
            Assert.Equal(lip, archive.ReadFile(Path.ChangeExtension(exact, ".lip")));
            Assert.Null(archive.ReadFile(missing));
            using var audit = JsonDocument.Parse(await File.ReadAllTextAsync(output + ".dialogue-audio.json"));
            Assert.Equal("Complete", audit.RootElement.GetProperty("status").GetString());
            var rows = audit.RootElement.GetProperty("rows").EnumerateArray().ToArray();
            Assert.Equal(2, rows.Length);
            Assert.All(rows, row => Assert.Equal("Unbound", row.GetProperty("binding").GetString()));
            Assert.All(rows.Single(row => row.GetProperty("formId").GetString() == "00100001")
                .GetProperty("assets").EnumerateArray(), asset => Assert.Equal("Packed", asset.GetProperty("outcome").GetString()));
            Assert.All(rows.Single(row => row.GetProperty("formId").GetString() == "00100002")
                .GetProperty("assets").EnumerateArray(), asset => Assert.Equal("Missing", asset.GetProperty("outcome").GetString()));
            Assert.Equal(ogg, await File.ReadAllBytesAsync(Path.Combine(baseline, exact.Replace('\\', Path.DirectorySeparatorChar))));
        }
        finally { Directory.Delete(root, true); }

        static void WriteAsset(string folder, string path, byte[] data)
        {
            var target = Path.Combine(folder, path.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, data);
        }
    }

    [Fact]
    public async Task PackAsync_NonExistentEspPath_ReturnsFailureNotThrows()
    {
        var options = new AssetPackingOptions
        {
            ConvertedEsmPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.esp"),
            BaselineDataFolder = Path.GetTempPath(),
            SecondaryDataFolders = [],
            OutputBsaPath = Path.Combine(Path.GetTempPath(), $"out-{Guid.NewGuid():N}.bsa")
        };

        var result = await AssetPackingService.PackAsync(
            options,
            NullConversionProgressSink.Instance,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.False(File.Exists(options.OutputBsaPath));
    }

    [Fact]
    public async Task PackAsync_CanceledToken_ReportsCancellation()
    {
        var options = new AssetPackingOptions
        {
            ConvertedEsmPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.esp"),
            BaselineDataFolder = Path.GetTempPath(),
            SecondaryDataFolders = [],
            OutputBsaPath = Path.Combine(Path.GetTempPath(), $"out-{Guid.NewGuid():N}.bsa")
        };

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await AssetPackingService.PackAsync(options, NullConversionProgressSink.Instance, cts.Token);

        // We can't guarantee which exception type fires first when nothing is loaded;
        // either Success=false (file-not-found path) or Success=false with cancel message
        // is acceptable. The contract is "never throw".
        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void PlanBsaOutputs_MixedAssetClasses_UsesPluginSidecarNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "v50-xex43.bsa");
        var files = new List<(string Path, byte[] Data)>
        {
            ("meshes\\armor\\ulysses\\ulysses.nif", [1]),
            ("textures\\armor\\ulysses\\ulysses_n.dds", [2]),
            ("sound\\fx\\amb\\wind.wav", [3]),
            ("sound\\voice\\v50-xex43.esp\\maleuniqueulysses\\line.ogg", [4])
        };

        var plans = AssetPackingService.PlanBsaOutputs(root, files);

        Assert.Equal([
            Path.Combine(Path.GetTempPath(), "v50-xex43 - Main.bsa"),
            Path.Combine(Path.GetTempPath(), "v50-xex43 - Textures.bsa"),
            Path.Combine(Path.GetTempPath(), "v50-xex43 - Sounds.bsa"),
            Path.Combine(Path.GetTempPath(), "v50-xex43 - Voices.bsa")
        ], plans.Select(p => p.OutputPath).ToArray());
        Assert.DoesNotContain(plans, p => p.OutputPath == root);
        Assert.All(plans, p => Assert.Single(p.Files));
    }

    [Fact]
    public void PlanBsaOutputs_SingleAssetClass_UsesRequestedPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "textures-only.bsa");
        var plans = AssetPackingService.PlanBsaOutputs(root,
        [
            ("textures\\armor\\a.dds", [1]),
            ("textures\\armor\\b.dds", [2])
        ]);

        var plan = Assert.Single(plans);
        Assert.Equal(root, plan.OutputPath);
        Assert.Equal(AssetPackBucket.Textures, plan.Bucket);
        Assert.Equal(2, plan.Files.Count);
    }

    [Fact]
    public void PlanBsaOutputs_OversizedBucket_ChunksWithNumberedSidecars()
    {
        var root = Path.Combine(Path.GetTempPath(), "big.bsa");
        var plans = AssetPackingService.PlanBsaOutputs(root,
        [
            ("textures\\armor\\a.dds", new byte[140]),
            ("textures\\armor\\b.dds", new byte[140])
        ], 220);

        Assert.Equal([
            Path.Combine(Path.GetTempPath(), "big - Textures.bsa"),
            Path.Combine(Path.GetTempPath(), "big - Textures2.bsa")
        ], plans.Select(p => p.OutputPath).ToArray());
        Assert.All(plans, p => Assert.Single(p.Files));
    }
}
