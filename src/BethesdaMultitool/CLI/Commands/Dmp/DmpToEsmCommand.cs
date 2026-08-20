using System.Collections.Immutable;
using System.CommandLine;
using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using BethesdaMultitool.Core.Vfs;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Dmp;

/// <summary>
///     CLI command for running the DMP→ESM plugin conversion pipeline. Same engine as the
///     WinUI tab — reads an Xbox 360 memory dump, merges its records into a PC plugin
///     authored against the provided master ESM.
/// </summary>
public static class DmpToEsmCommand
{
    public static Command Create()
    {
        var dmpArg = new Argument<string>("dmp") { Description = "Path to Xbox 360 memory dump (.dmp)" };
        var pcEsmOpt = new Option<string>("--pc-esm")
        {
            Description = "Path to PC FalloutNV.esm (master ESM)",
            Required = true
        };
        var outputOpt = new Option<string>("-o", "--output")
        {
            Description = "Output ESM path",
            Required = true
        };
        var authorOpt = new Option<string?>("--author") { Description = "Plugin author metadata" };
        var descriptionOpt = new Option<string?>("--description") { Description = "Plugin description" };
        var compressOpt = new Option<bool>("--compress") { Description = "Compress record bodies (zlib)" };
        var validateOpt = new Option<bool>("--validate")
        {
            Description = "Re-parse the output ESM to validate structure",
            DefaultValueFactory = _ => true
        };
        var verboseOpt = new Option<bool>("--verbose")
        {
            Description = "Emit per-record decision events (very chatty)"
        };
        var eventLogJsonlOpt = new Option<string?>("--event-log-jsonl")
        {
            Description = "Write every structured conversion event plus final stats as JSON Lines. " +
                          "Intended for reproducible corpus certification."
        };
        var secondaryDataOpt = new Option<string[]>("--secondary-data")
        {
            Description = "Repeatable: secondary data folder to pull missing assets from, in priority order " +
                          "(first wins). Xbox 360 folders are auto-detected when possible. Use with --pack-assets."
        };
        var secondaryData360Opt = new Option<string[]>("--secondary-data-360")
        {
            Description = "Repeatable compatibility/override form: secondary data folder containing Xbox 360 " +
                          "assets. Ordinary --secondary-data also auto-detects 360 folders; use this only " +
                          "when a folder has loose 360 assets and no detectable 360 ESM/BSA header."
        };
        var packAssetsOpt = new Option<string?>("--pack-assets")
        {
            Description = "Output BSA path. When set, after the ESM is written the converter scans " +
                          "the ESM + DMP for referenced assets, resolves them against --secondary-data / " +
                          "--secondary-data-360 folders, and packs the missing ones into this BSA."
        };
        var writeMissingListOpt = new Option<bool>("--write-missing-list")
        {
            Description = "Write a human-reviewable per-asset audit file at <pack-assets>.missing.txt " +
                          "alongside the packed BSA. Sections: missing, fuzzy-matched, conversion-failed."
        };
        var dialogueAudioCsvOpt = new Option<string[]>("--dialogue-audio-csv")
        {
            Description = "Repeatable: Bethesda Audio Transcriber CSV export used to add dialogue voice " +
                          "audio/lip requests for INFO records present in the ESM/DMP. Use with " +
                          "--pack-assets and a --secondary-data folder containing the audio."
        };
        var overrideVanillaOpt = new Option<bool>("--override-vanilla")
        {
            Description = "When resolving assets, consult --secondary-data folders BEFORE the PC Data " +
                          "baseline. Assets present in a secondary override the vanilla copy at runtime. " +
                          "Applied to both the asset-rename rewriter and the BSA packer."
        };
        var disableRefrEditorIdRemapOpt = new Option<bool>("--disable-refr-editorid-remap")
        {
            Description = "Disable the EditorID-stem rename fallback that rescues otherwise-dropped " +
                          "REFRs whose prototype base FormID matches a same-type master record by stem " +
                          "(e.g. SCOLParkingLotChunk03 → master SCOLParkingLotChunk03b). On by default."
        };
        var replaceCellTemporariesOpt = new Option<bool>("--replace-cell-temporaries")
        {
            Description = "Diagnostic mode: when a cell already exists in master AND the DMP captured " +
                          "placements for it, delete every master temporary ref not in the DMP. Master " +
                          "persistent refs (and therefore quest-bound objects) are preserved. Off by default."
        };
        var recoverGapsOpt = new Option<bool>("--recover-gaps")
        {
            Description = "Experimental opt-in: scan uncovered DMP gaps for validated raw ESM records and " +
                          "runtime TESForm structs, then promote only candidates existing semantic readers " +
                          "can consume safely. Off by default."
        };
        var emitMasterCellNavmAugmentationOpt = new Option<bool>("--emit-master-cell-navm-augmentation")
        {
            Description = "Opt in to emitting DMP-captured proto NAVMs into overridden master cells. " +
                          "OFF by default: our reconstructed NAVMs carry empty NVCI door-links and " +
                          "shadow master's complete navmeshes, which null-derefs the cross-cell " +
                          "TeleportDoorSearch graph on a cold coc (Gomorrah01). Master's own NAVMs " +
                          "load via the cell file-list merge and are strictly better. New (non-master) " +
                          "cells emit their proto NAVMs regardless. Enable only for NVCI-reconstruction work."
        };

        var noCellInferenceOpt = new Option<bool>("--no-cell-inference")
        {
            Description = "Disable placement inference for refs whose parent CELL was never captured. " +
                          "By default such refs are placed by their own coordinates (exact-grid match " +
                          "against a uniquely-identifying captured cell, else unique containment in one " +
                          "worldspace's captured grid bounds) and provenance-marked as inferred. Pass " +
                          "this to keep the conservative skip instead."
        };

        var noRecoverLeveledSpawnsOpt = new Option<bool>("--no-recover-leveled-spawns")
        {
            Description = "Disable leveled-spawn recovery. By default, placed actors (ACHR/ACRE) whose " +
                          "base is a runtime clone (0xFF) — the generic leveled-spawn crowd — are " +
                          "recovered by re-pointing the base to the actor's captured ExtraLeveledCreature " +
                          "template (a type-compatible NPC_/CREA, master or captured-proto). The re-pointed " +
                          "actor is a fixed instance (no re-leveling). Pass this for a bare-master baseline. " +
                          "Run `dmp leveled-spawn-census` to gauge recovery yield."
        };

        var command = new Command("to-esm", "Convert a DMP to a PC plugin ESM overlay against a master ESM");
        command.Aliases.Add("to-esp"); // back-compat: was 'to-esp' before the output became ESM-flagged
        command.Arguments.Add(dmpArg);
        command.Options.Add(pcEsmOpt);
        command.Options.Add(outputOpt);
        command.Options.Add(authorOpt);
        command.Options.Add(descriptionOpt);
        command.Options.Add(compressOpt);
        command.Options.Add(validateOpt);
        command.Options.Add(verboseOpt);
        command.Options.Add(eventLogJsonlOpt);
        command.Options.Add(secondaryDataOpt);
        command.Options.Add(secondaryData360Opt);
        command.Options.Add(packAssetsOpt);
        command.Options.Add(writeMissingListOpt);
        command.Options.Add(dialogueAudioCsvOpt);
        command.Options.Add(overrideVanillaOpt);
        command.Options.Add(disableRefrEditorIdRemapOpt);
        command.Options.Add(replaceCellTemporariesOpt);
        command.Options.Add(recoverGapsOpt);
        command.Options.Add(emitMasterCellNavmAugmentationOpt);
        command.Options.Add(noRecoverLeveledSpawnsOpt);
        command.Options.Add(noCellInferenceOpt);

        var cellAuthorityOpt = new Option<string?>("--cell-authority")
        {
            Description =
                "Optional corpus-derived CELL metadata authority JSON (built with " +
                "`dmp build-cell-authority`). Applied to parsed cells before grouping so cells " +
                "land under the correct WRLD in the output ESM. Defaults to " +
                "data/cell_worldspace_authority.json next to the executable if it exists."
        };
        command.Options.Add(cellAuthorityOpt);

        var skipWorldspaceOpt = new Option<string[]>("--skip-worldspace")
        {
            Description =
                "Diagnostic: repeatable WRLD FormID (hex, e.g. 0x000DA726) whose cells and nested " +
                "placements the converter should drop from emission. Used to bisect crashes that " +
                "point at a specific worldspace — master content remains in effect via per-FormID " +
                "merge."
        };
        command.Options.Add(skipWorldspaceOpt);

        var skipRecordTypeOpt = new Option<string[]>("--skip-record-type")
        {
            Description =
                "Diagnostic: repeatable record-type signature (e.g. STAT, NPC_, WEAP) whose " +
                "top-level emission the converter should drop. Master records remain in effect " +
                "via per-FormID merge; new records of this type aren't emitted at all. Used to " +
                "bisect crashes that point at a specific record type."
        };
        command.Options.Add(skipRecordTypeOpt);

        var diagnosticKeepMasterFormIdOpt = new Option<string[]>("--diag-keep-master-formid")
        {
            Description =
                "Diagnostic: repeatable exact DMP-override FormID to leave master-pure. " +
                "Requires the record type to be planner-enabled. Invalid or non-override IDs fail the build."
        };
        command.Options.Add(diagnosticKeepMasterFormIdOpt);

        var diagnosticRetainMasterSubrecordsOpt =
            new Option<string[]>("--diag-retain-master-subrecords")
            {
                Description =
                    "Diagnostic: repeatable <FormID>:<SIG>[,<SIG>...] directive (for example " +
                    "0x0012795D:AIDT,SNAM) retaining those exact master subrecords on one override."
            };
        command.Options.Add(diagnosticRetainMasterSubrecordsOpt);

        // --planner-types was removed 2026-08-11 with the legacy emission path. The planner
        // owns every record type unconditionally; a partial set would mean "emit nothing" for
        // the types left out, so there is no coherent subset to select any more.

        var diagSkipCellNavmOpt = new Option<bool>("--diag-skip-cell-navm")
        {
            Description =
                "Diagnostic: suppress NAVM record emission inside cell bundles while keeping " +
                "the TES4 ESM flag and everything else unchanged. Bisects crash classes where " +
                "the proto navmesh records are the suspect."
        };
        command.Options.Add(diagSkipCellNavmOpt);

        var diagSkipCellNewRefsOpt = new Option<bool>("--diag-skip-cell-new-refs")
        {
            Description =
                "Diagnostic: suppress NEW placed-ref emission inside cell bundles (master " +
                "overrides/carries still emit). Second knob of the interior attach-AV bisect."
        };
        command.Options.Add(diagSkipCellNewRefsOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var dmp = parseResult.GetValue(dmpArg)!;
            var pcEsm = parseResult.GetValue(pcEsmOpt)!;
            var output = parseResult.GetValue(outputOpt)!;
            var author = parseResult.GetValue(authorOpt);
            var description = parseResult.GetValue(descriptionOpt);
            var compress = parseResult.GetValue(compressOpt);
            var validate = parseResult.GetValue(validateOpt);
            var verbose = parseResult.GetValue(verboseOpt);
            var eventLogJsonl = parseResult.GetValue(eventLogJsonlOpt);
            var secondaryData = parseResult.GetValue(secondaryDataOpt) ?? [];
            var secondaryData360 = parseResult.GetValue(secondaryData360Opt) ?? [];
            var packAssets = parseResult.GetValue(packAssetsOpt);
            var writeMissingList = parseResult.GetValue(writeMissingListOpt);
            var dialogueAudioCsv = parseResult.GetValue(dialogueAudioCsvOpt) ?? [];
            var overrideVanilla = parseResult.GetValue(overrideVanillaOpt);
            var disableRefrEditorIdRemap = parseResult.GetValue(disableRefrEditorIdRemapOpt);
            var replaceCellTemporaries = parseResult.GetValue(replaceCellTemporariesOpt);
            var recoverGaps = parseResult.GetValue(recoverGapsOpt);
            var emitMasterCellNavmAugmentation = parseResult.GetValue(emitMasterCellNavmAugmentationOpt);
            var recoverLeveledSpawns = !parseResult.GetValue(noRecoverLeveledSpawnsOpt);
            var inferUnresolvedCells = !parseResult.GetValue(noCellInferenceOpt);
            var diagSkipCellNavm = parseResult.GetValue(diagSkipCellNavmOpt);
            var diagSkipCellNewRefs = parseResult.GetValue(diagSkipCellNewRefsOpt);
            var cellAuthorityPath = parseResult.GetValue(cellAuthorityOpt);
            var skipWorldspaceArgs = parseResult.GetValue(skipWorldspaceOpt) ?? [];
            var skipWorldspaceFormIds = ParseHexFormIdSet(skipWorldspaceArgs);
            var skipRecordTypeArgs = parseResult.GetValue(skipRecordTypeOpt) ?? [];
            var skipRecordTypes = new HashSet<string>(
                skipRecordTypeArgs.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()),
                StringComparer.Ordinal);
            var diagnosticKeepMasterFormIds = ParseDiagnosticFormIdSet(
                parseResult.GetValue(diagnosticKeepMasterFormIdOpt) ?? []);
            var diagnosticRetainMasterSubrecords = ParseDiagnosticSubrecordRetentions(
                parseResult.GetValue(diagnosticRetainMasterSubrecordsOpt) ?? []);

            await RunAsync(dmp, pcEsm, output, author, description, compress, validate, verbose, eventLogJsonl,
                secondaryData, secondaryData360, packAssets, writeMissingList, dialogueAudioCsv, overrideVanilla,
                disableRefrEditorIdRemap, replaceCellTemporaries, recoverGaps, emitMasterCellNavmAugmentation,
                recoverLeveledSpawns, inferUnresolvedCells, diagSkipCellNavm, diagSkipCellNewRefs, cellAuthorityPath,
                skipWorldspaceFormIds, skipRecordTypes, diagnosticKeepMasterFormIds,
                diagnosticRetainMasterSubrecords, ct);
        });

        return command;
    }

    private static async Task RunAsync(
        string dmpPath,
        string pcEsmPath,
        string outputPath,
        string? author,
        string? description,
        bool compress,
        bool validate,
        bool verbose,
        string? eventLogJsonlPath,
        string[] secondaryDataFolders,
        string[] secondaryDataFolders360,
        string? packAssetsBsaPath,
        bool writeMissingList,
        string[] dialogueAudioCsvPaths,
        bool overrideVanilla,
        bool disableRefrEditorIdRemap,
        bool replaceCellTemporaries,
        bool recoverGaps,
        bool emitMasterCellNavmAugmentation,
        bool recoverLeveledSpawns,
        bool inferUnresolvedCells,
        bool diagnosticSkipCellNavm,
        bool diagnosticSkipCellNewRefs,
        string? cellAuthorityPath,
        HashSet<uint> skipWorldspaceFormIds,
        HashSet<string> skipRecordTypes,
        ImmutableHashSet<uint> diagnosticKeepMasterFormIds,
        ImmutableDictionary<uint, ImmutableHashSet<string>> diagnosticRetainMasterSubrecords,
        CancellationToken ct)
    {
        if (!File.Exists(dmpPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] DMP file not found: {Markup.Escape(dmpPath)}");
            Environment.Exit(1);
            return;
        }

        if (!File.Exists(pcEsmPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] PC ESM not found: {Markup.Escape(pcEsmPath)}");
            Environment.Exit(1);
            return;
        }

        var pcEsmFileSize = new FileInfo(pcEsmPath).Length;

        // Keep shared archive handles warm across the whole conversion: the asset-rename phase
        // (inside PluginConversionPipeline) and the pack phase index the same Data folders back-to-back, and
        // without this scope the registry's dispose-at-zero would re-parse every archive between
        // them. Handles release when the command finishes.
        using var warmArchives = ArchiveHandleRegistry.Shared.KeepWarm();

        // v22 asset-rename: when any secondary data folder is supplied, also feed the
        // PluginConversionPipeline so it can rewrite record paths in-place to match unified asset
        // names that survived in the indexed Data folders under different filenames.
        // Baseline = the directory containing the master ESM (the user's FNV PC Data\).
        var baselineDataFolder = Path.GetDirectoryName(pcEsmPath);
        var renameFolders = BuildRenameFolders(secondaryDataFolders, secondaryDataFolders360);

        // Cell authority: same loader cell-inventory uses. Auto-probes default paths when
        // --cell-authority isn't given; emits a one-line status either way.
        var authorityLoad = CellWorldspaceAuthorityJson.Load(cellAuthorityPath);
        if (authorityLoad.Warning is not null)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(authorityLoad.Warning)}[/]");
        }
        else if (authorityLoad.Cells is not null && authorityLoad.Path is not null)
        {
            AnsiConsole.MarkupLine(
                $"[cyan]Cell authority:[/] {authorityLoad.Cells.Count:N0} entries from " +
                $"{Markup.Escape(Path.GetFileName(authorityLoad.Path))}" +
                $"{(authorityLoad.RefToCell is { Count: > 0 } refs ? $", {refs.Count:N0} ref→cell entries" : "")}" +
                $"{(authorityLoad.RefWindows is { Count: > 0 } windows ? $", {windows.Count:N0} ref window(s)" : "")}");
        }

        var options = new PluginBuildOptions
        {
            MasterFileName = Path.GetFileName(pcEsmPath),
            MasterFileSize = pcEsmFileSize,
            Author = string.IsNullOrEmpty(author) ? null : author,
            Description = string.IsNullOrEmpty(description) ? null : description,
            CompressRecords = compress,
            ValidateOutput = validate,
            VerboseDecisions = verbose,
            AssetRenameBaselineFolder = renameFolders.Count > 0 ? baselineDataFolder : null,
            AssetRenameSecondaryFolders = renameFolders,
            AssetRenameOverrideVanilla = overrideVanilla,
            EnableRefrBaseEditorIdRemap = !disableRefrEditorIdRemap,
            ReplaceCellTemporariesOnOverride = replaceCellTemporaries,
            RecoverGaps = recoverGaps,
            EmitMasterCellNavmAugmentation = emitMasterCellNavmAugmentation,
            RecoverLeveledSpawnActors = recoverLeveledSpawns,
            InferUnresolvedCellPlacements = inferUnresolvedCells,
            DiagnosticSkipCellNavm = diagnosticSkipCellNavm,
            DiagnosticSkipCellNewRefs = diagnosticSkipCellNewRefs,
            CellWorldspaceAuthority = authorityLoad.CellToWorldspace,
            CellMetadataAuthority = authorityLoad.Cells,
            CellReferenceParentAuthority = authorityLoad.RefToCell,
            CellReferenceParentWindows = authorityLoad.RefWindows,
            CellWorldspaceAuthorityWorldspaceNames = authorityLoad.WorldspaceNames,
            SkipWorldspaceFormIds = skipWorldspaceFormIds,
            SkipRecordTypes = skipRecordTypes,
            DiagnosticKeepMasterFormIds = diagnosticKeepMasterFormIds,
            DiagnosticRetainMasterSubrecords = diagnosticRetainMasterSubrecords,
            DialogueTextOverridesCsvPaths = dialogueAudioCsvPaths
        };

        if (skipWorldspaceFormIds.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Skipping worldspaces:[/] {string.Join(", ", skipWorldspaceFormIds.Select(f => $"0x{f:X8}"))}");
        }

        if (skipRecordTypes.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Skipping record types:[/] {string.Join(", ", skipRecordTypes)}");
        }

        if (diagnosticKeepMasterFormIds.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Master-pure diagnostic FormIDs:[/] " +
                $"{string.Join(", ", diagnosticKeepMasterFormIds.Order().Select(f => $"0x{f:X8}"))}");
        }

        if (diagnosticRetainMasterSubrecords.Count > 0)
        {
            var retentionSummary = string.Join("; ",
                diagnosticRetainMasterSubrecords.OrderBy(pair => pair.Key).Select(pair =>
                    $"0x{pair.Key:X8}:{string.Join(",", pair.Value.Order(StringComparer.Ordinal))}"));
            AnsiConsole.MarkupLine(
                "[yellow]Master-subrecord diagnostics:[/] " +
                Markup.Escape(retentionSummary));
        }

        if (recoverGaps)
        {
            AnsiConsole.MarkupLine("[yellow]Recover gaps:[/] enabled (experimental, opt-in)");
        }

        if (!inferUnresolvedCells)
        {
            AnsiConsole.MarkupLine("[yellow]Unresolved-cell placement inference disabled (--no-cell-inference).[/]");
        }

        if (!recoverLeveledSpawns)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Leveled-spawn recovery:[/] disabled via --no-recover-leveled-spawns (bare-master baseline)");
        }

        var inputs = new DmpToEsmInputs
        {
            DmpPath = dmpPath,
            PcEsmPath = pcEsmPath,
            OutputEsmPath = outputPath,
            Options = options
        };

        AnsiConsole.MarkupLine($"[cyan]DMP:[/] {Markup.Escape(dmpPath)}");
        AnsiConsole.MarkupLine($"[cyan]Master ESM:[/] {Markup.Escape(pcEsmPath)} ({pcEsmFileSize:N0} bytes)");
        AnsiConsole.MarkupLine($"[cyan]Output:[/] {Markup.Escape(outputPath)}");
        AnsiConsole.WriteLine();

        var registry = RecordEncoderRegistry.CreateDefault();
        var consoleSink = new ConsoleProgressSink(verbose);
        using var jsonlSink = string.IsNullOrWhiteSpace(eventLogJsonlPath)
            ? null
            : new JsonlConversionProgressSink(eventLogJsonlPath);
        IConversionProgressSink sink = jsonlSink is null
            ? consoleSink
            : new TeeProgressSink(consoleSink, jsonlSink);
        var pipeline = new PluginConversionPipeline(registry, sink);

        // Turn on the two opt-in read-side diagnostics for the duration of the build so a DMP→ESM run
        // surfaces silently-dropped subrecords (unmodeled shapes) and schema fallbacks, instead of them
        // being invisible by default. Reset in finally so the global flags don't leak to later work.
        UnmodeledSubrecordLog.Clear();
        UnmodeledSubrecordLog.Enabled = true;
        SubrecordSchemaRegistry.ClearFallbackLog();
        SubrecordSchemaRegistry.EnableFallbackLogging = true;
        PluginBuildResult result;
        try
        {
            result = await pipeline.BuildAsync(inputs, ct);
        }
        finally
        {
            UnmodeledSubrecordLog.Enabled = false;
            SubrecordSchemaRegistry.EnableFallbackLogging = false;
        }

        AnsiConsole.WriteLine();
        ReportReadDiagnostics();
        if (result.Success)
        {
            var s = result.Stats;
            AnsiConsole.MarkupLine("[green]✓ Conversion succeeded.[/]");
            AnsiConsole.MarkupLine(
                $"  Records: considered={s.RecordsConsidered:N0}, emitted={s.RecordsEmitted:N0}, " +
                $"skipped={s.RecordsSkipped:N0}, failed={s.RecordsFailed:N0}");
            AnsiConsole.MarkupLine($"  Overrides={s.OverridesEmitted:N0}, new={s.NewRecordsEmitted:N0}, " +
                                   $"cells={s.CellsMerged:N0}");
            var xespDistinctByStatus = Enum.GetValues<PlannerXespParentStatus>()
                .ToDictionary(
                    status => status,
                    status => s.PlannerXespObservations
                        .Where(observation => observation.ParentStatus == status)
                        .Select(observation => observation.SourceParentFormId)
                        .Distinct()
                        .Count());
            AnsiConsole.MarkupLine(
                $"  Planner XESP sanitation: observed={s.PlannerXespObserved:N0}, " +
                $"emitted={s.PlannerXespEmitted:N0}, skipped={s.PlannerXespSkipped:N0}; " +
                $"distinct parents live-master={xespDistinctByStatus[PlannerXespParentStatus.LiveMaster]:N0}, " +
                $"live-emitted={xespDistinctByStatus[PlannerXespParentStatus.LiveEmitted]:N0}, " +
                $"captured-dropped={xespDistinctByStatus[PlannerXespParentStatus.CapturedDropped]:N0}, " +
                $"runtime={xespDistinctByStatus[PlannerXespParentStatus.RuntimeState]:N0}, " +
                $"absent={xespDistinctByStatus[PlannerXespParentStatus.Absent]:N0}");

            foreach (var group in s.PlannerXespObservations
                         .Where(observation => !observation.XespEmitted
                                               || observation.ParentStatus is not (
                                                   PlannerXespParentStatus.LiveMaster
                                                   or PlannerXespParentStatus.LiveEmitted))
                         .GroupBy(observation => new
                         {
                             observation.XespEmitted,
                             observation.ParentStatus,
                             observation.Reason
                         })
                         .OrderBy(group => group.Key.XespEmitted)
                         .ThenBy(group => group.Key.ParentStatus)
                         .ThenBy(group => group.Key.Reason, StringComparer.Ordinal))
            {
                var detail = $"    XESP {(group.Key.XespEmitted ? "emitted" : "skipped")}: " +
                             $"{group.Key.ParentStatus} ({group.Key.Reason}); " +
                             $"parents={FormatXespParentPreview(group)}";
                AnsiConsole.MarkupLine(Markup.Escape(detail));
            }

            if (s.RecoverableGapCandidates > 0 || s.PromotedGapRawRecords > 0 ||
                s.PromotedGapRuntimeDialogue > 0 || s.PromotedGapPlacedRefs > 0)
            {
                AnsiConsole.MarkupLine(
                    $"  Gap recovery: candidates={s.RecoverableGapCandidates:N0}, " +
                    $"promoted raw={s.PromotedGapRawRecords:N0}, " +
                    $"dialogue={s.PromotedGapRuntimeDialogue:N0}, " +
                    $"placed refs={s.PromotedGapPlacedRefs:N0}, audit/skipped={s.SkippedGapCandidates:N0}");
            }

            AnsiConsole.MarkupLine($"  Output: {s.OutputBytes:N0} bytes in {s.Elapsed.TotalSeconds:F2}s");

            var leveledRecovered = s.DropReasonCounts.GetValueOrDefault("refr.leveled-recovered");
            if (recoverLeveledSpawns || leveledRecovered > 0)
            {
                var stillDangling = s.DropReasonCounts.GetValueOrDefault("refr.dangling-base");
                AnsiConsole.MarkupLine(
                    $"  Leveled-spawn recovery: recovered={leveledRecovered:N0}, still-dropped={stillDangling:N0}");
            }

            if (verbose && s.DropReasonCounts.Count > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[bold]Drop-reason breakdown[/] (descending):");
                foreach (var (code, count) in s.DropReasonCounts.OrderByDescending(kv => kv.Value))
                {
                    AnsiConsole.MarkupLine($"  {count,8:N0}  {code.EscapeMarkup()}");
                }
            }

            if (s.Scols.TotalParsed > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[bold]SCOL Census[/]");
                AnsiConsole.MarkupLine(
                    $"  Parsed={s.Scols.TotalParsed:N0}  InMaster={s.Scols.InMaster:N0}  " +
                    $"NewEmitted={s.Scols.NewEmitted:N0}  AllUnreachableDropped={s.Scols.DroppedAllPartsUnreachable:N0}");
                AnsiConsole.MarkupLine(
                    $"  PartsDroppedTotal={s.Scols.PartsDroppedTotal:N0}  " +
                    $"OverrideDeltaObserved={s.Scols.OverrideDeltaObserved:N0}");
            }

            if (!string.IsNullOrEmpty(result.ValidationReport))
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[bold]Validation:[/]");
                AnsiConsole.WriteLine(result.ValidationReport);
            }

            // If --pack-assets was provided, run the asset packer against the freshly-
            // written ESM. The baseline data folder is derived from the master ESM's
            // location (FNV PC Data\).
            if (!string.IsNullOrEmpty(packAssetsBsaPath))
            {
                await RunAssetPackingAsync(
                    outputPath, dmpPath, pcEsmPath,
                    secondaryDataFolders, secondaryDataFolders360,
                    packAssetsBsaPath, verbose, writeMissingList, dialogueAudioCsvPaths,
                    overrideVanilla, result.NewRecordSourceToAllocated,
                    result.EmittedDialogueAudioBindings, sink, ct);
            }
            else if (dialogueAudioCsvPaths.Length > 0)
            {
                AnsiConsole.MarkupLine(
                    "[yellow]Note:[/] --dialogue-audio-csv was provided without --pack-assets; " +
                    "the CSV Text column was still used to backfill INFO response text in the ESM, " +
                    "but voice audio files were not packed into a BSA.");
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]✗ Conversion failed:[/] {Markup.Escape(result.ErrorMessage ?? "(unknown)")}");
            Environment.Exit(1);
        }
    }

    /// <summary>
    ///     Parse a list of hex form-id strings (with or without 0x prefix) into a set.
    ///     Invalid entries are reported and skipped.
    /// </summary>
    /// <summary>
    ///     Prints a concise summary of the two read-side diagnostics captured during the build:
    ///     subrecords the typed handlers iterated but did not model, and schema-lookup fallbacks.
    ///     Both are silent by default; surfacing them lets a user judge recovery completeness.
    /// </summary>
    private static void ReportReadDiagnostics()
    {
        var unmodeled = UnmodeledSubrecordLog.Snapshot();
        if (unmodeled.Count > 0)
        {
            var occurrences = unmodeled.Sum(e => (long)e.Count);
            var top = string.Join(", ",
                unmodeled.Take(5).Select(e => $"{e.RecordType}/{e.Signature}({e.DataLength})×{e.Count}"));
            AnsiConsole.MarkupLine(
                $"  [yellow]Unmodeled subrecords:[/] {unmodeled.Count:N0} shapes, {occurrences:N0} occurrences — " +
                $"top: {Markup.Escape(top)}");
        }

        var fallbacks = SubrecordSchemaRegistry.GetFallbackUsage().ToList();
        if (fallbacks.Count > 0)
        {
            var top = string.Join(", ",
                fallbacks.Take(5).Select(f => $"{f.RecordType}/{f.Subrecord}:{f.FallbackType}×{f.Count}"));
            AnsiConsole.MarkupLine(
                $"  [yellow]Schema fallbacks:[/] {fallbacks.Count:N0} shapes — top: {Markup.Escape(top)}");
        }
    }

    private static HashSet<uint> ParseHexFormIdSet(string[] hexStrings)
    {
        var set = new HashSet<uint>();
        foreach (var raw in hexStrings)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var s = raw.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                s = s[2..];
            }

            if (uint.TryParse(s, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var fid))
            {
                set.Add(fid);
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]Warning:[/] could not parse FormID '{Markup.Escape(raw)}', skipping.");
            }
        }

        return set;
    }

    /// <summary>
    ///     Strict parser for repeatable exact-record diagnostics. Unlike the older
    ///     skip-worldspace helper, malformed values throw so a bisect can never silently
    ///     run against a different artifact than requested.
    /// </summary>
    internal static ImmutableHashSet<uint> ParseDiagnosticFormIdSet(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var result = ImmutableHashSet.CreateBuilder<uint>();
        foreach (var raw in values)
        {
            result.Add(ParseDiagnosticFormId(raw));
        }

        return result.ToImmutable();
    }

    /// <summary>Parse repeatable <c>FormID:SIG[,SIG...]</c> retention directives.</summary>
    internal static ImmutableDictionary<uint, ImmutableHashSet<string>> ParseDiagnosticSubrecordRetentions(
        IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var byFormId = new Dictionary<uint, ImmutableHashSet<string>.Builder>();
        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new FormatException("Diagnostic master-subrecord directive cannot be empty.");
            }

            var colon = raw.IndexOf(':');
            if (colon <= 0 || colon != raw.LastIndexOf(':') || colon == raw.Length - 1)
            {
                throw new FormatException(
                    $"Invalid diagnostic master-subrecord directive '{raw}'; expected FormID:SIG[,SIG...].");
            }

            var formId = ParseDiagnosticFormId(raw[..colon]);
            var signatureTokens = raw[(colon + 1)..].Split(',');
            if (!byFormId.TryGetValue(formId, out var signatures))
            {
                signatures = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
                byFormId.Add(formId, signatures);
            }

            foreach (var token in signatureTokens)
            {
                var signature = token.Trim().ToUpperInvariant();
                if (signature.Length != 4 || signature.Any(static character =>
                        character is not (>= 'A' and <= 'Z')
                            and not (>= '0' and <= '9')
                            and not '_'))
                {
                    throw new FormatException(
                        $"Invalid subrecord signature '{token}' for 0x{formId:X8}; " +
                        "expected four uppercase letters, digits, or underscores.");
                }

                signatures.Add(signature);
            }
        }

        return byFormId.ToImmutableDictionary(
            pair => pair.Key,
            pair => pair.Value.ToImmutable());
    }

    private static uint ParseDiagnosticFormId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new FormatException("Diagnostic FormID cannot be empty.");
        }

        var token = raw.Trim();
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            token = token[2..];
        }

        if (token.Length is < 1 or > 8
            || !uint.TryParse(token, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var formId)
            || formId == 0)
        {
            throw new FormatException(
                $"Invalid diagnostic FormID '{raw}'; expected a nonzero hexadecimal FormID.");
        }

        return formId;
    }

    private static string FormatXespParentPreview(IEnumerable<PlannerXespObservation> observations)
    {
        const int previewLimit = 16;
        var parents = observations
            .Select(observation => (observation.SourceParentFormId, observation.FinalParentFormId))
            .Distinct()
            .OrderBy(pair => pair.SourceParentFormId)
            .ThenBy(pair => pair.FinalParentFormId)
            .ToList();
        var preview = string.Join(", ", parents.Take(previewLimit).Select(pair =>
            pair.SourceParentFormId == pair.FinalParentFormId
                ? $"0x{pair.SourceParentFormId:X8}"
                : $"0x{pair.SourceParentFormId:X8}->0x{pair.FinalParentFormId:X8}"));

        return parents.Count > previewLimit
            ? $"{preview}, ... (+{parents.Count - previewLimit:N0} more)"
            : preview;
    }

    /// <summary>
    ///     Build the list of (path, isXbox360) pairs shared by the v22 asset-rename pass
    ///     and the v21 asset packer. Missing folders are dropped with a console warning
    ///     (the rename + pack paths each guard against an empty list).
    /// </summary>
    private static List<SecondaryDataFolder> BuildRenameFolders(
        string[] secondaryDataFolders,
        string[] secondaryDataFolders360)
    {
        return BuildSecondaryFolders(secondaryDataFolders, secondaryDataFolders360, false);
    }

    private static List<SecondaryDataFolder> BuildSecondaryFolders(
        string[] secondaryDataFolders,
        string[] secondaryDataFolders360,
        bool warnMissing)
    {
        var folders = new List<SecondaryDataFolder>(
            secondaryDataFolders.Length + secondaryDataFolders360.Length);

        foreach (var folder in secondaryDataFolders)
        {
            if (!Directory.Exists(folder))
            {
                if (warnMissing)
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]Warning:[/] --secondary-data folder not found, skipping: " +
                        $"{Markup.Escape(folder)}");
                }

                continue;
            }

            var isXbox360 = Xbox360FolderDetector.DetectIsXbox360Format(folder);
            folders.Add(new SecondaryDataFolder { Path = folder, IsXbox360Format = isXbox360 });
        }

        foreach (var folder in secondaryDataFolders360)
        {
            if (!Directory.Exists(folder))
            {
                if (warnMissing)
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]Warning:[/] --secondary-data-360 folder not found, skipping: " +
                        $"{Markup.Escape(folder)}");
                }

                continue;
            }

            folders.Add(new SecondaryDataFolder { Path = folder, IsXbox360Format = true });
        }

        return folders;
    }

    private static async Task RunAssetPackingAsync(
        string esmPath,
        string dmpPath,
        string pcEsmPath,
        string[] secondaryDataFolders,
        string[] secondaryDataFolders360,
        string outputBsaPath,
        bool verbose,
        bool writeMissingList,
        string[] dialogueAudioCsvPaths,
        bool overrideVanilla,
        IReadOnlyDictionary<uint, uint> newRecordSourceToAllocatedFormIds,
        IReadOnlyList<EmittedDialogueAudioBinding> emittedDialogueAudioBindings,
        IConversionProgressSink sink,
        CancellationToken ct)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold cyan]▶ Asset packing[/]");

        var baselineDataFolder = Path.GetDirectoryName(pcEsmPath)!;
        if (string.IsNullOrEmpty(baselineDataFolder) || !Directory.Exists(baselineDataFolder))
        {
            AnsiConsole.MarkupLine(
                $"[red]Error:[/] Cannot derive baseline Data folder from --pc-esm path: " +
                $"{Markup.Escape(pcEsmPath)}");
            Environment.Exit(1);
            return;
        }

        var secondaries = BuildSecondaryFolders(secondaryDataFolders, secondaryDataFolders360, true);

        if (secondaries.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Warning:[/] --pack-assets was set but no --secondary-data / " +
                "--secondary-data-360 folders were provided. Nothing to pack.");
            return;
        }

        var options = new AssetPackingOptions
        {
            ConvertedEsmPath = esmPath,
            DmpPath = dmpPath,
            BaselineDataFolder = baselineDataFolder,
            SecondaryDataFolders = secondaries,
            OutputBsaPath = outputBsaPath,
            VerbosePerAsset = verbose,
            WriteAuditFile = writeMissingList,
            DialogueAudioCsvPaths = dialogueAudioCsvPaths,
            OverrideVanillaBaseline = overrideVanilla,
            NewRecordSourceToAllocatedFormIds = newRecordSourceToAllocatedFormIds,
            EmittedDialogueAudioBindings = emittedDialogueAudioBindings
        };

        var result = await AssetPackingService.PackAsync(options, sink, ct);

        AnsiConsole.WriteLine();
        if (result.Success)
        {
            var s = result.Stats;
            AnsiConsole.MarkupLine("[green]✓ Asset packing succeeded.[/]");
            AnsiConsole.MarkupLine(
                $"  Paths scanned: {s.TotalPathsScanned:N0}  " +
                $"(baseline-already-has={s.AlreadyInBaseline:N0}, " +
                $"resolved-exact={s.ResolvedExact:N0}, " +
                $"resolved-fuzzy={s.ResolvedFuzzy:N0}, " +
                $"converted-360={s.Converted360:N0}, " +
                $"conversion-failed={s.ConversionFailed:N0}, " +
                $"missing={s.Missing:N0})");
            if (result.OutputPaths.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    $"  Packed: {s.PackedAssetCount:N0} assets in {s.OutputBsaSizeBytes:N0} bytes " +
                    $"across {result.OutputPaths.Count:N0} archive(s) ({s.Elapsed.TotalSeconds:F2}s)");
                foreach (var outputPath in result.OutputPaths)
                {
                    AnsiConsole.MarkupLine($"  Output: {Markup.Escape(outputPath)}");
                }
            }
            else
            {
                AnsiConsole.MarkupLine("  [grey]No BSA was written (no assets needed packing).[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[red]✗ Asset packing failed:[/] {Markup.Escape(result.ErrorMessage ?? "(unknown)")}");
            Environment.Exit(1);
        }
    }

    private sealed class TeeProgressSink(
        IConversionProgressSink first,
        IConversionProgressSink second) : IConversionProgressSink
    {
        public void OnPhaseStart(string phase, int? totalItems)
        {
            first.OnPhaseStart(phase, totalItems);
            second.OnPhaseStart(phase, totalItems);
        }

        public void OnEvent(ConversionProgressEvent evt)
        {
            first.OnEvent(evt);
            second.OnEvent(evt);
        }

        public void OnPhaseEnd(string phase, ConversionPipelineStats partialStats)
        {
            first.OnPhaseEnd(phase, partialStats);
            second.OnPhaseEnd(phase, partialStats);
        }

        public void OnComplete(ConversionPipelineStats stats)
        {
            first.OnComplete(stats);
            second.OnComplete(stats);
        }
    }

    /// <summary>
    ///     IConversionProgressSink that writes events to the console. Mirrors the WinUI
    ///     tab's channel-based sink but synchronous (no UI dispatcher).
    /// </summary>
    private sealed class ConsoleProgressSink(bool verbose) : IConversionProgressSink
    {
        public void OnPhaseStart(string phase, int? totalItems)
        {
            AnsiConsole.MarkupLine($"[bold cyan]▶ {Markup.Escape(phase)}[/]");
        }

        public void OnEvent(ConversionProgressEvent evt)
        {
            // Drop info events unless --verbose; warnings and errors always print.
            if (evt.Severity == ConversionEventSeverity.Info && !verbose)
            {
                return;
            }

            var label = evt.Severity switch
            {
                ConversionEventSeverity.Info => "[grey]INFO[/]",
                ConversionEventSeverity.Decision => "[blue]DEC[/]",
                ConversionEventSeverity.Warning => "[yellow]WARN[/]",
                ConversionEventSeverity.Error => "[red]ERR[/]",
                _ => Markup.Escape(evt.Severity.ToString())
            };

            var formId = evt.FormId.HasValue ? $" 0x{evt.FormId.Value:X8}" : "";
            var type = string.IsNullOrEmpty(evt.FormType) ? "" : $" {evt.FormType}";
            AnsiConsole.MarkupLine($"  {label}{type}{formId}: {Markup.Escape(evt.Message)}");
        }

        public void OnPhaseEnd(string phase, ConversionPipelineStats partialStats)
        {
            // Running stats are reflected in the final summary; nothing to print per phase.
        }

        public void OnComplete(ConversionPipelineStats stats)
        {
            // Final summary printed by caller.
        }
    }
}
