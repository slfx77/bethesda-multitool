using System.CommandLine;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Dmp;

/// <summary>
///     CLI command for running the DMP→ESP plugin conversion pipeline. Same engine as the
///     WinUI tab — reads an Xbox 360 memory dump, merges its records into a PC plugin
///     authored against the provided master ESM.
/// </summary>
public static class DmpToEspCommand
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
            Description = "Output ESP path",
            Required = true
        };
        var authorOpt = new Option<string?>("--author") { Description = "Plugin author metadata" };
        var descriptionOpt = new Option<string?>("--description") { Description = "Plugin description" };
        var compressOpt = new Option<bool>("--compress") { Description = "Compress record bodies (zlib)" };
        var validateOpt = new Option<bool>("--validate")
        {
            Description = "Re-parse the output ESP to validate structure",
            DefaultValueFactory = _ => true
        };
        var verboseOpt = new Option<bool>("--verbose")
        {
            Description = "Emit per-record decision events (very chatty)"
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
            Description = "Output BSA path. When set, after the ESP is written the converter scans " +
                          "the ESP + DMP for referenced assets, resolves them against --secondary-data / " +
                          "--secondary-data-360 folders, and packs the missing ones into this BSA."
        };
        var writeMissingListOpt = new Option<bool>("--write-missing-list")
        {
            Description = "Write a human-reviewable per-asset audit file at <pack-assets>.missing.txt " +
                          "alongside the packed BSA. Sections: missing, fuzzy-matched, conversion-failed."
        };
        var dialogueAudioCsvOpt = new Option<string[]>("--dialogue-audio-csv")
        {
            Description = "Repeatable: Fallout Audio Transcriber CSV export used to add dialogue voice " +
                          "audio/lip requests for INFO records present in the ESP/DMP. Use with " +
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

        var command = new Command("to-esp", "Convert a DMP to a PC plugin ESP overlay against a master ESM");
        command.Arguments.Add(dmpArg);
        command.Options.Add(pcEsmOpt);
        command.Options.Add(outputOpt);
        command.Options.Add(authorOpt);
        command.Options.Add(descriptionOpt);
        command.Options.Add(compressOpt);
        command.Options.Add(validateOpt);
        command.Options.Add(verboseOpt);
        command.Options.Add(secondaryDataOpt);
        command.Options.Add(secondaryData360Opt);
        command.Options.Add(packAssetsOpt);
        command.Options.Add(writeMissingListOpt);
        command.Options.Add(dialogueAudioCsvOpt);
        command.Options.Add(overrideVanillaOpt);
        command.Options.Add(disableRefrEditorIdRemapOpt);
        command.Options.Add(replaceCellTemporariesOpt);

        var cellAuthorityOpt = new Option<string?>("--cell-authority")
        {
            Description =
                "Optional corpus-derived CellFormId→WorldspaceFormId authority JSON (built with " +
                "`dmp build-cell-authority`). Applied to parsed cells before grouping so cells " +
                "land under the correct WRLD in the output ESP. Defaults to " +
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
            var secondaryData = parseResult.GetValue(secondaryDataOpt) ?? [];
            var secondaryData360 = parseResult.GetValue(secondaryData360Opt) ?? [];
            var packAssets = parseResult.GetValue(packAssetsOpt);
            var writeMissingList = parseResult.GetValue(writeMissingListOpt);
            var dialogueAudioCsv = parseResult.GetValue(dialogueAudioCsvOpt) ?? [];
            var overrideVanilla = parseResult.GetValue(overrideVanillaOpt);
            var disableRefrEditorIdRemap = parseResult.GetValue(disableRefrEditorIdRemapOpt);
            var replaceCellTemporaries = parseResult.GetValue(replaceCellTemporariesOpt);
            var cellAuthorityPath = parseResult.GetValue(cellAuthorityOpt);
            var skipWorldspaceArgs = parseResult.GetValue(skipWorldspaceOpt) ?? [];
            var skipWorldspaceFormIds = ParseHexFormIdSet(skipWorldspaceArgs);
            var skipRecordTypeArgs = parseResult.GetValue(skipRecordTypeOpt) ?? [];
            var skipRecordTypes = new HashSet<string>(
                skipRecordTypeArgs.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()),
                StringComparer.Ordinal);

            await RunAsync(dmp, pcEsm, output, author, description, compress, validate, verbose,
                secondaryData, secondaryData360, packAssets, writeMissingList, dialogueAudioCsv, overrideVanilla,
                disableRefrEditorIdRemap, replaceCellTemporaries, cellAuthorityPath,
                skipWorldspaceFormIds, skipRecordTypes, ct);
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
        string[] secondaryDataFolders,
        string[] secondaryDataFolders360,
        string? packAssetsBsaPath,
        bool writeMissingList,
        string[] dialogueAudioCsvPaths,
        bool overrideVanilla,
        bool disableRefrEditorIdRemap,
        bool replaceCellTemporaries,
        string? cellAuthorityPath,
        IReadOnlySet<uint> skipWorldspaceFormIds,
        IReadOnlySet<string> skipRecordTypes,
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

        // v22 asset-rename: when any secondary data folder is supplied, also feed the
        // PluginBuilder so it can rewrite record paths in-place to match unified asset
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
                $"{Markup.Escape(Path.GetFileName(authorityLoad.Path))}");
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
            CellWorldspaceAuthority = authorityLoad.Cells,
            CellWorldspaceAuthorityWorldspaceNames = authorityLoad.WorldspaceNames,
            SkipWorldspaceFormIds = skipWorldspaceFormIds,
            SkipRecordTypes = skipRecordTypes
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

        var inputs = new DmpToEspInputs
        {
            DmpPath = dmpPath,
            PcEsmPath = pcEsmPath,
            OutputEspPath = outputPath,
            Options = options
        };

        AnsiConsole.MarkupLine($"[cyan]DMP:[/] {Markup.Escape(dmpPath)}");
        AnsiConsole.MarkupLine($"[cyan]Master ESM:[/] {Markup.Escape(pcEsmPath)} ({pcEsmFileSize:N0} bytes)");
        AnsiConsole.MarkupLine($"[cyan]Output:[/] {Markup.Escape(outputPath)}");
        AnsiConsole.WriteLine();

        var registry = RecordEncoderRegistry.CreateDefault();
        var sink = new ConsoleProgressSink(verbose);
        var pipeline = new PluginConversionPipeline(registry, sink);

        var result = await pipeline.BuildAsync(inputs, ct);

        AnsiConsole.WriteLine();
        if (result.Success)
        {
            var s = result.Stats;
            AnsiConsole.MarkupLine("[green]✓ Conversion succeeded.[/]");
            AnsiConsole.MarkupLine(
                $"  Records: considered={s.RecordsConsidered:N0}, emitted={s.RecordsEmitted:N0}, " +
                $"skipped={s.RecordsSkipped:N0}, failed={s.RecordsFailed:N0}");
            AnsiConsole.MarkupLine($"  Overrides={s.OverridesEmitted:N0}, new={s.NewRecordsEmitted:N0}, " +
                                   $"cells={s.CellsMerged:N0}");
            AnsiConsole.MarkupLine($"  Output: {s.OutputBytes:N0} bytes in {s.Elapsed.TotalSeconds:F2}s");

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
            // written ESP. The baseline data folder is derived from the master ESM's
            // location (FNV PC Data\).
            if (!string.IsNullOrEmpty(packAssetsBsaPath))
            {
                await RunAssetPackingAsync(
                    outputPath, dmpPath, pcEsmPath,
                    secondaryDataFolders, secondaryDataFolders360,
                    packAssetsBsaPath, verbose, writeMissingList, dialogueAudioCsvPaths,
                    overrideVanilla, sink, ct);
            }
            else if (dialogueAudioCsvPaths.Length > 0)
            {
                AnsiConsole.MarkupLine(
                    "[yellow]Warning:[/] --dialogue-audio-csv was provided but --pack-assets was not; " +
                    "dialogue audio CSV paths were not used.");
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
    private static IReadOnlySet<uint> ParseHexFormIdSet(string[] hexStrings)
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
            if (uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var fid))
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
    ///     Build the list of (path, isXbox360) pairs shared by the v22 asset-rename pass
    ///     and the v21 asset packer. Missing folders are dropped with a console warning
    ///     (the rename + pack paths each guard against an empty list).
    /// </summary>
    private static IReadOnlyList<SecondaryDataFolder> BuildRenameFolders(
        string[] secondaryDataFolders,
        string[] secondaryDataFolders360)
    {
        return BuildSecondaryFolders(secondaryDataFolders, secondaryDataFolders360, warnMissing: false);
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
        string espPath,
        string dmpPath,
        string pcEsmPath,
        string[] secondaryDataFolders,
        string[] secondaryDataFolders360,
        string outputBsaPath,
        bool verbose,
        bool writeMissingList,
        string[] dialogueAudioCsvPaths,
        bool overrideVanilla,
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

        var secondaries = BuildSecondaryFolders(secondaryDataFolders, secondaryDataFolders360, warnMissing: true);

        if (secondaries.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Warning:[/] --pack-assets was set but no --secondary-data / " +
                "--secondary-data-360 folders were provided. Nothing to pack.");
            return;
        }

        var options = new AssetPackingOptions
        {
            ConvertedEspPath = espPath,
            DmpPath = dmpPath,
            BaselineDataFolder = baselineDataFolder,
            SecondaryDataFolders = secondaries,
            OutputBsaPath = outputBsaPath,
            VerbosePerAsset = verbose,
            WriteAuditFile = writeMissingList,
            DialogueAudioCsvPaths = dialogueAudioCsvPaths,
            OverrideVanillaBaseline = overrideVanilla
        };

        var service = new AssetPackingService();
        var result = await service.PackAsync(options, sink, ct);

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
            if (result.OutputPath is not null)
            {
                AnsiConsole.MarkupLine(
                    $"  Packed: {s.PackedAssetCount:N0} assets in {s.OutputBsaSizeBytes:N0} bytes " +
                    $"({s.Elapsed.TotalSeconds:F2}s)");
                AnsiConsole.MarkupLine($"  Output: {Markup.Escape(result.OutputPath)}");
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
