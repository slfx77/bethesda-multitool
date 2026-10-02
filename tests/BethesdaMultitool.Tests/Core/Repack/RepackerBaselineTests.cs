using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.Core.Repack;
using BethesdaMultitool.Core.Repack.Processors;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Repack;

/// <summary>
///     The first tests for <c>Core/Repack/**</c>, which had none. They freeze what the repacker does today
///     rather than what it should do: M2.6 is a parity milestone, and there is nothing to compare a change
///     against until the current behaviour is written down.
/// </summary>
/// <remarks>
///     <para>
///         Two of these pinned a defect and have now been <b>inverted</b> rather than deleted, which is
///         what the earlier versions said had to happen. <c>EsmProcessor</c> used to hand every file it
///         found to a converter that byte-swaps unconditionally, with no endianness check — the one
///         <c>EsmConvertCommand</c> had always performed before constructing that same converter. A
///         simple already-PC-format plugin came back swapped into console format and counted as
///         converted; the retail <c>update.esp</c> made the swap fail partway, the exception became a
///         progress <c>Message</c> the CLI never prints, nothing was written, and the run still reported
///         success because <c>RepackerService</c> set it without consulting any phase.
///     </para>
///     <para>
///         Both halves are fixed. A plugin already in PC byte order is copied through unchanged, and a
///         phase that fails now reaches <c>RepackResult.Success</c> and <c>Error</c>. The cases below
///         assert the repaired behaviour and keep the old one described, so what changed stays legible.
///         Whole-tree agreement with the 4.8 GB retail console build carries the real-asset guard,
///         because no synthetic fixture can stand in for it.
///     </para>
/// </remarks>
public sealed class RepackerBaselineTests
{
    private const string Fixture = @"Builds\Fallout - New Vegas (2010-8-22, X360 - Final)";

    /// <summary>Builds a plugin whose leading bytes declare the given endianness, and nothing more.</summary>
    /// <param name="bigEndian">Whether to write the byte-swapped console signature.</param>
    /// <returns>A record header plus one short subrecord, in the requested byte order.</returns>
    private static byte[] Plugin(bool bigEndian)
    {
        var bytes = new List<byte>();
        // "4SET" is "TES4" with its bytes reversed, which is exactly how EsmParser tells the two apart.
        bytes.AddRange(Encoding.ASCII.GetBytes(bigEndian ? "4SET" : "TES4"));
        bytes.AddRange(bigEndian ? [0, 0, 0, 0x0C] : new byte[] { 0x0C, 0, 0, 0 });
        bytes.AddRange(new byte[16]);
        bytes.AddRange(Encoding.ASCII.GetBytes(bigEndian ? "RDEH" : "HEDR"));
        bytes.AddRange(bigEndian ? [0, 0x0C] : new byte[] { 0x0C, 0 });
        bytes.AddRange(new byte[12]);
        return bytes.ToArray();
    }

    /// <summary>Creates an owned temporary source tree holding a Data folder with one plugin.</summary>
    /// <param name="fileName">The plugin's file name inside Data.</param>
    /// <param name="content">Its bytes.</param>
    /// <returns>The source root.</returns>
    private static string SourceWith(string fileName, byte[] content)
    {
        var root = Path.Combine(Path.GetTempPath(), "bmt-repack-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        File.WriteAllBytes(Path.Combine(root, "Data", fileName), content);
        // ValidateSourceFolder wants both of these before any phase runs.
        File.WriteAllBytes(Path.Combine(root, "default.xex"), [0x58, 0x45, 0x58, 0x32]);
        if (fileName != "FalloutNV.esm")
            File.WriteAllBytes(Path.Combine(root, "Data", "FalloutNV.esm"), Plugin(bigEndian: true));

        return root;
    }

    /// <summary>Deletes only a directory this class created.</summary>
    /// <param name="directories">Paths that must lie under this class's owned temporary root.</param>
    private static void Discard(params string[] directories)
    {
        var owned = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bmt-repack-tests"));
        foreach (var directory in directories)
        {
            if (!Path.GetFullPath(directory)
                    .StartsWith(owned + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Temporary test path escaped its owned root.");

            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Runs one ESM or ESP phase over a source tree and returns what it did.</summary>
    /// <param name="source">A source root built by <see cref="SourceWith" />.</param>
    /// <param name="isEsp">Whether to run the ESP variant.</param>
    /// <returns>The processed count, the destination path, and every progress message reported.</returns>
    private static async Task<(int Processed, string Destination, List<string> Messages)> RunPluginPhase(
        string source, bool isEsp)
    {
        var messages = new List<string>();
        var output = Path.Combine(source, "out");
        var processed = await new EsmProcessor(isEsp).ProcessAsync(
            new RepackerOptions { SourceFolder = source, OutputFolder = output },
            new MessageCollector(messages),
            TestContext.Current.CancellationToken);
        return (processed, Path.Combine(output, "Data", isEsp ? "update.esp" : "FalloutNV.esm"), messages);
    }

    // Progress<T> posts callbacks asynchronously, so awaiting ProcessAsync does not mean its
    // callbacks have populated the assertion evidence. Capture each report before Report returns.
    private sealed class MessageCollector(List<string> messages) : IProgress<RepackerProgress>
    {
        public void Report(RepackerProgress value)
        {
            if (value.Message is not null) messages.Add(value.Message);
        }
    }

    [Fact]
    public async Task AnAlreadyPcFormatPluginIsCopiedThroughByteForByte()
    {
        // Inverted from the case that pinned the defect. This input is already in PC byte order, so
        // there is nothing to convert: it must arrive identical. Before the guard it came back as
        // 4SET - byte-swapped into console format - and was counted as converted.
        var original = Plugin(bigEndian: false);
        var source = SourceWith("update.esp", original);
        try
        {
            var (processed, destination, messages) = await RunPluginPhase(source, isEsp: true);

            Assert.Equal(1, processed);
            var copied = await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken);

            Assert.Equal("TES4", Encoding.ASCII.GetString(copied, 0, 4));
            Assert.Equal(original, copied);
            Assert.DoesNotContain(messages, message => message.Contains("Failed", StringComparison.Ordinal));
            Assert.Contains(messages, message => message.Contains("already in PC byte order",
                StringComparison.Ordinal));
        }
        finally
        {
            Discard(source);
        }
    }

    [Fact]
    public async Task AConsoleFormatPluginConvertsToPcFormat_WhichIsTheControl()
    {
        // Without this the previous case could just mean the ESP path never works, and freezing it would
        // say nothing about direction.
        var source = SourceWith("update.esp", Plugin(bigEndian: true));
        try
        {
            var (processed, destination, _) = await RunPluginPhase(source, isEsp: true);

            Assert.Equal(1, processed);
            var converted = await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken);
            Assert.Equal("TES4", Encoding.ASCII.GetString(converted, 0, 4));
        }
        finally
        {
            Discard(source);
        }
    }

    [Trait("Category", BucketBTestGuard.Category)]
    [Fact]
    public async Task TheRetailPluginIsCarriedThroughUnchanged()
    {
        // Inverted from the case that pinned the retail symptom. update.esp begins TES4 while the
        // master beside it begins 4SET, so the guard copies it instead of swapping it. Before the
        // guard, the unconditional swap walked its real subrecord lengths, failed partway, and the
        // processor turned that into a progress Message the CLI never prints while writing nothing -
        // and the run still reported success.
        BucketBTestGuard.SkipUnlessEnabled();
        var fixture = RealAssetPaths.SampleDirectory(Fixture);
        Assert.SkipWhen(fixture is null, RealAssetPaths.SkipMessage(Fixture));

        var output = Path.Combine(Path.GetTempPath(), "bmt-repack-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(output);
            var messages = new List<string>();
            var result = await RepackerService.RepackAsync(
                new RepackerOptions
                {
                    SourceFolder = fixture!,
                    OutputFolder = output,
                    ProcessVideo = false,
                    ProcessMusic = false,
                    ProcessBsa = false,
                    ProcessMenus = false,
                    ProcessEsm = false,
                    ProcessIni = false,
                    ProcessEsp = true
                },
                new Progress<RepackerProgress>(report =>
                {
                    if (report.Message is not null) messages.Add(report.Message);
                }),
                TestContext.Current.CancellationToken);

            TestContext.Current.TestOutputHelper?.WriteLine(string.Join("\n", messages));

            Assert.Equal(1, result.EspFilesProcessed);
            var converted = Path.Combine(output, "Data", "update.esp");
            Assert.True(File.Exists(converted),
                "The retail console build's only plugin must reach the converted build.");
            Assert.DoesNotContain(messages,
                message => message.Contains("Failed to convert", StringComparison.Ordinal));

            // The retail plugin ships already in PC byte order, so it is copied rather than converted
            // and must be byte-identical to the source.
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(fixture!, "Data", "update.esp"),
                    TestContext.Current.CancellationToken),
                await File.ReadAllBytesAsync(converted, TestContext.Current.CancellationToken));
            Assert.True(result.Success);
            Assert.Null(result.Error);
        }
        finally
        {
            Discard(output);
        }
    }

    [Fact]
    public void ValidationRejectsAFolderMissingEitherRequiredFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "bmt-repack-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        try
        {
            Assert.Contains("default.xex", RepackerService.ValidateSourceFolder(root).Message,
                StringComparison.Ordinal);

            File.WriteAllBytes(Path.Combine(root, "default.xex"), [0x58, 0x45, 0x58, 0x32]);
            Assert.Contains("FalloutNV.esm", RepackerService.ValidateSourceFolder(root).Message,
                StringComparison.Ordinal);

            File.WriteAllBytes(Path.Combine(root, "Data", "FalloutNV.esm"), Plugin(bigEndian: true));
            Assert.True(RepackerService.ValidateSourceFolder(root).IsValid);
        }
        finally
        {
            Discard(root);
        }
    }

    [Trait("Category", BucketBTestGuard.Category)]
    [Fact]
    public void TheRetailConsoleMasterIsBigEndianAndItsPluginIsNot()
    {
        // The premise the synthetic defect cases stand on: this console build really does ship a
        // PC-format plugin beside a console-format master. If a future fixture changes that, those cases
        // are modelling something that no longer happens and must be revisited rather than kept.
        BucketBTestGuard.SkipUnlessEnabled();
        var fixture = RealAssetPaths.SampleDirectory(Fixture);
        Assert.SkipWhen(fixture is null, RealAssetPaths.SkipMessage(Fixture));

        Assert.Equal("4SET", Head(Path.Combine(fixture!, "Data", "FalloutNV.esm")));
        Assert.Equal("TES4", Head(Path.Combine(fixture!, "Data", "update.esp")));
    }

    [Trait("Category", BucketBTestGuard.Category)]
    [Theory]
    [InlineData("menus-nodonor")]
    [InlineData("esp")]
    [InlineData("bsa-misc")]
    public async Task APhaseMatchesItsFrozenBaselineByteForByte(string phase)
    {
        // The heavy phases - esm, video and music - are in the frozen artifact but not re-derived here:
        // together they are a gigabyte of output, and the music hashes are bound to an ffmpeg build rather
        // than to this code. The artifact records them; this case re-derives only what is cheap to re-derive.
        BucketBTestGuard.SkipUnlessEnabled();
        var fixture = RealAssetPaths.SampleDirectory(Fixture);
        Assert.SkipWhen(fixture is null, RealAssetPaths.SkipMessage(Fixture));

        var expected = Baseline(phase);
        var output = Path.Combine(Path.GetTempPath(), "bmt-repack-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(output);
            var result = await RepackerService.RepackAsync(
                Options(fixture!, output, phase), new Progress<RepackerProgress>(),
                TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Error);

            Assert.Equal(expected, Hashes(output));
        }
        finally
        {
            Discard(output);
        }
    }

    /// <summary>Reads the frozen per-file hashes for one phase from the tracked baseline artifact.</summary>
    /// <param name="phase">The phase key recorded in the artifact.</param>
    /// <returns>Relative path to hash, ordered by path.</returns>
    private static SortedDictionary<string, string> Baseline(string phase)
    {
        var path = Path.Combine(SourceContract.RepoRoot, "docs", "repack-baseline-2026-09-12.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var files = document.RootElement.GetProperty("phases").GetProperty(phase).GetProperty("files");
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files.EnumerateArray())
            result[file.GetProperty("path").GetString()!] = file.GetProperty("sha256").GetString()!;

        return result;
    }

    /// <summary>Hashes every file an output tree contains, keyed by its forward-slash relative path.</summary>
    /// <param name="root">The output tree root.</param>
    /// <returns>Relative path to hash, ordered by path.</returns>
    private static SortedDictionary<string, string> Hashes(string root)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            using var stream = File.OpenRead(file);
            result[Path.GetRelativePath(root, file).Replace('\\', '/')] =
                Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        return result;
    }

    /// <summary>Rebuilds the exact option set the baseline artifact records for one phase.</summary>
    /// <param name="source">The console installation root.</param>
    /// <param name="output">A fresh output directory.</param>
    /// <param name="phase">The phase key.</param>
    /// <returns>Options matching the recorded arguments.</returns>
    private static RepackerOptions Options(string source, string output, string phase)
    {
        return new RepackerOptions
        {
            SourceFolder = source,
            OutputFolder = output,
            ProcessVideo = false,
            ProcessMusic = false,
            ProcessBsa = phase == "bsa-misc",
            SelectedBsaFiles = phase == "bsa-misc"
                ? new HashSet<string>(["Fallout - Misc.bsa"], StringComparer.OrdinalIgnoreCase)
                : null,
            ProcessMenus = phase == "menus-nodonor",
            ProcessEsm = false,
            ProcessEsp = phase == "esp"
            // ProcessIni is left at its default, exactly as the CLI leaves it: it has no toggle, so every
            // recorded phase also emits Fallout_default.ini and the baseline contains it.
        };
    }

    /// <summary>Reads a file's four-byte leading signature.</summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The signature, or an empty string if the file is shorter than four bytes.</returns>
    private static string Head(string path)
    {
        var head = new byte[4];
        using var stream = File.OpenRead(path);
        return stream.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4
            ? Encoding.ASCII.GetString(head)
            : string.Empty;
    }
}
