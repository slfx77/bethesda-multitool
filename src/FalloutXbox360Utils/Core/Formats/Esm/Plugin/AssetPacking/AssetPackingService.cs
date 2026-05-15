using System.Diagnostics;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;
using FalloutXbox360Utils.Core.Semantic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Orchestrates the prototype asset packer:
///
///     <list type="number">
///         <item><description>Loads the converted ESP into a <see cref="Models.RecordCollection"/>.</description></item>
///         <item><description>Collects every referenced asset path from records + raw DMP bytes.</description></item>
///         <item><description>Builds a <see cref="DataFolderIndex"/> for the FNV baseline and each secondary folder.</description></item>
///         <item><description>Resolves each requested path through <see cref="DataFolderResolver"/>.</description></item>
///         <item><description>Converts 360-format bytes to PC format on the fly via <see cref="PrototypeAssetConverter"/>.</description></item>
///         <item><description>Writes the resolved set to a new BSA via <see cref="BsaWriter"/>.</description></item>
///     </list>
/// </summary>
public sealed class AssetPackingService
{
    /// <summary>Run an asset packing job end-to-end.</summary>
    public async Task<AssetPackingResult> PackAsync(
        AssetPackingOptions options,
        IConversionProgressSink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sink);

        var stopwatch = Stopwatch.StartNew();
        var resolutions = new List<AssetResolution>();

        sink.OnPhaseStart("AssetPacking", null);

        try
        {
            // 1) Parse the converted ESP back into a RecordCollection.
            sink.Info("AssetPacking", $"Loading converted ESP: {Path.GetFileName(options.ConvertedEspPath)}");
            using var espResult = await SemanticFileLoader
                .LoadAsync(options.ConvertedEspPath, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // 2) Collect every referenced asset path.
            var requested = AssetPathCollector.Collect(espResult.Records, options.DmpPath, sink);
            sink.Info("AssetPacking", $"Total unique asset paths to resolve: {requested.Count}");

            if (requested.Count == 0)
            {
                return BuildEmptyResult(options, sink, resolutions, stopwatch, runningStats: null);
            }

            // 3) Build baseline and secondary folder indexes.
            sink.Info("AssetPacking", $"Indexing baseline data folder: {options.BaselineDataFolder}");
            using var baseline = new DataFolderIndex(options.BaselineDataFolder, xbox360FormatHint: false);
            baseline.Build();
            sink.Info("AssetPacking", $"Baseline indexed {baseline.EntryCount} entries");

            var secondaryDisposables = new List<DataFolderIndex>();
            try
            {
                foreach (var secondary in options.SecondaryDataFolders)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sink.Info("AssetPacking",
                        $"Indexing secondary folder ({(secondary.IsXbox360Format ? "Xbox 360" : "PC")}): {secondary.Path}");
                    var idx = new DataFolderIndex(secondary.Path, secondary.IsXbox360Format);
                    idx.Build();
                    sink.Info("AssetPacking",
                        $"  → {idx.EntryCount} entries");
                    secondaryDisposables.Add(idx);
                }

                var resolver = new DataFolderResolver(
                    baseline, secondaryDisposables, options.OverrideVanillaBaseline);
                var converter = new PrototypeAssetConverter();

                // 4) Resolve + convert + collect bytes to pack.
                var packedFiles = new List<(string Path, byte[] Data)>();
                var stats = new RunningStats();

                foreach (var requestedPath in requested)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var resolution = resolver.Resolve(requestedPath);
                    stats.Total++;

                    switch (resolution.Kind)
                    {
                        case AssetResolutionKind.AlreadyInBaseline:
                            stats.AlreadyInBaseline++;
                            // Don't emit a resolution entry for these — would balloon the log.
                            break;

                        case AssetResolutionKind.ResolvedExact:
                        case AssetResolutionKind.ResolvedFuzzy:
                            await TryPackAsync(
                                converter,
                                requestedPath,
                                resolution,
                                options,
                                packedFiles,
                                stats,
                                resolutions,
                                sink,
                                cancellationToken).ConfigureAwait(false);
                            break;

                        case AssetResolutionKind.Missing:
                            stats.Missing++;
                            resolutions.Add(new AssetResolution
                            {
                                RequestedPath = requestedPath,
                                Kind = AssetResolutionKind.Missing
                            });
                            if (options.VerbosePerAsset)
                            {
                                sink.Warn("AssetPacking", $"Missing: {requestedPath}");
                            }

                            break;
                    }
                }

                sink.Info("AssetPacking",
                    $"Summary: already-in-baseline={stats.AlreadyInBaseline}, " +
                    $"resolved-exact={stats.ResolvedExact}, resolved-fuzzy={stats.ResolvedFuzzy}, " +
                    $"converted-360={stats.Converted360}, conversion-failed={stats.ConversionFailed}, " +
                    $"missing={stats.Missing}");

                if (packedFiles.Count == 0)
                {
                    return BuildEmptyResult(options, sink, resolutions, stopwatch, runningStats: stats);
                }

                // 5) Write the output BSA.
                Directory.CreateDirectory(Path.GetDirectoryName(options.OutputBsaPath)!);

                sink.Info("AssetPacking",
                    $"Writing {packedFiles.Count} assets to {Path.GetFileName(options.OutputBsaPath)}");

                using var writer = BsaWriter.CreateWithAutoFlags(packedFiles.Select(p => p.Path));
                foreach (var (path, data) in packedFiles)
                {
                    writer.AddFile(path, data);
                }

                writer.Write(options.OutputBsaPath);

                var outputSize = new FileInfo(options.OutputBsaPath).Length;
                stopwatch.Stop();

                // Drop a per-asset audit next to the BSA so the user can review what
                // resolved, what fuzzy-matched, and what stayed missing. Sorted, deduped,
                // one path per line within each section. Opt-in via WriteAuditFile.
                if (options.WriteAuditFile)
                {
                    TryWriteAuditFile(options.OutputBsaPath, resolutions, stats, sink);
                }

                sink.OnPhaseEnd("AssetPacking", new ConversionPipelineStats());
                sink.Info("AssetPacking",
                    $"BSA written: {outputSize:N0} bytes in {stopwatch.Elapsed.TotalSeconds:F2}s");

                return new AssetPackingResult
                {
                    Success = true,
                    OutputPath = options.OutputBsaPath,
                    Stats = new AssetPackingStats
                    {
                        TotalPathsScanned = stats.Total,
                        AlreadyInBaseline = stats.AlreadyInBaseline,
                        ResolvedExact = stats.ResolvedExact,
                        ResolvedFuzzy = stats.ResolvedFuzzy,
                        Converted360 = stats.Converted360,
                        ConversionFailed = stats.ConversionFailed,
                        Missing = stats.Missing,
                        PackedAssetCount = packedFiles.Count,
                        OutputBsaSizeBytes = outputSize,
                        Elapsed = stopwatch.Elapsed
                    },
                    Resolutions = resolutions
                };
            }
            finally
            {
                foreach (var idx in secondaryDisposables)
                {
                    idx.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            sink.Warn("AssetPacking", "Asset packing canceled");
            return new AssetPackingResult
            {
                Success = false,
                ErrorMessage = "Asset packing canceled",
                Stats = EmptyStats(stopwatch.Elapsed),
                Resolutions = resolutions
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            sink.Error("AssetPacking", $"Asset packing failed: {ex.Message}");
            return new AssetPackingResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Stats = EmptyStats(stopwatch.Elapsed),
                Resolutions = resolutions
            };
        }
    }

    private static async Task TryPackAsync(
        PrototypeAssetConverter converter,
        string requestedPath,
        DataFolderResolution resolution,
        AssetPackingOptions options,
        List<(string Path, byte[] Data)> packedFiles,
        RunningStats stats,
        List<AssetResolution> resolutions,
        IConversionProgressSink sink,
        CancellationToken cancellationToken)
    {
        if (resolution.Source is null || resolution.ResolvedPath is null)
        {
            stats.Missing++;
            resolutions.Add(new AssetResolution
            {
                RequestedPath = requestedPath,
                Kind = AssetResolutionKind.Missing
            });
            return;
        }

        if (resolution.Kind == AssetResolutionKind.ResolvedFuzzy && !options.IncludeFuzzyMatches)
        {
            // User opted out of fuzzy packing.
            stats.Missing++;
            resolutions.Add(new AssetResolution
            {
                RequestedPath = requestedPath,
                Kind = AssetResolutionKind.Missing
            });
            return;
        }

        byte[] rawBytes;
        try
        {
            rawBytes = resolution.Source.Read();
        }
        catch (Exception ex)
        {
            sink.Warn("AssetPacking", $"Read failed for {resolution.ResolvedPath}: {ex.Message}");
            stats.Missing++;
            resolutions.Add(new AssetResolution
            {
                RequestedPath = requestedPath,
                Kind = AssetResolutionKind.Missing
            });
            return;
        }

        // 360-format sources go through PrototypeAssetConverter; PC-format ones pass through.
        var outputPath = resolution.Source.NormalizedPath;
        var outputBytes = rawBytes;
        var wasConverted = false;
        string? conversionError = null;

        if (resolution.Source.IsXbox360)
        {
            var converted = await converter
                .ConvertAsync(rawBytes, resolution.Source.NormalizedPath, cancellationToken)
                .ConfigureAwait(false);

            if (!converted.Success)
            {
                stats.ConversionFailed++;
                conversionError = converted.FailureReason;
                resolutions.Add(new AssetResolution
                {
                    RequestedPath = requestedPath,
                    Kind = AssetResolutionKind.ConversionFailed,
                    ResolvedPath = resolution.ResolvedPath,
                    SourceFolder = $"#{resolution.SourceFolderIndex}",
                    ConversionError = conversionError
                });
                if (options.VerbosePerAsset)
                {
                    sink.Warn("AssetPacking",
                        $"Conversion failed: {resolution.ResolvedPath} — {conversionError}");
                }

                return;
            }

            outputPath = converted.OutputPath;
            outputBytes = converted.Data;
            wasConverted = converted.WasConverted;
        }

        // We always pack under the REQUESTED path, not the resolved path. The runtime asked
        // for X, so we deliver bytes at X. (This is what makes fuzzy-match meaningful — we
        // re-home the candidate to satisfy the missing reference.)
        var packedPath = requestedPath;

        // If we converted an extension (.ddx → .dds, .xma → .wav), apply the same swap to
        // the requested path so the runtime finds the converted output. Otherwise the
        // record's reference would still point to the original extension.
        var originalExt = Path.GetExtension(resolution.Source.NormalizedPath);
        var convertedExt = Path.GetExtension(outputPath);
        if (!string.Equals(originalExt, convertedExt, StringComparison.OrdinalIgnoreCase))
        {
            packedPath = Path.ChangeExtension(requestedPath, convertedExt);
        }

        packedFiles.Add((packedPath, outputBytes));

        var kind = (resolution.Kind, wasConverted) switch
        {
            (AssetResolutionKind.ResolvedExact, true) => AssetResolutionKind.ResolvedExactConverted,
            (AssetResolutionKind.ResolvedFuzzy, true) => AssetResolutionKind.ResolvedFuzzyConverted,
            _ => resolution.Kind
        };

        switch (kind)
        {
            case AssetResolutionKind.ResolvedExact:
                stats.ResolvedExact++;
                break;
            case AssetResolutionKind.ResolvedFuzzy:
                stats.ResolvedFuzzy++;
                break;
            case AssetResolutionKind.ResolvedExactConverted:
                stats.ResolvedExact++;
                stats.Converted360++;
                break;
            case AssetResolutionKind.ResolvedFuzzyConverted:
                stats.ResolvedFuzzy++;
                stats.Converted360++;
                break;
        }

        resolutions.Add(new AssetResolution
        {
            RequestedPath = requestedPath,
            Kind = kind,
            ResolvedPath = outputPath,
            SourceFolder = $"#{resolution.SourceFolderIndex}",
            FuzzySuffixTokens = resolution.FuzzySuffixTokens
        });

        if (options.VerbosePerAsset)
        {
            sink.Info("AssetPacking",
                $"{kind}: {requestedPath} ← {outputPath} (folder #{resolution.SourceFolderIndex}" +
                (resolution.FuzzySuffixTokens > 0 ? $", suffix={resolution.FuzzySuffixTokens}" : "") + ")");
        }
    }

    private static AssetPackingResult BuildEmptyResult(
        AssetPackingOptions options,
        IConversionProgressSink sink,
        List<AssetResolution> resolutions,
        Stopwatch stopwatch,
        RunningStats? runningStats)
    {
        stopwatch.Stop();
        sink.Info("AssetPacking", "No assets needed packing — output BSA not written");

        // Even when no BSA is written, drop the audit file next to where it would have
        // gone so the user can still review the resolution outcome (especially missing
        // paths — those are the most important diagnostic). Opt-in via WriteAuditFile.
        if (runningStats is not null && options.WriteAuditFile)
        {
            TryWriteAuditFile(options.OutputBsaPath, resolutions, runningStats, sink);
        }

        sink.OnPhaseEnd("AssetPacking", new ConversionPipelineStats());
        var stats = runningStats is null
            ? new AssetPackingStats { Elapsed = stopwatch.Elapsed }
            : new AssetPackingStats
            {
                TotalPathsScanned = runningStats.Total,
                AlreadyInBaseline = runningStats.AlreadyInBaseline,
                ResolvedExact = runningStats.ResolvedExact,
                ResolvedFuzzy = runningStats.ResolvedFuzzy,
                Converted360 = runningStats.Converted360,
                ConversionFailed = runningStats.ConversionFailed,
                Missing = runningStats.Missing,
                PackedAssetCount = 0,
                OutputBsaSizeBytes = 0,
                Elapsed = stopwatch.Elapsed
            };
        return new AssetPackingResult
        {
            Success = true,
            OutputPath = null,
            Stats = stats,
            Resolutions = resolutions
        };
    }

    private static AssetPackingStats EmptyStats(TimeSpan elapsed) => new() { Elapsed = elapsed };

    /// <summary>
    ///     Writes a human-reviewable per-asset audit next to the output BSA. Sections:
    ///     missing paths (the most useful — these are what the runtime won't find), fuzzy-
    ///     matched paths (sanity check the renames), and conversion-failed paths. Each
    ///     section is sorted alphabetically with one path per line so the user can diff
    ///     between runs.
    /// </summary>
    private static void TryWriteAuditFile(
        string outputBsaPath,
        IReadOnlyList<AssetResolution> resolutions,
        RunningStats stats,
        IConversionProgressSink sink)
    {
        try
        {
            var auditPath = outputBsaPath + ".missing.txt";
            using var writer = new StreamWriter(auditPath);

            writer.WriteLine($"# Asset packing audit for {Path.GetFileName(outputBsaPath)}");
            writer.WriteLine($"# Generated: {DateTime.UtcNow:O}");
            writer.WriteLine($"# Total paths scanned: {stats.Total}");
            writer.WriteLine($"# Already in baseline (skipped pack): {stats.AlreadyInBaseline}");
            writer.WriteLine($"# Resolved exact:                     {stats.ResolvedExact}");
            writer.WriteLine($"# Resolved fuzzy:                     {stats.ResolvedFuzzy}");
            writer.WriteLine($"# 360 → PC converted:                 {stats.Converted360}");
            writer.WriteLine($"# Conversion failed:                  {stats.ConversionFailed}");
            writer.WriteLine($"# Missing (unresolved):               {stats.Missing}");
            writer.WriteLine();

            WriteAuditSection(writer,
                "## MISSING — runtime will fail to load these (not in baseline, no fuzzy hit)",
                resolutions.Where(r => r.Kind == AssetResolutionKind.Missing));

            WriteAuditSection(writer,
                "## CONVERSION FAILED — 360→PC conversion errored; original bytes packed as fallback",
                resolutions.Where(r => r.Kind == AssetResolutionKind.ConversionFailed),
                includeError: true);

            WriteAuditSection(writer,
                "## FUZZY-MATCHED — same basename, different directory; sanity-check these",
                resolutions.Where(r =>
                    r.Kind is AssetResolutionKind.ResolvedFuzzy
                        or AssetResolutionKind.ResolvedFuzzyConverted),
                includeResolved: true);

            sink.Info("AssetPacking", $"Audit file written: {auditPath}");
        }
        catch (Exception ex)
        {
            sink.Warn("AssetPacking", $"Could not write audit file: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void WriteAuditSection(
        TextWriter writer,
        string header,
        IEnumerable<AssetResolution> entries,
        bool includeResolved = false,
        bool includeError = false)
    {
        var sorted = entries
            .OrderBy(r => r.RequestedPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        writer.WriteLine(header);
        writer.WriteLine($"# Count: {sorted.Count}");
        writer.WriteLine();

        foreach (var entry in sorted)
        {
            if (includeResolved && !string.IsNullOrEmpty(entry.ResolvedPath))
            {
                writer.WriteLine($"{entry.RequestedPath}  →  {entry.ResolvedPath}");
            }
            else if (includeError && !string.IsNullOrEmpty(entry.ConversionError))
            {
                writer.WriteLine($"{entry.RequestedPath}  ({entry.ConversionError})");
            }
            else
            {
                writer.WriteLine(entry.RequestedPath);
            }
        }

        writer.WriteLine();
    }

    /// <summary>Mutable counters used while iterating; copied into the immutable Stats record at the end.</summary>
    private sealed class RunningStats
    {
        public int Total;
        public int AlreadyInBaseline;
        public int ResolvedExact;
        public int ResolvedFuzzy;
        public int Converted360;
        public int ConversionFailed;
        public int Missing;
    }
}
