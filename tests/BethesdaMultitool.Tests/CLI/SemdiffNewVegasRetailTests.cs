using System.Text.Json;
using BethesdaMultitool.CLI.Commands.Esm;
using BethesdaMultitool.CLI.Formatters;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Spectre.Console;
using Xunit;

namespace BethesdaMultitool.Tests.CLI;

/// <summary>
///     The semdiff fixes against the retail New Vegas builds in <c>Sample/Builds</c>. Every expected
///     value was measured independently of this tool, by a header walk over the same files (read-only
///     Python mmap scripts in the 2026-09-28 audit): the 24-byte headers of ACHR 0x000E739E, the
///     per-bit census of header-flag changes between the 2010 retail DVD and the 2022 Steam master,
///     the Old World Blues FormID-reuse and EditorID-renumbering census, and the split-INFO duplicate
///     of 0x000E9476 in the July 2010 Xbox 360 master.
///     <para>
///         Each test parses only what it needs (a FormID or a signature filter), except the Old World
///         Blues census, whose two plugins are about 15 MB each.
///     </para>
/// </summary>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class SemdiffNewVegasRetailTests
{
    [Fact]
    public void Semdiff_Fnv10VsRetail_Achr000E739E_ReportsInitiallyDisabledAdded()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var disc = RealAssetPaths.NewVegasBuilds.SteamDisc2010();
        var retail = RealAssetPaths.NewVegasBuilds.Steam2022();
        Assert.SkipWhen(disc is null, RealAssetPaths.SkipMessage("2010 Steam Disc FalloutNV.esm"));
        Assert.SkipWhen(retail is null, RealAssetPaths.SkipMessage("2022 Steam FalloutNV.esm"));

        var result = Compare(disc, retail, null, 0x000E739E, SemdiffTypes.MatchMode.FormId, false);

        var diff = Assert.Single(result.Records);
        Assert.Equal(SemdiffTypes.DiffType.Different, diff.DiffType);
        Assert.Equal("ACHR", diff.RecordType);
        // Control: the payloads are byte-identical, so the header is the whole difference.
        Assert.Empty(diff.FieldDiffs!);
        Assert.Equal(0x00000400u, diff.RecordA!.Flags);
        Assert.Equal(0x00000C00u, diff.RecordB!.Flags);
        Assert.Equal(40u, diff.RecordA.DataSize);
        var added = Assert.Single(diff.Header!.Added);
        Assert.Equal(11, added.Bit);
        Assert.Equal("Initially Disabled", added.Name);
        Assert.Empty(diff.Header.Removed);

        // The version-control words differ too, and are bookkeeping; the form version does not.
        Assert.Equal(
            [
                new SemdiffTypes.HeaderFieldDelta("Version Control Info 1", "0x00195609", "0x0006060B",
                    SemdiffTypes.HeaderDeltaClass.Bookkeeping),
                new SemdiffTypes.HeaderFieldDelta("Version Control Info 2", "3", "4",
                    SemdiffTypes.HeaderDeltaClass.Bookkeeping)
            ],
            diff.Header.Fields);
        Assert.Equal((ushort)15, diff.RecordA.FormVersion);
    }

    [Fact]
    public void Semdiff_Fnv10VsRetail_AchrPopulation_MatchesIndependentHeaderWalk()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var disc = RealAssetPaths.NewVegasBuilds.SteamDisc2010();
        var retail = RealAssetPaths.NewVegasBuilds.Steam2022();
        Assert.SkipWhen(disc is null, RealAssetPaths.SkipMessage("2010 Steam Disc FalloutNV.esm"));
        Assert.SkipWhen(retail is null, RealAssetPaths.SkipMessage("2022 Steam FalloutNV.esm"));

        var result = Compare(disc, retail, "ACHR", null, SemdiffTypes.MatchMode.FormId, false);

        // The census of same-FormID, same-signature pairs found every ACHR flag change to be a single
        // bit: bit 11 (Initially Disabled) on 105 records and bit 25 (No AI Acquire) on one.
        var bitChanges = result.Records
            .Where(r => r.Header != null)
            .SelectMany(r => r.Header!.Added.Concat(r.Header.Removed))
            .GroupBy(delta => delta.Bit)
            .ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(105, bitChanges.GetValueOrDefault(11));
        Assert.Equal(1, bitChanges.GetValueOrDefault(25));
        Assert.Equal([11, 25], bitChanges.Keys.Order().ToList());
    }

    [Fact]
    public void Semdiff_OwbPrototypeVsRetail_ReusedFormIdsAreRefused()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var prototype = RealAssetPaths.NewVegasBuilds.X360Proto2011("OldWorldBlues.esm");
        var retail = RealAssetPaths.NewVegasBuilds.Steam2022("OldWorldBlues.esm");
        Assert.SkipWhen(prototype is null, RealAssetPaths.SkipMessage("2011 prototype OldWorldBlues.esm"));
        Assert.SkipWhen(retail is null, RealAssetPaths.SkipMessage("2022 Steam OldWorldBlues.esm"));

        var dataA = File.ReadAllBytes(prototype);
        var dataB = File.ReadAllBytes(retail);
        var bigEndianA = EsmParser.IsBigEndian(dataA);
        var bigEndianB = EsmParser.IsBigEndian(dataB);

        // The two audited FormIDs: a prototype QUST and SCPT whose numbers retail reuses for REFRs,
        // and whose EditorIDs retail does not have at all.
        foreach (var (formId, signature, editorId) in new[]
                 {
                     (0x01011E59u, "QUST", "NVDLC03X13VR"),
                     (0x01011E66u, "SCPT", "NVDLC03X13VRSCRIPT")
                 })
        {
            var single = Compare(dataA, dataB, prototype, retail, null, formId, SemdiffTypes.MatchMode.FormId,
                false);
            var diff = Assert.Single(single.Records);
            Assert.Equal(SemdiffTypes.DiffType.SignatureMismatch, diff.DiffType);
            Assert.Equal(signature, diff.RecordA!.Type);
            Assert.Equal(editorId, diff.EditorIdA);
            Assert.Equal("REFR", diff.RecordB!.Type);
            Assert.Null(diff.EditorIdB);
            Assert.True(diff.FieldDiffs is null or { Count: 0 });

            var hint = SemdiffComparer.BuildEditorIdHint(diff,
                (sig, edid) => SemdiffRecordParser.ParseRecordsWithEditorId(dataA, bigEndianA, sig, edid),
                (sig, edid) => SemdiffRecordParser.ParseRecordsWithEditorId(dataB, bigEndianB, sig, edid),
                "File A", "File B");
            Assert.Equal($"File B has no {signature} with EditorID {editorId}", hint);
        }

        // Whole files, FormID pairing: 379 shared FormIDs change signature.
        var byFormId = Compare(dataA, dataB, prototype, retail, null, null, SemdiffTypes.MatchMode.FormId, false);
        Assert.Equal(379, byFormId.Summary.SignatureMismatch);

        // Three SCPTs keep their FormID and change their EditorID only in case
        // (NVDLC03HoloFlicker*EffectScript -> ...EffectSCRIPT). The engine resolves EditorIDs
        // case-insensitively, so no pair of them may be warned about as possibly different objects.
        uint[] caseOnlyRenames = [0x01012023, 0x01012024, 0x01012028];
        Assert.DoesNotContain(byFormId.Warnings, w =>
            w.Code == "editorid-differs" && w.FormId is { } id && caseOnlyRenames.Contains(id));

        // Whole files, EditorID pairing: 11 records keep their signature and EditorID under a new
        // FormID, and no EditorID is claimed twice within either file.
        var byEditorId = Compare(dataA, dataB, prototype, retail, null, null, SemdiffTypes.MatchMode.EditorId,
            true);
        var renumbered = byEditorId.Records.Count(r =>
            r.MatchedBy == SemdiffTypes.MatchedBy.EditorId && r.RecordA != null && r.RecordB != null &&
            r.FormId != r.FormIdB);
        Assert.Equal(11, renumbered);
        Assert.Equal(0, byEditorId.Summary.Ambiguous);

        // Four of the prototype records whose FormIDs retail reuses are the Klein DIAL topics, which retail
        // carries under new FormIDs (the audit's type+EditorID census); EditorID pairing must find each.
        foreach (var (formIdA, formIdB) in new[]
                 {
                     (0x010134A8u, 0x01011314u),
                     (0x010134A9u, 0x01011315u),
                     (0x010134AAu, 0x01011316u),
                     (0x010134AFu, 0x0101137Bu)
                 })
        {
            var topic = Assert.Single(byEditorId.Records, r => r.RecordA?.FormId == formIdA);
            Assert.Equal("DIAL", topic.RecordType);
            Assert.Equal(SemdiffTypes.MatchedBy.EditorId, topic.MatchedBy);
            Assert.NotNull(topic.RecordB);
            Assert.Equal(formIdB, topic.FormIdB);
        }

        // The case-only renames pair by EditorID under their own FormID.
        foreach (var formId in caseOnlyRenames)
        {
            var script = Assert.Single(byEditorId.Records, r => r.RecordA?.FormId == formId);
            Assert.Equal("SCPT", script.RecordType);
            Assert.Equal(SemdiffTypes.MatchedBy.EditorId, script.MatchedBy);
            Assert.Equal(formId, script.FormIdB);
            Assert.NotEqual(script.EditorIdA, script.EditorIdB);
            Assert.Equal(script.EditorIdA, script.EditorIdB, ignoreCase: true);
        }
    }

    /// <summary>
    ///     The audit's own argv (<c>--formid 0x01011E59 --format json</c>) used to print the box-drawn table
    ///     with exit 0. Through the command core in JSON mode it is one parseable document whose single
    ///     record is the refused QUST-vs-REFR pair, with nothing on the console.
    /// </summary>
    [Fact]
    public void Semdiff_OwbPrototypeVsRetail_Json_0x01011E59()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var prototype = RealAssetPaths.NewVegasBuilds.X360Proto2011("OldWorldBlues.esm");
        var retail = RealAssetPaths.NewVegasBuilds.Steam2022("OldWorldBlues.esm");
        Assert.SkipWhen(prototype is null, RealAssetPaths.SkipMessage("2011 prototype OldWorldBlues.esm"));
        Assert.SkipWhen(retail is null, RealAssetPaths.SkipMessage("2022 Steam OldWorldBlues.esm"));

        var consoleOutput = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(consoleOutput),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No
        });
        using var stdout = new MemoryStream();
        using var stderr = new StringWriter();

        var exitCode = EsmSemdiffCommand.RunSemanticDiffCore(
            new EsmSemdiffCommand.SemdiffRequest(prototype, retail) { FormIdText = "0x01011E59", Format = "json" },
            console, stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Equal("", consoleOutput.ToString());
        using var document = JsonDocument.Parse(stdout.ToArray());
        var root = document.RootElement;
        Assert.Equal("bethesda-multitool/esm-semdiff", root.GetProperty("schema").GetString());
        Assert.Equal("FalloutNewVegas", root.GetProperty("files").GetProperty("a").GetProperty("game").GetString());
        Assert.Equal("OldWorldBlues.esm",
            root.GetProperty("files").GetProperty("a").GetProperty("fileName").GetString());
        // Measured: both plugins open with the bytes "TES4" (the February 2011 bundle is stored in PC byte
        // order), not the Xbox 360 "4SET".
        Assert.Equal("little", root.GetProperty("files").GetProperty("a").GetProperty("endianness").GetString());
        Assert.Equal("little", root.GetProperty("files").GetProperty("b").GetProperty("endianness").GetString());
        Assert.Equal("0x01011E59", root.GetProperty("query").GetProperty("formId").GetString());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("signatureMismatch").GetInt32());

        var record = Assert.Single(root.GetProperty("records").EnumerateArray());
        Assert.Equal("signatureMismatch", record.GetProperty("status").GetString());
        Assert.Equal("refused", record.GetProperty("comparison").GetString());
        Assert.Equal("QUST", record.GetProperty("a").GetProperty("signature").GetString());
        Assert.Equal("NVDLC03X13VR", record.GetProperty("a").GetProperty("editorId").GetString());
        Assert.Equal("REFR", record.GetProperty("b").GetProperty("signature").GetString());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("b").GetProperty("editorId").ValueKind);
        Assert.Equal(0, record.GetProperty("subrecords").GetArrayLength());
        Assert.Equal("File B has no QUST with EditorID NVDLC03X13VR",
            record.GetProperty("editorIdLookup").GetString());
    }

    [Fact]
    public void RunSemanticDiffCore_OwbMatchEditorIdWithFormId_PairsTheRenumberedKleinTopic()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var prototype = RealAssetPaths.NewVegasBuilds.X360Proto2011("OldWorldBlues.esm");
        var retail = RealAssetPaths.NewVegasBuilds.Steam2022("OldWorldBlues.esm");
        Assert.SkipWhen(prototype is null, RealAssetPaths.SkipMessage("2011 prototype OldWorldBlues.esm"));
        Assert.SkipWhen(retail is null, RealAssetPaths.SkipMessage("2022 Steam OldWorldBlues.esm"));

        // The command's own path for --match editorid -f: the bounded neighbourhood parse of the
        // prototype and retail plugins (both stored little-endian: the prototype opens with "TES4"),
        // then the table renderer.
        var (exitCode, output, stderr) = RunCommand(new EsmSemdiffCommand.SemdiffRequest(prototype, retail)
        {
            Match = "editorid",
            FormIdText = "0x010134AA",
            ShowAll = true,
            Limit = 50
        });

        Assert.Equal(0, exitCode);
        Assert.Equal("", stderr);
        Assert.Contains("DIAL 0x010134AA -> 0x01011316", output);
        Assert.Contains("Matched by EditorID", output);
    }

    [Fact]
    public void RunSemanticDiffCore_July2010X360VsRetail_DuplicateInfo_RendersAndWarns()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var july = RealAssetPaths.NewVegasBuilds.X360July2010();
        var retail = RealAssetPaths.NewVegasBuilds.Steam2022();
        Assert.SkipWhen(july is null, RealAssetPaths.SkipMessage("July 2010 X360 FalloutNV.esm"));
        Assert.SkipWhen(retail is null, RealAssetPaths.SkipMessage("2022 Steam FalloutNV.esm"));

        var (exitCode, output, stderr) = RunCommand(new EsmSemdiffCommand.SemdiffRequest(july, retail)
        {
            FormIdText = "0x000E9476",
            ShowAll = true
        });

        Assert.Equal(0, exitCode);
        Assert.Equal("", stderr);
        Assert.Contains("FormID 0x000E9476 occurs 2 times in File A", output);
        Assert.Contains("Record only exists in File A (occurrence 2 of its FormID)", output);
    }

    [Fact]
    public void Semdiff_July2010X360VsRetail_DuplicateInfo_DoesNotCrash()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var july = RealAssetPaths.NewVegasBuilds.X360July2010();
        var retail = RealAssetPaths.NewVegasBuilds.Steam2022();
        Assert.SkipWhen(july is null, RealAssetPaths.SkipMessage("July 2010 X360 FalloutNV.esm"));
        Assert.SkipWhen(retail is null, RealAssetPaths.SkipMessage("2022 Steam FalloutNV.esm"));

        var result = Compare(july, retail, null, 0x000E9476, SemdiffTypes.MatchMode.FormId, true);

        // The Xbox 360 master carries INFO 0x000E9476 twice; its second copy sits at 0x01B262B4.
        var warning = Assert.Single(result.Warnings, w => w.Code == "duplicate-formid");
        Assert.Equal(SemdiffTypes.SemdiffSide.A, warning.Side);
        Assert.Equal(0x000E9476u, warning.FormId);
        Assert.Collection(result.Records,
            paired =>
            {
                Assert.Equal("INFO", paired.RecordType);
                Assert.Equal(0, paired.OccurrenceA);
                Assert.NotNull(paired.RecordA);
                Assert.NotNull(paired.RecordB);
            },
            extra =>
            {
                Assert.Equal(SemdiffTypes.DiffType.OnlyInA, extra.DiffType);
                Assert.Equal(1, extra.OccurrenceA);
                Assert.Equal(0x01B262B4, extra.RecordA!.Offset);
            });
    }

    /// <summary>
    ///     Runs the command end to end on a StringWriter-backed console, wide enough that no warning or
    ///     title line wraps.
    /// </summary>
    private static (int ExitCode, string Output, string Stderr) RunCommand(EsmSemdiffCommand.SemdiffRequest request)
    {
        var output = new StringWriter();
        using var stderr = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(output),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No
        });
        console.Profile.Width = 400;

        var exitCode = EsmSemdiffCommand.RunSemanticDiffCore(request, console, Stream.Null, stderr);
        return (exitCode, output.ToString(), stderr.ToString());
    }

    private static SemdiffTypes.SemdiffResult Compare(string fileA, string fileB, string? type, uint? formId,
        SemdiffTypes.MatchMode match, bool showAll)
    {
        return Compare(File.ReadAllBytes(fileA), File.ReadAllBytes(fileB), fileA, fileB, type, formId, match,
            showAll);
    }

    /// <summary>The command's pipeline without the console: detect, parse with the filter, compare.</summary>
    private static SemdiffTypes.SemdiffResult Compare(byte[] dataA, byte[] dataB, string fileA, string fileB,
        string? type, uint? formId, SemdiffTypes.MatchMode match, bool showAll)
    {
        var bigEndianA = EsmParser.IsBigEndian(dataA);
        var bigEndianB = EsmParser.IsBigEndian(dataB);
        var recordsA = SemdiffRecordParser.ParseRecordsWithSubrecords(dataA, bigEndianA, type, formId,
            out var skippedA);
        var recordsB = SemdiffRecordParser.ParseRecordsWithSubrecords(dataB, bigEndianB, type, formId,
            out var skippedB);

        // The independent census decoded every compressed record; so must this parse, or the counts
        // compare different populations.
        Assert.Equal(0, skippedA);
        Assert.Equal(0, skippedB);

        return SemdiffComparer.Compare(recordsA, recordsB, new SemdiffTypes.SemdiffCompareOptions
        {
            Match = match,
            ShowAll = showAll,
            GameA = GameDetector.DetectFromBytes(dataA, Path.GetFileName(fileA)).Game,
            GameB = GameDetector.DetectFromBytes(dataB, Path.GetFileName(fileB)).Game,
            BigEndianA = bigEndianA,
            BigEndianB = bigEndianB
        });
    }
}
