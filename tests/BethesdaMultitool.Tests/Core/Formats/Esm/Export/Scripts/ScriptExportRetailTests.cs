using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.CLI.Commands.Export;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Export.Scripts;
using BethesdaMultitool.Tests.Helpers;
using Spectre.Console;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Export.Scripts;

/// <summary>
///     <c>export scripts</c> over real New Vegas plugins, through the command's own selection, source description
///     and writer (<see cref="ExportScriptsCommand.ExportLoaded" />) on the shared cached parse. Every exported
///     source file is compared with the SCTX subrecord bytes read by <see cref="ReadScptIndependently" />, a
///     test-local walker of the SCPT top-level group that never touches the tool's parser; the manifest's
///     <c>recordOffset</c> must name the record that walker found.
///     <para>
///         Pinned values were first read off the files with a read-only mmap census (Python, no memory dumps):
///         <list type="bullet">
///             <item>
///                 2022-5-24 Steam FalloutNV.esm: little-endian container, HEDR 1.34, no masters; 2,576 SCPT, every
///                 one with EDID, SCHR-era SCDA opening <c>1D 00 00 00</c> and one SCTX; no compressed or deleted
///                 SCPT, no XXXX, no NUL in any SCTX, no BOM; 2,567 SCTX with CRLF line breaks and 9 with none;
///                 2,963,844 SCTX bytes in all. EditorIDs are unique ignoring case and file-name safe, at most 52
///                 characters. The only non-ASCII script is 0x001746C1 Lucky38MrHouseTerminalCodeSCRIPT (record at
///                 1,185,381): 1,853 SCTX bytes holding TWO 0x92 bytes ("; Caesar\x92s Legion" at 671 and 1,533),
///                 SHA-256 cf1ba8d9…6d69.
///             </item>
///             <item>
///                 2010-7-21 Xbox 360 prototype FalloutNV.esm: BIG-endian container ("4SET", "XTCS" + big-endian
///                 u16 lengths), HEDR 1.32, no masters; 2,487 SCPT, all with SCTX and with SCDA opening
///                 <c>1D 00 00 00</c> (little-endian bytecode inside the big-endian container); 2,476 CRLF and 11
///                 with no line break; 2,544,712 SCTX bytes. Its Lucky38MrHouseTerminalCodeSCRIPT is 436 bytes at
///                 record 961,062 with no 0x92.
///             </item>
///             <item>
///                 2011-2-15 Xbox 360 prototype DLC plugins, both LITTLE-endian containers with master
///                 FalloutNV.esm: DeadMansHand.esm 323 SCPT (486,171 SCTX bytes), DeadMoney.esm 347 (521,861), every
///                 SCTX CRLF.
///             </item>
///         </list>
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", TestCategories.BucketB)]
public sealed class ScriptExportRetailTests : IDisposable
{
    private const string BannerFirstLine =
        "; Reconstruction from SCDA — BethesdaMultitool\r\n";

    private static readonly DateTimeOffset CreatedUtc = new(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bmt_script_export_retail_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public async Task Retail_2022_exports_all_scripts_verbatim()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esm = RealAssetPaths.NewVegasBuilds.Steam2022();
        Assert.SkipUnless(esm is not null, RealAssetPaths.SkipMessage("FalloutNV.esm (2022-5-24 Steam build)"));

        var raw = ReadScptIndependently(esm);
        Assert.False(raw.BigEndian);
        Assert.Equal(2576, raw.Scripts.Count);

        // Never disposed: RealAssetEsmCache owns the result and shares it across this collection.
        var loaded = await RealAssetEsmCache.LoadAsync(esm, TestContext.Current.CancellationToken);
        var outDir = Path.Combine(_root, "retail-2022");
        var outcome = RunExport(loaded, outDir, "missing");

        var summary = Assert.IsType<ScriptExportSummary>(outcome.Summary);
        Assert.Equal(2576, summary.ScriptCount);
        Assert.Equal(2576, summary.StoredSourceFiles);
        Assert.Equal(0, summary.CapturedSourceFiles);
        Assert.Equal(0, summary.DecompiledFiles);
        Assert.Equal(0, summary.SkippedScripts);

        // Every script has SCTX, so the default policy writes no reconstruction at all.
        var names = Directory.GetFiles(outDir).Select(path => Path.GetFileName(path)).ToList();
        Assert.Equal(2577, names.Count);
        Assert.Equal(2576, names.Count(name => name.EndsWith(".gek", StringComparison.Ordinal)));
        Assert.DoesNotContain(names, name => name.EndsWith(".decompiled.gek", StringComparison.Ordinal));
        Assert.Contains("scripts.manifest.json", names);

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "scripts.manifest.json")));
        var root = manifest.RootElement;
        var source = root.GetProperty("source");
        Assert.Equal("plugin", source.GetProperty("kind").GetString());
        Assert.Equal("FalloutNewVegas", source.GetProperty("game").GetString());
        Assert.Equal("little", source.GetProperty("containerEndianness").GetString());
        Assert.Equal(0, source.GetProperty("plugin").GetProperty("masters").GetArrayLength());
        Assert.Equal(new FileInfo(esm).Length, source.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(FileSha256(esm), source.GetProperty("sha256").GetString());
        AssertCorpusLabel(esm, "Fallout - New Vegas (2022-5-24, Steam - Final)", source);

        var census = AssertEveryScriptIsVerbatim(root, raw, outDir, ".gek");
        Assert.Equal(2963844L, census.TotalBytes);
        Assert.Equal(2567, census.LineEndings.GetValueOrDefault("crlf"));
        Assert.Equal(9, census.LineEndings.GetValueOrDefault("none"));
        Assert.Equal(2576, census.LittleEndianBytecode);

        // The one non-ASCII script, byte for byte: both 0x92 survive (a UTF-8 writer gives E2 80 99).
        var lucky38 = FindEntry(root, "0x001746C1");
        Assert.Equal("Lucky38MrHouseTerminalCodeSCRIPT", lucky38.GetProperty("stem").GetString());
        Assert.Equal(1185381L, lucky38.GetProperty("recordOffset").GetInt64());
        var lucky38Bytes = File.ReadAllBytes(Path.Combine(outDir, "Lucky38MrHouseTerminalCodeSCRIPT.gek"));
        Assert.Equal(1853, lucky38Bytes.Length);
        Assert.Equal(2, lucky38Bytes.Count(b => b == 0x92));
        Assert.DoesNotContain(lucky38Bytes, b => b == 0xE2);
        Assert.Equal(
            "cf1ba8d95b4a8cf54874867bfac5a99efd691424ab79b5c8499553b2665b6d69",
            Convert.ToHexStringLower(SHA256.HashData(lucky38Bytes)));
        Assert.Equal(raw.Scripts[0x001746C1].Sctx, lucky38Bytes);
    }

    [Fact]
    public async Task July_2010_X360_exports_verbatim_sources_and_little_endian_reconstructions()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esm = RealAssetPaths.NewVegasBuilds.X360July2010();
        Assert.SkipUnless(esm is not null, RealAssetPaths.SkipMessage("FalloutNV.esm (2010-7-21 X360 prototype)"));

        var raw = ReadScptIndependently(esm);
        Assert.True(raw.BigEndian);
        Assert.Equal(2487, raw.Scripts.Count);

        var loaded = await RealAssetEsmCache.LoadAsync(esm, TestContext.Current.CancellationToken);
        var outDir = Path.Combine(_root, "july-2010");
        var outcome = RunExport(loaded, outDir, "all");

        var summary = Assert.IsType<ScriptExportSummary>(outcome.Summary);
        Assert.Equal(2487, summary.ScriptCount);
        Assert.Equal(2487, summary.StoredSourceFiles);
        Assert.Equal(2487, summary.DecompiledFiles);
        Assert.Equal(0, summary.SkippedScripts);

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "scripts.manifest.json")));
        var root = manifest.RootElement;
        var source = root.GetProperty("source");
        Assert.Equal("big", source.GetProperty("containerEndianness").GetString());
        Assert.Equal("FalloutNewVegas", source.GetProperty("game").GetString());
        Assert.Equal(0, source.GetProperty("plugin").GetProperty("masters").GetArrayLength());
        AssertCorpusLabel(esm, "Fallout - New Vegas (2010-7-21, X360 - Prototype)", source);

        // The source text is the same Windows-1252 bytes whatever the container's byte order.
        var census = AssertEveryScriptIsVerbatim(root, raw, outDir, ".gek");
        Assert.Equal(2544712L, census.TotalBytes);
        Assert.Equal(2476, census.LineEndings.GetValueOrDefault("crlf"));
        Assert.Equal(11, census.LineEndings.GetValueOrDefault("none"));

        var lucky38 = FindEntry(root, "0x001746C1");
        Assert.Equal(961062L, lucky38.GetProperty("recordOffset").GetInt64());
        Assert.Equal(
            "56dc7d9b6408529025a8575b8eecba18dec7f18a7c43b2b0ca123a4c0c75b310",
            Convert.ToHexStringLower(SHA256.HashData(
                File.ReadAllBytes(Path.Combine(outDir, "Lucky38MrHouseTerminalCodeSCRIPT.gek")))));

        // Every SCDA here is little-endian inside the big-endian container. Read in the container's order the
        // opening ScriptName (1D 00) becomes the unknown opcode 0x1D00, the signature of the old defect.
        Assert.Equal(2487, census.LittleEndianBytecode);
        var reconstructions = Directory.GetFiles(outDir, "*.decompiled.gek");
        Assert.Equal(2487, reconstructions.Length);
        foreach (var path in reconstructions)
        {
            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.AsSpan().StartsWith(Encoding.ASCII.GetBytes(BannerFirstLine)), path);
            Assert.True(bytes.AsSpan().IndexOf("UnknownFunc_0x1D00"u8) < 0, path);
            Assert.True(bytes.AsSpan().IndexOf("; Bytecode byte order: little-endian ("u8) >= 0, path);
        }
    }

    [Theory]
    [InlineData("DeadMansHand.esm", 323, 486171L)]
    [InlineData("DeadMoney.esm", 347, 521861L)]
    public async Task Dlc_2011_sources_export_verbatim(string plugin, int scriptCount, long sctxBytes)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esm = RealAssetPaths.NewVegasBuilds.X360Proto2011(plugin);
        Assert.SkipUnless(esm is not null, RealAssetPaths.SkipMessage($"{plugin} (2011-2-15 X360 prototype)"));

        var raw = ReadScptIndependently(esm);
        Assert.False(raw.BigEndian);
        Assert.Equal(scriptCount, raw.Scripts.Count);

        var loaded = await RealAssetEsmCache.LoadAsync(esm, TestContext.Current.CancellationToken);
        var outDir = Path.Combine(_root, Path.GetFileNameWithoutExtension(plugin));
        var outcome = RunExport(loaded, outDir, "missing");

        var summary = Assert.IsType<ScriptExportSummary>(outcome.Summary);
        Assert.Equal(scriptCount, summary.ScriptCount);
        Assert.Equal(scriptCount, summary.StoredSourceFiles);
        Assert.Equal(0, summary.DecompiledFiles);
        Assert.Equal(scriptCount + 1, Directory.GetFiles(outDir).Length);

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "scripts.manifest.json")));
        var root = manifest.RootElement;
        var source = root.GetProperty("source");
        Assert.Equal("little", source.GetProperty("containerEndianness").GetString());
        Assert.Equal("FalloutNewVegas", source.GetProperty("game").GetString());
        Assert.Equal(
            ["FalloutNV.esm"],
            source.GetProperty("plugin").GetProperty("masters").EnumerateArray().Select(master => master.GetString()));
        AssertCorpusLabel(esm, "Fallout - New Vegas (2011-2-15, X360 - Prototype)", source);

        var census = AssertEveryScriptIsVerbatim(root, raw, outDir, ".gek");
        Assert.Equal(sctxBytes, census.TotalBytes);
        Assert.Equal(scriptCount, census.LineEndings.GetValueOrDefault("crlf"));
    }

    private static ExportScriptsCommand.Outcome RunExport(UnifiedAnalysisResult loaded, string outDir, string decompiled)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var outcome = ExportScriptsCommand.ExportLoaded(
            loaded,
            new ExportScriptsCommand.Request
            {
                InputPath = loaded.FilePath,
                OutputDirectory = outDir,
                Decompiled = decompiled
            },
            CreateConsole(output),
            CreateConsole(error),
            CreatedUtc);
        Assert.True(outcome.ExitCode == 0, $"exit={outcome.ExitCode}\n{output}\n{error}");
        return outcome;
    }

    /// <summary>
    ///     Checks every manifest entry against the independently walked record with its FormID: the same record
    ///     offset, the EditorID as stem with no adjustment, exactly one authored file whose bytes on disk ARE the
    ///     SCTX subrecord (no BOM, no trailing NUL, nothing added) and whose recorded length and SHA-256 match
    ///     them. Returns totals for the caller's pins.
    /// </summary>
    private static ExportCensus AssertEveryScriptIsVerbatim(
        JsonElement root,
        RawScptGroup raw,
        string outDir,
        string extension)
    {
        var entries = root.GetProperty("scripts").EnumerateArray().ToList();
        Assert.Equal(raw.Scripts.Count, entries.Count);

        var seen = new HashSet<uint>();
        var totalBytes = 0L;
        var littleEndianBytecode = 0;
        var lineEndings = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var formIdText = entry.GetProperty("formId").GetString()!;
            var formId = Convert.ToUInt32(formIdText[2..], 16);
            Assert.True(seen.Add(formId), $"{formIdText} is listed twice");
            Assert.True(raw.Scripts.TryGetValue(formId, out var record), $"{formIdText} is not an SCPT in the file");

            Assert.Equal(record.Offset, entry.GetProperty("recordOffset").GetInt64());
            Assert.Equal(record.EditorId, entry.GetProperty("editorId").GetString());
            Assert.Equal(record.EditorId, entry.GetProperty("stem").GetString());
            Assert.Equal(0, entry.GetProperty("nameAdjustments").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("skipped").ValueKind);
            if (entry.GetProperty("bytecode").GetProperty("endianness").GetString() == "little")
            {
                littleEndianBytecode++;
            }

            var authored = entry.GetProperty("files").EnumerateArray()
                .Where(file => file.GetProperty("content").GetString() == "stored-source")
                .ToList();
            var file = Assert.Single(authored);
            Assert.Equal(record.EditorId + extension, file.GetProperty("path").GetString());
            Assert.Equal("plugin-record", file.GetProperty("provenance").GetString());
            Assert.Equal("windows-1252", file.GetProperty("encoding").GetString());

            var bytes = File.ReadAllBytes(Path.Combine(outDir, record.EditorId + extension));
            Assert.NotNull(record.Sctx);
            Assert.True(record.Sctx.AsSpan().SequenceEqual(bytes), $"{formIdText} {record.EditorId} is not verbatim");
            Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), record.EditorId);
            Assert.True(bytes.Length == 0 || bytes[^1] != 0, record.EditorId);
            Assert.Equal(bytes.Length, file.GetProperty("byteLength").GetInt32());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), file.GetProperty("sha256").GetString());

            totalBytes += bytes.Length;
            var lineEnding = file.GetProperty("lineEndings").GetString()!;
            lineEndings[lineEnding] = lineEndings.GetValueOrDefault(lineEnding) + 1;
        }

        return new ExportCensus(totalBytes, lineEndings, littleEndianBytecode);
    }

    private static JsonElement FindEntry(JsonElement root, string formId)
    {
        return Assert.Single(
            root.GetProperty("scripts").EnumerateArray(),
            entry => entry.GetProperty("formId").GetString() == formId);
    }

    /// <summary>
    ///     The build label is the name of the <c>Sample/Builds/&lt;build&gt;</c> directory the plugin sits in —
    ///     asserted only when the resolved path really runs through that directory (a fixture root override may
    ///     place it elsewhere, and then no corpus label is expected to exist).
    /// </summary>
    private static void AssertCorpusLabel(string esm, string build, JsonElement source)
    {
        var marker = Path.Combine("Sample", "Builds", build) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(esm).Contains(marker, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Assert.Equal(build, source.GetProperty("buildLabel").GetString());
        Assert.Equal("corpus-path", source.GetProperty("buildLabelSource").GetString());
    }

    private static string FileSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
            FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static IAnsiConsole CreateConsole(StringWriter writer)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No
        });
        console.Profile.Width = 400;
        return console;
    }

    /// <summary>
    ///     Reads every record of the SCPT top-level group straight from the file, independently of the tool's
    ///     parser: the TES4 record is skipped by its size, other top-level groups are seeked past, and each SCPT
    ///     record's subrecords are walked for EDID and the raw SCTX payload. Handles the little-endian PC layout
    ///     ("TES4") and the Xbox 360 big-endian one ("4SET", reversed signatures, big-endian sizes). Compressed
    ///     records and XXXX-extended subrecords fail the test instead of being guessed at; none of the plugins
    ///     measured here has either in its SCPT group.
    /// </summary>
    private static RawScptGroup ReadScptIndependently(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
            FileOptions.SequentialScan);
        var header = new byte[24];
        stream.ReadExactly(header);
        var signature = Encoding.ASCII.GetString(header, 0, 4);
        Assert.True(signature is "TES4" or "4SET", $"{path} opens with '{signature}'");
        var bigEndian = signature == "4SET";
        stream.Seek(ReadUInt32(header, 4, bigEndian), SeekOrigin.Current);

        while (stream.Position + 24 <= stream.Length)
        {
            var groupOffset = stream.Position;
            stream.ReadExactly(header);
            Assert.Equal("GRUP", ReadSignature(header, 0, bigEndian));
            var groupSize = ReadUInt32(header, 4, bigEndian);
            if (ReadSignature(header, 8, bigEndian) != "SCPT")
            {
                stream.Seek(groupSize - 24, SeekOrigin.Current);
                continue;
            }

            var body = new byte[groupSize - 24];
            stream.ReadExactly(body);
            return new RawScptGroup(bigEndian, ParseScptRecords(body, groupOffset + 24, bigEndian));
        }

        Assert.Fail($"{path} has no SCPT top-level group.");
        return null!;
    }

    private static Dictionary<uint, RawScpt> ParseScptRecords(byte[] body, long bodyOffset, bool bigEndian)
    {
        var scripts = new Dictionary<uint, RawScpt>();
        var position = 0;
        while (position < body.Length)
        {
            Assert.Equal("SCPT", ReadSignature(body, position, bigEndian));
            var dataSize = (int)ReadUInt32(body, position + 4, bigEndian);
            var flags = ReadUInt32(body, position + 8, bigEndian);
            var formId = ReadUInt32(body, position + 12, bigEndian);
            Assert.True((flags & 0x00040000) == 0, $"SCPT 0x{formId:X8} is compressed; this walker does not inflate");

            string? editorId = null;
            byte[]? sctx = null;
            var subrecord = position + 24;
            var end = subrecord + dataSize;
            while (subrecord + 6 <= end)
            {
                var subSignature = ReadSignature(body, subrecord, bigEndian);
                Assert.NotEqual("XXXX", subSignature);
                var length = bigEndian
                    ? BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(subrecord + 4))
                    : BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(subrecord + 4));
                var payload = body.AsSpan(subrecord + 6, length);
                if (subSignature == "EDID")
                {
                    var nul = payload.IndexOf((byte)0);
                    editorId = Encoding.ASCII.GetString(nul < 0 ? payload : payload[..nul]);
                }
                else if (subSignature == "SCTX")
                {
                    Assert.Null(sctx);
                    sctx = payload.ToArray();
                }

                subrecord += 6 + length;
            }

            Assert.Equal(end, subrecord);
            Assert.True(
                scripts.TryAdd(formId, new RawScpt(formId, editorId, bodyOffset + position, sctx)),
                $"SCPT 0x{formId:X8} occurs twice");
            position = end;
        }

        return scripts;
    }

    private static string ReadSignature(byte[] data, int offset, bool bigEndian)
    {
        Span<byte> signature = stackalloc byte[4];
        data.AsSpan(offset, 4).CopyTo(signature);
        if (bigEndian)
        {
            signature.Reverse();
        }

        return Encoding.ASCII.GetString(signature);
    }

    private static uint ReadUInt32(byte[] data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    }

    private sealed record RawScpt(uint FormId, string? EditorId, long Offset, byte[]? Sctx);

    private sealed record RawScptGroup(bool BigEndian, Dictionary<uint, RawScpt> Scripts);

    private sealed record ExportCensus(long TotalBytes, Dictionary<string, int> LineEndings, int LittleEndianBytecode);
}
