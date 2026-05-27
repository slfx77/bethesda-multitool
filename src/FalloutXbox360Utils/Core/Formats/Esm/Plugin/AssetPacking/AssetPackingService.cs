using System.Collections.Concurrent;
using System.Diagnostics;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;
using FalloutXbox360Utils.Core.Semantic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;

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
            sink.Info("AssetPacking", $"Loading converted ESP: {Path.GetFileName(options.ConvertedEspPath)}");
            using var espResult = await SemanticFileLoader
                .LoadAsync(options.ConvertedEspPath, cancellationToken: cancellationToken)
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
                        Path.GetFileName(options.ConvertedEspPath),
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

                    var companionResolution = resolver.Resolve(normalized);
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

                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    CancellationToken = cancellationToken
                };
                await Parallel.ForEachAsync(requested, parallelOptions,
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
                    }).ConfigureAwait(false);

                sink.Info("AssetPacking",
                    $"Summary: already-in-baseline={stats.AlreadyInBaseline}, " +
                    $"resolved-exact={stats.ResolvedExact}, resolved-fuzzy={stats.ResolvedFuzzy}, " +
                    $"converted-360={stats.Converted360}, conversion-failed={stats.ConversionFailed}, " +
                    $"missing={stats.Missing}");

                // Snapshot ConcurrentBag → List once so all downstream consumers (BSA
                // writer, audit, result) see a stable enumeration order.
                var packedSnapshot = packedFiles.ToList();
                var resolutionsSnapshot = resolutions.ToList();
                ReportVoiceLipPairDiagnostics(
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
                DeleteStaleBsaOutputs(options.OutputBsaPath, outputPlans, sink);

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
                    TryWriteAuditFile(options.OutputBsaPath, resolutionsSnapshot, stats, sink);
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
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(nifPaths, parallelOptions, (nifPath, ct) =>
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
        }).ConfigureAwait(false);

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
            OutputPaths = [],
            Stats = stats,
            Resolutions = resolutions
        };
    }

    private static void ReportVoiceLipPairDiagnostics(
        List<(string Path, byte[] Data)> packedFiles,
        List<AssetResolution> resolutions,
        IReadOnlyDictionary<string, string>? packPathRenames,
        IConversionProgressSink sink)
    {
        var oggStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lipStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, _) in packedFiles)
        {
            if (!IsVoicePath(path))
            {
                continue;
            }

            var ext = Path.GetExtension(path);
            if (ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
            {
                oggStems.Add(PathStem(path));
            }
            else if (ext.Equals(".lip", StringComparison.OrdinalIgnoreCase))
            {
                lipStems.Add(PathStem(path));
            }
        }

        if (oggStems.Count == 0)
        {
            return;
        }

        var missingLipStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resolution in resolutions)
        {
            if (resolution.Kind != AssetResolutionKind.Missing
                || !resolution.RequestedPath.EndsWith(".lip", StringComparison.OrdinalIgnoreCase)
                || !IsVoicePath(resolution.RequestedPath))
            {
                continue;
            }

            var packPath = resolution.RequestedPath;
            if (packPathRenames is not null
                && packPathRenames.TryGetValue(resolution.RequestedPath, out var renamed))
            {
                packPath = renamed;
            }

            missingLipStems.Add(PathStem(packPath));
        }

        var unpaired = oggStems
            .Where(stem => !lipStems.Contains(stem))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unpaired.Count == 0)
        {
            sink.Info("AssetPacking",
                $"Voice lip pairing: {oggStems.Count:N0} packed OGG voice line(s), every line has a paired LIP.");
            return;
        }

        var missingSourceCount = unpaired.Count(stem => missingLipStems.Contains(stem));
        var samples = string.Join(", ", unpaired.Take(5));
        sink.Warn("AssetPacking",
            $"Voice lip pairing: {unpaired.Count:N0}/{oggStems.Count:N0} packed OGG voice line(s) " +
            $"have no paired LIP in the output ({missingSourceCount:N0} were unresolved source LIP requests). " +
            $"Samples: {samples}");
    }

    private static bool IsVoicePath(string path)
        => path.StartsWith("sound\\voice\\", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith("sound/voice/", StringComparison.OrdinalIgnoreCase);

    private static string PathStem(string path)
    {
        var normalized = path.Replace('/', '\\');
        var ext = Path.GetExtension(normalized);
        return (ext.Length == 0 ? normalized : normalized[..^ext.Length]).ToLowerInvariant();
    }

    internal static IReadOnlyList<BsaOutputPlan> PlanBsaOutputs(
        string outputBsaPath,
        IReadOnlyList<(string Path, byte[] Data)> packedFiles,
        long maxArchiveBytes = DefaultMaxBsaBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputBsaPath);

        if (packedFiles.Count == 0)
        {
            return [];
        }

        var bucketPlans = packedFiles
            .GroupBy(f => ClassifyAssetBucket(f.Path))
            .OrderBy(g => BucketSortOrder(g.Key))
            .SelectMany(g => ChunkBucket(g.Key, g, maxArchiveBytes))
            .ToList();

        var split = bucketPlans.Count > 1 || bucketPlans[0].ChunkIndex > 0;
        for (var i = 0; i < bucketPlans.Count; i++)
        {
            var plan = bucketPlans[i];
            var outputPath = split
                ? BuildSidecarPath(outputBsaPath, plan.Bucket, plan.ChunkIndex)
                : outputBsaPath;
            bucketPlans[i] = plan with { OutputPath = outputPath };
        }

        return bucketPlans;
    }

    private static IEnumerable<BsaOutputPlan> ChunkBucket(
        AssetPackBucket bucket,
        IEnumerable<(string Path, byte[] Data)> files,
        long maxArchiveBytes)
    {
        var ordered = files
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (EstimateBsaSize(ordered) <= maxArchiveBytes)
        {
            yield return new BsaOutputPlan(
                OutputPath: "",
                Bucket: bucket,
                BucketLabel: BucketLabel(bucket),
                ChunkIndex: 0,
                Files: ordered);
            yield break;
        }

        var chunk = new List<(string Path, byte[] Data)>();
        var chunkIndex = 0;
        var chunkEstimate = new BsaSizeEstimate();
        foreach (var file in ordered)
        {
            var candidateEstimate = chunkEstimate.EstimateWith(file);
            if (chunk.Count > 0 && candidateEstimate > maxArchiveBytes)
            {
                yield return new BsaOutputPlan(
                    OutputPath: "",
                    Bucket: bucket,
                    BucketLabel: BucketLabel(bucket),
                    ChunkIndex: chunkIndex++,
                    Files: chunk);
                chunk = [];
                chunkEstimate = new BsaSizeEstimate();
            }

            chunk.Add(file);
            chunkEstimate.Add(file);
        }

        if (chunk.Count > 0)
        {
            yield return new BsaOutputPlan(
                OutputPath: "",
                Bucket: bucket,
                BucketLabel: BucketLabel(bucket),
                ChunkIndex: chunkIndex,
                Files: chunk);
        }
    }

    private static long EstimateBsaSize(IReadOnlyList<(string Path, byte[] Data)> files)
    {
        var unique = new Dictionary<(string Folder, string Name), byte[]>(files.Count);
        foreach (var (path, data) in files)
        {
            var normalized = path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
            var slash = normalized.LastIndexOf('\\');
            var folder = slash >= 0 ? normalized[..slash] : "";
            var name = slash >= 0 ? normalized[(slash + 1)..] : normalized;
            unique.TryAdd((folder, name), data);
        }

        var folderCount = unique.Keys.Select(k => k.Folder).Distinct(StringComparer.Ordinal).Count();
        var fileCount = unique.Count;
        var totalFolderNameLength = unique.Keys
            .Select(k => k.Folder)
            .Distinct(StringComparer.Ordinal)
            .Sum(folder => folder.Length + 1);
        var totalFileNameLength = unique.Keys.Sum(k => k.Name.Length + 1);
        var dataLength = unique.Values.Sum(static data => (long)data.Length);

        return 36L
               + folderCount * 16L
               + folderCount
               + totalFolderNameLength
               + fileCount * 16L
               + totalFileNameLength
               + dataLength;
    }

    private sealed class BsaSizeEstimate
    {
        private readonly HashSet<(string Folder, string Name)> _files = [];
        private readonly HashSet<string> _folders = new(StringComparer.Ordinal);
        private long _total = 36;

        public long EstimateWith((string Path, byte[] Data) file)
        {
            var key = Normalize(file.Path);
            if (_files.Contains(key))
            {
                return _total;
            }

            var estimate = _total + 16 + key.Name.Length + 1 + file.Data.Length;
            if (!_folders.Contains(key.Folder))
            {
                estimate += 16 + 1 + key.Folder.Length + 1;
            }

            return estimate;
        }

        public void Add((string Path, byte[] Data) file)
        {
            var key = Normalize(file.Path);
            if (!_files.Add(key))
            {
                return;
            }

            _total += 16 + key.Name.Length + 1 + file.Data.Length;
            if (_folders.Add(key.Folder))
            {
                _total += 16 + 1 + key.Folder.Length + 1;
            }
        }

        private static (string Folder, string Name) Normalize(string path)
        {
            var normalized = path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
            var slash = normalized.LastIndexOf('\\');
            return slash >= 0
                ? (normalized[..slash], normalized[(slash + 1)..])
                : ("", normalized);
        }
    }

    private static AssetPackBucket ClassifyAssetBucket(string path)
    {
        var normalized = path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
        if (normalized.StartsWith("textures\\", StringComparison.Ordinal))
        {
            return AssetPackBucket.Textures;
        }

        if (normalized.StartsWith("sound\\voice\\", StringComparison.Ordinal))
        {
            return AssetPackBucket.Voices;
        }

        if (normalized.StartsWith("sound\\", StringComparison.Ordinal))
        {
            return AssetPackBucket.Sounds;
        }

        return AssetPackBucket.Main;
    }

    private static int BucketSortOrder(AssetPackBucket bucket) => bucket switch
    {
        AssetPackBucket.Main => 0,
        AssetPackBucket.Textures => 1,
        AssetPackBucket.Sounds => 2,
        AssetPackBucket.Voices => 3,
        _ => 99
    };

    private static string BucketLabel(AssetPackBucket bucket) => bucket switch
    {
        AssetPackBucket.Main => "main",
        AssetPackBucket.Textures => "texture",
        AssetPackBucket.Sounds => "sound",
        AssetPackBucket.Voices => "voice",
        _ => "misc"
    };

    private static string BucketSuffix(AssetPackBucket bucket) => bucket switch
    {
        AssetPackBucket.Main => "Main",
        AssetPackBucket.Textures => "Textures",
        AssetPackBucket.Sounds => "Sounds",
        AssetPackBucket.Voices => "Voices",
        _ => "Assets"
    };

    private static string BuildSidecarPath(string outputBsaPath, AssetPackBucket bucket, int chunkIndex)
    {
        var directory = Path.GetDirectoryName(outputBsaPath);
        var stem = Path.GetFileNameWithoutExtension(outputBsaPath);
        var ext = Path.GetExtension(outputBsaPath);
        if (string.IsNullOrEmpty(ext))
        {
            ext = ".bsa";
        }

        var suffix = BucketSuffix(bucket);
        if (chunkIndex > 0)
        {
            suffix += chunkIndex + 1;
        }

        return Path.Combine(directory ?? "", $"{stem} - {suffix}{ext}");
    }

    private static void DeleteStaleBsaOutputs(
        string outputBsaPath,
        IReadOnlyList<BsaOutputPlan> outputPlans,
        IConversionProgressSink sink)
    {
        var planned = new HashSet<string>(
            outputPlans.Select(p => Path.GetFullPath(p.OutputPath)),
            StringComparer.OrdinalIgnoreCase);

        DeleteIfStale(outputBsaPath);

        var directory = Path.GetDirectoryName(outputBsaPath);
        var stem = Path.GetFileNameWithoutExtension(outputBsaPath);
        var ext = Path.GetExtension(outputBsaPath);
        if (string.IsNullOrEmpty(ext))
        {
            ext = ".bsa";
        }

        if (string.IsNullOrEmpty(directory))
        {
            directory = ".";
        }

        if (Directory.Exists(directory))
        {
            foreach (var sidecar in Directory.EnumerateFiles(directory, $"{stem} - *{ext}"))
            {
                DeleteIfStale(sidecar);
            }
        }

        void DeleteIfStale(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (planned.Contains(fullPath) || !File.Exists(fullPath))
            {
                return;
            }

            try
            {
                File.Delete(fullPath);
                sink.Info("AssetPacking", $"Deleted stale BSA output: {Path.GetFileName(fullPath)}");
            }
            catch (Exception ex)
            {
                sink.Warn("AssetPacking",
                    $"Could not delete stale BSA output {fullPath}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static AssetPackingStats EmptyStats(TimeSpan elapsed)
    {
        return new AssetPackingStats { Elapsed = elapsed };
    }

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
                true);

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
        public int AlreadyInBaseline;
        public int ConversionFailed;
        public int Converted360;
        public int Missing;
        public int ResolvedExact;
        public int ResolvedFuzzy;
        public int Total;
    }
}

internal enum AssetPackBucket
{
    Main,
    Textures,
    Sounds,
    Voices
}

internal sealed record BsaOutputPlan(
    string OutputPath,
    AssetPackBucket Bucket,
    string BucketLabel,
    int ChunkIndex,
    IReadOnlyList<(string Path, byte[] Data)> Files);
