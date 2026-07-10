using System.Collections.Concurrent;
using System.Diagnostics;
using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using BethesdaMultitool.Core.Orchestration;
using BethesdaMultitool.Core.Semantic;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Orchestrates the prototype asset packer:
///     <list type="number">
///         <item>
///             <description>Loads the converted ESP into a <see cref="Models.RecordCollection" />.</description>
///         </item>
///         <item>
///             <description>Collects every referenced asset path from records + raw DMP bytes.</description>
///         </item>
///         <item>
///             <description>Builds a <see cref="DataFolderIndex" /> for the FNV baseline and each secondary folder.</description>
///         </item>
///         <item>
///             <description>Resolves each requested path through <see cref="DataFolderResolver" />.</description>
///         </item>
///         <item>
///             <description>Converts 360-format bytes to PC format on the fly via <see cref="PrototypeAssetConverter" />.</description>
///         </item>
///         <item>
///             <description>Writes the resolved set to a new BSA via <see cref="BsaWriter" />.</description>
///         </item>
///     </list>
/// </summary>
public sealed class AssetPackingService
{
    private const long DefaultMaxBsaBytes = 1_900_000_000L;

    /// <summary>Run an asset packing job end-to-end.</summary>
    public static async Task<AssetPackingResult> PackAsync(
        AssetPackingOptions options,
        IConversionProgressSink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sink);

        var stopwatch = Stopwatch.StartNew();
        var resolutions = new ConcurrentBag<AssetResolution>();

        sink.OnPhaseStart("AssetPacking", null);

        try
        {
            // 1) Parse the converted ESP back into a RecordCollection.
            sink.Info("AssetPacking", $"Loading converted ESM: {Path.GetFileName(options.ConvertedEsmPath)}");
            using var espResult = await SemanticFileLoader
                .LoadAsync(options.ConvertedEsmPath, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // 2) Collect every referenced asset path.
            var requested = AssetPathCollector.Collect(espResult.Records, options.DmpPath, sink);
            IReadOnlyDictionary<string, string>? packPathRenames = null;
            if (options.DialogueAudioCsvPaths.Count > 0)
            {
                var dialogueAudio = await DialogueAudioCsvAssetCollector
                    .CollectAsync(
                        espResult.Records,
                        options.DmpPath,
                        options.DialogueAudioCsvPaths,
                        sink,
                        cancellationToken,
                        options.NewRecordSourceToAllocatedFormIds.Count > 0
                            ? options.NewRecordSourceToAllocatedFormIds
                            : null,
                        Path.GetFileName(options.ConvertedEsmPath),
                        options.EmittedDialogueAudioBindings.Count > 0
                            ? options.EmittedDialogueAudioBindings
                            : null)
                    .ConfigureAwait(false);

                foreach (var path in dialogueAudio.Paths)
                {
                    requested.Add(path);
                }

                packPathRenames = dialogueAudio.PackPathRenames;
            }

            sink.Info("AssetPacking", $"Total unique asset paths to resolve: {requested.Count}");

            if (requested.Count == 0)
            {
                return BuildEmptyResult(options, sink, resolutions.ToList(), stopwatch, null);
            }

            // 3) Build baseline and secondary folder indexes.
            sink.Info("AssetPacking", $"Indexing baseline data folder: {options.BaselineDataFolder}");
            using var baseline = new DataFolderIndex(options.BaselineDataFolder, false);
            baseline.Build();
            sink.Info("AssetPacking", $"Baseline indexed {baseline.EntryCount} entries");
            WarnIfTruncated(sink, baseline, "Baseline");

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
                    WarnIfTruncated(sink, idx, Path.GetFileName(secondary.Path.TrimEnd('\\', '/')));
                    secondaryDisposables.Add(idx);
                }

                var resolver = new DataFolderResolver(
                    baseline, secondaryDisposables, options.OverrideVanillaBaseline);
                // Companion fetcher lets the converter pull the `_s.ddx` specular companion
                // for any `_n.ddx` normal map it's processing. The merge step packs the spec
                // map into the normal map's alpha channel so the runtime gets vanilla-shape
                // DXT5 normals instead of BC5/ATI2 (which FNV's loader rejects).
                var converter = new PrototypeAssetConverter(companionSourcePath =>
                {
                    var normalized = AssetPathRules.TryNormalizeRequestPath(companionSourcePath);
                    if (normalized is null)
                    {
                        return null;
                    }

                    // Exact-only: a specular companion must be the literal `_s` sibling.
                    // The full fuzzy resolver would otherwise match an unrelated sibling
                    // (the diffuse / normal) at a different resolution, breaking the merge.
                    var companionResolution = resolver.ResolveExactOnly(normalized);
                    if (companionResolution.Source is null)
                    {
                        return null;
                    }

                    try
                    {
                        return companionResolution.Source.Read();
                    }
                    catch
                    {
                        return null;
                    }
                });

                // 4a) NIF embedded-texture pre-pass. NIF blocks store texture paths as
                // SizedString (length-prefix + ASCII, no null terminator), so the DMP
                // raw-byte scanner can't find them — and the engine then renders missing
                // textures with whatever stale memory occupies the texture slot. Open every
                // .nif source we plan to pack, scan the bytes for embedded texture paths,
                // and feed them back into the request set so the resolver picks them up.
                await CollectNifEmbeddedTexturesAsync(
                    requested,
                    resolver,
                    sink,
                    cancellationToken).ConfigureAwait(false);

                // 4b) Resolve + convert + collect bytes to pack. Parallelized — XMA→OGG/
                // DDX→DDS conversion is CPU-heavy and dominates wall time on real
                // workloads (6000+ assets per BSA). DataFolderResolver.Resolve and
                // PrototypeAssetConverter.ConvertAsync are reentrant (read-only index
                // lookups + per-call temp state), so we fan out across all cores.
                var packedFiles = new ConcurrentBag<(string Path, byte[] Data)>();
                var stats = new RunningStats();

                await ParallelWork.ForEachAsync(
                    "asset-pack-resolve", requested, ConcurrencyPolicy.FullCores,
                    async (requestedPath, ct) =>
                    {
                        var resolution = resolver.Resolve(requestedPath);
                        Interlocked.Increment(ref stats.Total);

                        switch (resolution.Kind)
                        {
                            case AssetResolutionKind.AlreadyInBaseline:
                                Interlocked.Increment(ref stats.AlreadyInBaseline);
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
                                    packPathRenames,
                                    ct).ConfigureAwait(false);
                                break;

                            case AssetResolutionKind.Missing:
                                Interlocked.Increment(ref stats.Missing);
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
                    }, cancellationToken: cancellationToken).ConfigureAwait(false);

                sink.Info("AssetPacking",
                    $"Summary: already-in-baseline={stats.AlreadyInBaseline}, " +
                    $"resolved-exact={stats.ResolvedExact}, resolved-fuzzy={stats.ResolvedFuzzy}, " +
                    $"converted-360={stats.Converted360}, conversion-failed={stats.ConversionFailed}, " +
                    $"missing={stats.Missing}");

                // Snapshot ConcurrentBag → List once so all downstream consumers (BSA
                // writer, audit, result) see a stable enumeration order.
                var packedSnapshot = packedFiles.ToList();
                var resolutionsSnapshot = resolutions.ToList();
                AssetPackAuditWriter.ReportVoiceLipPairDiagnostics(
                    packedSnapshot,
                    resolutionsSnapshot,
                    packPathRenames,
                    sink);

                if (packedSnapshot.Count == 0)
                {
                    return BuildEmptyResult(options, sink, resolutionsSnapshot, stopwatch, stats);
                }

                // 5) Write output BSAs. FO3/FNV archives are safest when no file data
                // offset crosses the signed 2 GB boundary; the vanilla game also sorts
                // meshes/textures/sounds/voices into separate BSA files. Treat the user's
                // --pack-assets path as a base name when multiple asset classes exist.
                Directory.CreateDirectory(Path.GetDirectoryName(options.OutputBsaPath)!);

                var outputPlans = PlanBsaOutputs(options.OutputBsaPath, packedSnapshot);
                AssetPackBsaPlanner.DeleteStaleBsaOutputs(options.OutputBsaPath, outputPlans, sink);

                var outputPaths = new List<string>(outputPlans.Count);
                long totalOutputSize = 0;
                foreach (var plan in outputPlans)
                {
                    sink.Info("AssetPacking",
                        $"Writing {plan.Files.Count:N0} {plan.BucketLabel} asset(s) to " +
                        $"{Path.GetFileName(plan.OutputPath)}");

                    using var writer = BsaWriter.CreateWithAutoFlags(plan.Files.Select(p => p.Path));
                    foreach (var (path, data) in plan.Files)
                    {
                        writer.AddFile(path, data);
                    }

                    writer.Write(plan.OutputPath);

                    var size = new FileInfo(plan.OutputPath).Length;
                    totalOutputSize += size;
                    outputPaths.Add(plan.OutputPath);

                    if (size > int.MaxValue)
                    {
                        sink.Warn("AssetPacking",
                            $"{Path.GetFileName(plan.OutputPath)} is {size:N0} bytes, above the " +
                            "signed 2 GB boundary. Split rules should be tightened before in-game use.");
                    }
                }
                stopwatch.Stop();

                // Drop a per-asset audit next to the BSA so the user can review what
                // resolved, what fuzzy-matched, and what stayed missing. Sorted, deduped,
                // one path per line within each section. Opt-in via WriteAuditFile.
                if (options.WriteAuditFile)
                {
                    AssetPackAuditWriter.TryWriteAuditFile(options.OutputBsaPath, resolutionsSnapshot, stats, sink);
                }

                sink.OnPhaseEnd("AssetPacking", new ConversionPipelineStats());
                sink.Info("AssetPacking",
                    $"BSA output written: {outputPaths.Count:N0} archive(s), " +
                    $"{totalOutputSize:N0} total bytes in {stopwatch.Elapsed.TotalSeconds:F2}s");

                return new AssetPackingResult
                {
                    Success = true,
                    OutputPath = outputPaths.FirstOrDefault(),
                    OutputPaths = outputPaths,
                    Stats = new AssetPackingStats
                    {
                        TotalPathsScanned = stats.Total,
                        AlreadyInBaseline = stats.AlreadyInBaseline,
                        ResolvedExact = stats.ResolvedExact,
                        ResolvedFuzzy = stats.ResolvedFuzzy,
                        Converted360 = stats.Converted360,
                        ConversionFailed = stats.ConversionFailed,
                        Missing = stats.Missing,
                        PackedAssetCount = packedSnapshot.Count,
                        OutputBsaSizeBytes = totalOutputSize,
                        Elapsed = stopwatch.Elapsed
                    },
                    Resolutions = resolutionsSnapshot
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
                Resolutions = resolutions.ToList()
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
                Resolutions = resolutions.ToList()
            };
        }
    }

    /// <summary>
    ///     Pre-pass: for each .nif in the requested set, resolve it through the resolver,
    ///     read the bytes, and feed any embedded texture-path references back into the
    ///     requested set. The main pack loop then resolves and packs the newly-discovered
    ///     textures alongside the originally-requested ones.
    /// </summary>
    private static async Task CollectNifEmbeddedTexturesAsync(
        HashSet<string> requested,
        DataFolderResolver resolver,
        IConversionProgressSink sink,
        CancellationToken cancellationToken)
    {
        var nifPaths = requested
            .Where(static p => p.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (nifPaths.Count == 0)
        {
            return;
        }

        var discovered = new ConcurrentBag<string>();
        var scanned = 0;
        var scanFailures = 0;

        await ParallelWork.ForEachAsync(
            "asset-pack-nif-scan", nifPaths, ConcurrencyPolicy.FullCores,
            (nifPath, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            var resolution = resolver.Resolve(nifPath);
            if (resolution.Source is null)
            {
                // AlreadyInBaseline / Missing — either the engine finds it via vanilla
                // (and any textures the vanilla NIF references are also vanilla, so they
                // don't need packing) or there's nothing to scan. Either way: skip.
                return ValueTask.CompletedTask;
            }

            byte[] bytes;
            try
            {
                bytes = resolution.Source.Read();
            }
            catch
            {
                Interlocked.Increment(ref scanFailures);
                return ValueTask.CompletedTask;
            }

            foreach (var path in NifEmbeddedAssetCollector.ScanBytes(bytes))
            {
                discovered.Add(path);
            }

            Interlocked.Increment(ref scanned);
            return ValueTask.CompletedTask;
        }, cancellationToken: cancellationToken).ConfigureAwait(false);

        var added = 0;
        foreach (var path in discovered)
        {
            if (requested.Add(path))
            {
                added++;
            }
        }

        sink.Info("AssetPacking",
            $"NIF embedded-texture scan: read {scanned} NIFs, " +
            $"added {added} new texture paths" +
            (scanFailures > 0 ? $" ({scanFailures} read failures)" : string.Empty));
    }

    private static async Task TryPackAsync(
        PrototypeAssetConverter converter,
        string requestedPath,
        DataFolderResolution resolution,
        AssetPackingOptions options,
        ConcurrentBag<(string Path, byte[] Data)> packedFiles,
        RunningStats stats,
        ConcurrentBag<AssetResolution> resolutions,
        IConversionProgressSink sink,
        IReadOnlyDictionary<string, string>? packPathRenames,
        CancellationToken cancellationToken)
    {
        if (resolution.Source is null || resolution.ResolvedPath is null)
        {
            Interlocked.Increment(ref stats.Missing);
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
            Interlocked.Increment(ref stats.Missing);
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
            Interlocked.Increment(ref stats.Missing);
            resolutions.Add(new AssetResolution
            {
                RequestedPath = requestedPath,
                Kind = AssetResolutionKind.Missing
            });
            return;
        }

        // Defense in depth: a zero-filled payload (e.g. a record in a partial-recovery
        // BSA's zero tail that slipped past index-time truncation detection, or a mid-file
        // zero hole) must never be packed — it would ship garbage the runtime can't load.
        // Report it as Missing so the audit surfaces it honestly. Cheap: the bytes are
        // already in memory and the check short-circuits on the first non-zero byte.
        if (rawBytes.Length == 0 || BinaryUtils.IsAllZero(rawBytes))
        {
            Interlocked.Increment(ref stats.Missing);
            resolutions.Add(new AssetResolution
            {
                RequestedPath = requestedPath,
                Kind = AssetResolutionKind.Missing
            });
            if (options.VerbosePerAsset)
            {
                sink.Warn("AssetPacking",
                    $"Zero-filled source skipped: {resolution.ResolvedPath} (requested {requestedPath})");
            }

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
                Interlocked.Increment(ref stats.ConversionFailed);
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

        // Dialogue audio rewrite: when a CSV-derived voice path is for a remapped INFO,
        // the engine looks up the file under our ESP's directory using the allocated
        // FormID's bottom 24 bits in the filename. The collector built (resolveAs, packAs)
        // pairs; resolveAs is `requestedPath` (master shape) and packAs is the engine's
        // runtime shape. Apply the rename HERE so the resolver still found the master
        // bytes but the BSA entry lands at the engine path.
        if (packPathRenames is not null
            && packPathRenames.TryGetValue(requestedPath, out var renamedPackPath))
        {
            packedPath = renamedPackPath;
        }

        // If we converted an extension (.ddx → .dds, .xma → .wav), apply the same swap to
        // the requested path so the runtime finds the converted output. Otherwise the
        // record's reference would still point to the original extension.
        var originalExt = Path.GetExtension(resolution.Source.NormalizedPath);
        var convertedExt = Path.GetExtension(outputPath);
        if (!string.Equals(originalExt, convertedExt, StringComparison.OrdinalIgnoreCase))
        {
            packedPath = Path.ChangeExtension(packedPath, convertedExt);
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
                Interlocked.Increment(ref stats.ResolvedExact);
                break;
            case AssetResolutionKind.ResolvedFuzzy:
                Interlocked.Increment(ref stats.ResolvedFuzzy);
                break;
            case AssetResolutionKind.ResolvedExactConverted:
                Interlocked.Increment(ref stats.ResolvedExact);
                Interlocked.Increment(ref stats.Converted360);
                break;
            case AssetResolutionKind.ResolvedFuzzyConverted:
                Interlocked.Increment(ref stats.ResolvedFuzzy);
                Interlocked.Increment(ref stats.Converted360);
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
            AssetPackAuditWriter.TryWriteAuditFile(options.OutputBsaPath, resolutions, runningStats, sink);
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
            OutputPaths = [],
            Stats = stats,
            Resolutions = resolutions
        };
    }

    /// <summary>
    ///     Plans the output BSA archives for a set of packed files: classifies into asset
    ///     buckets, chunks oversized buckets, and assigns sidecar paths. Delegates to
    ///     <see cref="AssetPackBsaPlanner" />.
    /// </summary>
    internal static IReadOnlyList<BsaOutputPlan> PlanBsaOutputs(
        string outputBsaPath,
        IReadOnlyList<(string Path, byte[] Data)> packedFiles,
        long maxArchiveBytes = DefaultMaxBsaBytes)
        => AssetPackBsaPlanner.Plan(outputBsaPath, packedFiles, maxArchiveBytes);

    private static AssetPackingStats EmptyStats(TimeSpan elapsed)
    {
        return new AssetPackingStats { Elapsed = elapsed };
    }

    private static void WarnIfTruncated(IConversionProgressSink sink, DataFolderIndex index, string label)
    {
        if (index.TruncatedEntrySkipCount > 0)
        {
            sink.Warn("AssetPacking",
                $"  → {label}: skipped {index.TruncatedEntrySkipCount:N0} zero-filled entries " +
                "(truncated / partial-recovery BSA); resolution will fall through to other sources");
        }
    }

    /// <summary>Mutable counters used while iterating; copied into the immutable Stats record at the end.</summary>
    internal sealed class RunningStats
    {
        public int AlreadyInBaseline;
        public int ConversionFailed;
        public int Converted360;
        public int Missing;
        public int ResolvedExact;
        public int ResolvedFuzzy;
        public int Total;
    }
}
