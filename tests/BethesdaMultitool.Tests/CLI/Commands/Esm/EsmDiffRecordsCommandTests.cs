using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.CLI.Commands.Esm;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Models;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Spectre.Console;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Commands.Esm;

/// <summary>
///     xUnit collection for tests that swap the process-wide <see cref="AnsiConsole.Console" /> to record
///     what a command prints. The swap is restored in a <c>finally</c>, and this collection runs on its
///     own, so no concurrently running test writes into (or loses output to) the recording console.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalAnsiConsoleGroup
{
    public const string Name = "GlobalAnsiConsole";

    private GlobalAnsiConsoleGroup()
    {
    }
}

/// <summary>
///     Tests for the byte-level <c>esm diff</c> (two-way <see cref="EsmDiffRecordsCommand" />, three-way
///     <see cref="EsmDiffThreeWayCommand" />): repeated FormIDs within one file (the Xbox 360 split INFO
///     records), a FormID reused by a different record type (which must not be decoded with either
///     record's schema), the named header-flag cell, and the three-way <c>-f</c> syntax (hex, 0x optional,
///     as in two-way mode). Every plugin here is synthetic.
/// </summary>
[Collection(GlobalAnsiConsoleGroup.Name)]
public sealed class EsmDiffRecordsCommandTests
{
    // Wide enough that no warning line or table wraps, so substrings survive intact.
    private const int ConsoleWidth = 1000;

    // An INFO FormID that the Xbox 360 masters split into several same-FormID records.
    private const uint SplitInfoFormId = 0x000E9476;

    // The Old World Blues FormID a 2011 prototype gives a QUST and retail reassigns to a REFR.
    private const uint ReusedFormId = 0x01011E59;

    // ===== Repeated FormIDs =====

    [Fact]
    public void IndexFirstOccurrenceByFormId_RepeatedFormId_KeepsLowestOffsetAndWarnsOnce()
    {
        // Handed over out of file order: the lowest offset must win, not the first in the list.
        AnalyzerRecordInfo[] records = [Info(SplitInfoFormId, 500), Info(0x000E9477, 300), Info(SplitInfoFormId, 100)];
        Dictionary<uint, AnalyzerRecordInfo>? index = null;

        var output = CaptureOutput(() => index = EsmDiffRecordsCommand.IndexFirstOccurrenceByFormId(records, "PC [ref]"));

        Assert.NotNull(index);
        Assert.Equal(2, index.Count);
        Assert.Equal(100u, index[SplitInfoFormId].Offset);
        Assert.Equal(300u, index[0x000E9477].Offset);
        Assert.Equal(1, CountOf(output, "WARNING:"));
        Assert.Contains("1 FormID(s) occur more than once in PC [ref] file", output);
        Assert.Contains("0x000E9476 x2", output);
        Assert.Contains("Only the first occurrence of each", output);
    }

    [Fact]
    public void IndexFirstOccurrenceByFormId_UniqueFormIds_DoesNotWarn()
    {
        AnalyzerRecordInfo[] records = [Info(SplitInfoFormId, 100), Info(0x000E9477, 300)];

        var output = CaptureOutput(() => EsmDiffRecordsCommand.IndexFirstOccurrenceByFormId(records, "PC"));

        Assert.DoesNotContain("WARNING", output);
    }

    [Fact]
    public void NumberRepeatedFormIds_NumbersEachOccurrenceByOffset_AndSkipsUniqueFormIds()
    {
        AnalyzerRecordInfo[] records =
            [Info(SplitInfoFormId, 900), Info(0x000E9477, 300), Info(SplitInfoFormId, 100), Info(SplitInfoFormId, 500)];
        Dictionary<uint, EsmDiffRecordsCommand.FormIdOccurrence>? numbered = null;

        var output = CaptureOutput(() => numbered = EsmDiffRecordsCommand.NumberRepeatedFormIds(records, "Xbox 360"));

        Assert.NotNull(numbered);
        Assert.Equal(3, numbered.Count);
        Assert.Equal(new EsmDiffRecordsCommand.FormIdOccurrence(1, 3), numbered[100]);
        Assert.Equal(new EsmDiffRecordsCommand.FormIdOccurrence(2, 3), numbered[500]);
        Assert.Equal(new EsmDiffRecordsCommand.FormIdOccurrence(3, 3), numbered[900]);
        Assert.DoesNotContain(300u, numbered.Keys);
        Assert.Equal("Xbox 360 occurrence 2 of 3", numbered[500].Describe("Xbox 360"));
        Assert.Equal(1, CountOf(output, "WARNING:"));
        Assert.Contains("1 FormID(s) occur more than once in Xbox 360 file (e.g. 0x000E9476 x3", output);
    }

    [Fact]
    public void DiffRecordType_RepeatedFormIdInFileA_WarnsAndTitlesEachOccurrence()
    {
        // File A holds a split INFO (two records, one FormID); file B holds the merged one.
        var xbox = Plugin("INFO",
            EsmTestFileBuilder.BuildRecord("INFO", SplitInfoFormId, 0, ("NAM1", Text("Hello"))),
            EsmTestFileBuilder.BuildRecord("INFO", SplitInfoFormId, 0, ("CTDA", new byte[28])));
        var retail = Plugin("INFO", EsmTestFileBuilder.BuildRecord("INFO", SplitInfoFormId, 0, ("NAM1", Text("Hello"))));

        var (exitCode, output) = Capture(() => EsmDiffRecordsCommand.DiffRecordType(xbox, retail, false, false,
            "INFO", 10, 64, true, false, false, "Xbox 360", "PC", "FalloutNV.esm", "FalloutNV.esm"));

        Assert.Equal(0, exitCode);
        Assert.Equal(1, CountOf(output, "WARNING:"));
        Assert.Contains("1 FormID(s) occur more than once in Xbox 360 file (e.g. 0x000E9476 x2", output);
        Assert.Contains("INFO FormID: 0x000E9476 (Xbox 360 occurrence 1 of 2)", output);
        Assert.Contains("INFO FormID: 0x000E9476 (Xbox 360 occurrence 2 of 2)", output);
    }

    [Fact]
    public void RunThreeWayDiff_RecordType_RepeatedXboxFormId_WarnsAndTitlesEachOccurrence()
    {
        using var directory = new TemporaryDirectory();
        var xbox = directory.WritePlugin("xbox", Plugin("INFO",
            EsmTestFileBuilder.BuildRecord("INFO", SplitInfoFormId, 0, ("NAM1", Text("Hello"))),
            EsmTestFileBuilder.BuildRecord("INFO", SplitInfoFormId, 0, ("CTDA", new byte[28]))));
        var merged = Plugin("INFO", EsmTestFileBuilder.BuildRecord("INFO", SplitInfoFormId, 0, ("NAM1", Text("Hello"))));
        var converted = directory.WritePlugin("converted", merged);
        var pc = directory.WritePlugin("pc", merged);

        var (exitCode, output) = Capture(() =>
            EsmDiffThreeWayCommand.RunThreeWayDiff(xbox, converted, pc, null, "INFO", 10, 64, true, false));

        Assert.Equal(0, exitCode);
        Assert.Contains("1 FormID(s) occur more than once in Xbox 360 file (e.g. 0x000E9476 x2", output);
        Assert.Contains("INFO FormID: 0x000E9476 (Xbox 360 occurrence 1 of 2)", output);
        Assert.Contains("INFO FormID: 0x000E9476 (Xbox 360 occurrence 2 of 2)", output);
    }

    // ===== Header flag cell =====

    [Fact]
    public void FormatHeaderFlagsCell_NamesSetBitsForTheGameAndSignature()
    {
        // ACHR 0x000E739E's flags in the 2022 FalloutNV.esm: bits 10 and 11, which xEdit's FNV ACHR list
        // names Persistent and Initially Disabled.
        Assert.Equal("0x00000C00 (Persistent, Initially Disabled)",
            EsmDiffRecordsCommand.FormatHeaderFlagsCell(BethesdaGame.FalloutNewVegas, "ACHR", 0x00000C00));

        // QUST has no bit-11 meaning, so the same bit is named by number and mask.
        Assert.Equal("0x00000800 (bit 11 (0x00000800))",
            EsmDiffRecordsCommand.FormatHeaderFlagsCell(BethesdaGame.FalloutNewVegas, "QUST", 0x00000800));

        Assert.Equal("0x00000000",
            EsmDiffRecordsCommand.FormatHeaderFlagsCell(BethesdaGame.FalloutNewVegas, "REFR", 0));
    }

    // ===== FormID reused by a different record type =====

    [Fact]
    public void SharedSchemaRecordType_IsNullWhenAnySignatureDiffers()
    {
        Assert.Equal("QUST", EsmDiffRecordsCommand.SharedSchemaRecordType("QUST", "QUST"));
        Assert.Null(EsmDiffRecordsCommand.SharedSchemaRecordType("QUST", "REFR"));
        Assert.Null(EsmDiffRecordsCommand.SharedSchemaRecordType("QUST", "qust"));

        Assert.Equal("INFO", EsmDiffThreeWayCommand.SharedSchemaRecordType("INFO", "INFO", "INFO"));
        Assert.Null(EsmDiffThreeWayCommand.SharedSchemaRecordType("QUST", "QUST", "REFR"));
        Assert.Null(EsmDiffThreeWayCommand.SharedSchemaRecordType("REFR", "QUST", "QUST"));
        Assert.Null(EsmDiffThreeWayCommand.SharedSchemaRecordType("QUST", "REFR", "QUST"));
    }

    [Fact]
    public void DiffSpecificRecord_FormIdReusedByAnotherType_PrintsBothSignaturesAndNoSchemaHint()
    {
        var prototype = Plugin("QUST", Quest(ReusedFormId, 5.0f));
        var retail = Plugin("REFR", Reference(ReusedFormId, -1309.1f));

        var (exitCode, output) = Capture(() => EsmDiffRecordsCommand.DiffSpecificRecord(prototype, retail, false,
            false, ReusedFormId, 64, true, false, false, "Prototype", "Retail", "FalloutNV.esm", "FalloutNV.esm"));

        Assert.Equal(0, exitCode);
        Assert.Contains("is QUST in Prototype but REFR in Retail", output);
        Assert.Contains("reused by a different record type", output);
        Assert.Contains("Signature", output);

        // The two DATA payloads did reach the byte comparison; only the schema reading is refused.
        Assert.Contains("CONTENT DIFFERS", output);
        Assert.DoesNotContain("schema:", output);
        Assert.DoesNotContain("QuestDelay", output);
    }

    [Fact]
    public void DiffSpecificRecord_SameSignature_KeepsTheSchemaHint()
    {
        // The control for the test above: the same DATA bytes under one signature do get the hint, so
        // its absence there is the mismatch guard and not a quirk of the fixture.
        var a = Plugin("QUST", Quest(ReusedFormId, 5.0f));
        var b = Plugin("QUST", Quest(ReusedFormId, 7.5f));

        var (exitCode, output) = Capture(() => EsmDiffRecordsCommand.DiffSpecificRecord(a, b, false, false,
            ReusedFormId, 64, true, false, false, "A", "B", "FalloutNV.esm", "FalloutNV.esm"));

        Assert.Equal(0, exitCode);
        Assert.Contains("QUST FormID: 0x01011E59", output);
        Assert.Contains("schema: QuestDelay : Float", output);
        Assert.DoesNotContain("reused", output);
    }

    [Fact]
    public void RunThreeWayDiff_FormIdReusedByAnotherType_RefusesSchemaHintsAndSemanticFields()
    {
        // The prototype's QUST converts to a QUST; retail reassigned the FormID to a REFR.
        using var directory = new TemporaryDirectory();
        var xbox = directory.WritePlugin("xbox", Plugin("QUST", Quest(ReusedFormId, 5.0f)));
        var converted = directory.WritePlugin("converted", Plugin("QUST", Quest(ReusedFormId, 5.0f)));
        var pc = directory.WritePlugin("pc", Plugin("REFR", Reference(ReusedFormId, -1309.1f)));

        var (exitCode, output) = Capture(() =>
            EsmDiffThreeWayCommand.RunThreeWayDiff(xbox, converted, pc, "0x01011E59", null, 5, 64, true, true));

        Assert.Equal(0, exitCode);
        Assert.Contains("Xbox 360: QUST", output);
        Assert.Contains("Converted: QUST", output);
        Assert.Contains("PC Ref: REFR", output);
        Assert.Contains("reused by a different record type", output);
        Assert.Contains("Signature", output);
        Assert.Contains("CONTENT DIFFERS", output);
        Assert.DoesNotContain("schema:", output);
        Assert.DoesNotContain("QuestDelay", output);
        Assert.DoesNotContain("Semantic field comparison", output);
    }

    [Fact]
    public void RunThreeWayDiff_SameSignature_KeepsSchemaHintsAndSemanticFields()
    {
        // The control for the test above.
        using var directory = new TemporaryDirectory();
        var xbox = directory.WritePlugin("xbox", Plugin("QUST", Quest(ReusedFormId, 5.0f)));
        var converted = directory.WritePlugin("converted", Plugin("QUST", Quest(ReusedFormId, 5.0f)));
        var pc = directory.WritePlugin("pc", Plugin("QUST", Quest(ReusedFormId, 7.5f)));

        var (exitCode, output) = Capture(() =>
            EsmDiffThreeWayCommand.RunThreeWayDiff(xbox, converted, pc, "0x01011E59", null, 5, 64, true, true));

        Assert.Equal(0, exitCode);
        Assert.Contains("QUST FormID: 0x01011E59", output);
        Assert.Contains("schema: QuestDelay : Float", output);
        Assert.Contains("Semantic field comparison", output);
        Assert.DoesNotContain("reused", output);
    }

    // ===== Three-way -f syntax =====

    [Theory]
    [InlineData("00123456", "0x00123456")]
    [InlineData("0x00123456", "0x00123456")]
    [InlineData("000E739E", "0x000E739E")]
    public void RunThreeWayDiff_FormId_IsHexWithOrWithoutPrefix(string formIdText, string expected)
    {
        // 0x0001E240 is 123456 decimal: the record a decimal reading of "00123456" would silently diff.
        using var directory = new TemporaryDirectory();
        var plugin = Plugin("QUST",
            Quest(0x00123456, 1.0f),
            Quest(0x0001E240, 2.0f),
            Quest(0x000E739E, 3.0f));
        var xbox = directory.WritePlugin("xbox", plugin);
        var converted = directory.WritePlugin("converted", plugin);
        var pc = directory.WritePlugin("pc", plugin);

        var (exitCode, output) = Capture(() =>
            EsmDiffThreeWayCommand.RunThreeWayDiff(xbox, converted, pc, formIdText, null, 5, 64, true, false));

        Assert.Equal(0, exitCode);
        Assert.Contains($"QUST FormID: {expected}", output);
        Assert.DoesNotContain("0x0001E240", output);
    }

    [Fact]
    public void RunThreeWayDiff_MalformedFormId_IsACleanErrorBeforeAnyFileIsRead()
    {
        // None of the three paths exists: the FormID is rejected first.
        var (exitCode, output) = Capture(() => EsmDiffThreeWayCommand.RunThreeWayDiff(
            "missing-xbox.esm", "missing-converted.esm", "missing-pc.esm", "0xZZ", null, 5, 64, true, false));

        Assert.Equal(1, exitCode);
        Assert.Contains("Invalid FormID: 0xZZ", output);
        Assert.DoesNotContain("not found", output);
    }

    // ===== Helpers =====

    private static AnalyzerRecordInfo Info(uint formId, uint offset)
    {
        return new AnalyzerRecordInfo
        {
            Signature = "INFO",
            FormId = formId,
            Flags = 0,
            DataSize = 0,
            Offset = offset,
            TotalSize = 24
        };
    }

    /// <summary>A little-endian FNV plugin (TES4 with HEDR 1.34) holding one top-level GRUP.</summary>
    private static byte[] Plugin(string grupLabel, params byte[][] records)
    {
        return new EsmTestFileBuilder().AddTopLevelGrup(grupLabel, records).Build();
    }

    /// <summary>A QUST with an EDID and the 8-byte DATA (flags, priority, 2 padding, QuestDelay float).</summary>
    private static byte[] Quest(uint formId, float questDelay)
    {
        return EsmTestFileBuilder.BuildRecord("QUST", formId, 0,
            ("EDID", Text("NVDLC03X13VR")), ("DATA", EightByteData(questDelay)));
    }

    /// <summary>
    ///     A REFR with NAME and an 8-byte DATA. A real REFR DATA is 24 bytes (position and rotation);
    ///     eight here puts it against the QUST DATA as a same-size pair, the case where a QUST schema read
    ///     of the REFR's bytes would print QUST field names.
    /// </summary>
    private static byte[] Reference(uint formId, float y)
    {
        return EsmTestFileBuilder.BuildRecord("REFR", formId, 0,
            ("NAME", U32(0x00120F40)), ("DATA", EightByteData(y)));
    }

    private static byte[] EightByteData(float value)
    {
        var data = new byte[8];
        data[0] = 0x01;
        data[1] = 0x3F;
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), value);
        return data;
    }

    private static byte[] Text(string value)
    {
        return Encoding.ASCII.GetBytes(value + "\0");
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var index = text.IndexOf(needle, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string CaptureOutput(Action run)
    {
        return Capture(() =>
        {
            run();
            return 0;
        }).Output;
    }

    /// <summary>
    ///     Runs <paramref name="run" /> with <see cref="AnsiConsole.Console" /> swapped for a plain-text
    ///     recording console, and restores the previous console afterwards.
    /// </summary>
    private static (int ExitCode, string Output) Capture(Func<int> run)
    {
        using var writer = new StringWriter();
        var recording = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No
        });
        recording.Profile.Width = ConsoleWidth;

        var previous = AnsiConsole.Console;
        AnsiConsole.Console = recording;
        try
        {
            var exitCode = run();
            return (exitCode, writer.ToString());
        }
        finally
        {
            AnsiConsole.Console = previous;
        }
    }

    /// <summary>A temporary directory holding one <c>FalloutNV.esm</c> per build subdirectory.</summary>
    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path =
            Path.Combine(Path.GetTempPath(), "esm-diff-" + Guid.NewGuid().ToString("N"));

        /// <summary>
        ///     Writes <paramref name="bytes" /> as <c>&lt;build&gt;/FalloutNV.esm</c> (the master name is what
        ///     game detection reads) and returns its full path.
        /// </summary>
        public string WritePlugin(string build, byte[] bytes)
        {
            var directory = Path.Combine(_path, build);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "FalloutNV.esm");
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, true);
            }
        }
    }
}
