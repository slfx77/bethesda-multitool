// Copyright (c) 2026 FalloutXbox360Utils Contributors
// Licensed under the MIT License.

#if WINDOWS_GUI
using System.Threading.Channels;
using FalloutXbox360Utils.Core.Formats;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Ddx;

namespace FalloutXbox360Utils;

/// <summary>
///     Pure extraction/conversion logic for BSA archives, separated from UI concerns.
///     Reports progress via callbacks so the caller can marshal to the UI thread.
/// </summary>
internal sealed class BsaExtractionEngine
{
    /// <summary>
    ///     Tracks the result counters for an extraction run.
    /// </summary>
    internal sealed class ExtractionCounters
    {
        private int _succeeded;
        private int _failed;
        private int _converted;
        private long _totalSize;
        private int _ddxBatchConverted;

        public int Succeeded => _succeeded;
        public int Failed => _failed;
        public int Converted => _converted;
        public long TotalSize => _totalSize;
        public int DdxBatchConverted => _ddxBatchConverted;

        public void IncrementSucceeded() => Interlocked.Increment(ref _succeeded);
        public void IncrementFailed() => Interlocked.Increment(ref _failed);
        public void IncrementConverted() => Interlocked.Increment(ref _converted);
        public void IncrementDdxBatchConverted() => Interlocked.Increment(ref _ddxBatchConverted);
        public void AddSize(long size) => Interlocked.Add(ref _totalSize, size);

        /// <summary>
        ///     Returns the total number of files converted (per-file + DDX batch).
        /// </summary>
        public int TotalConverted => Converted + DdxBatchConverted;

        /// <summary>
        ///     Returns the combined succeeded count including conversions.
        /// </summary>
        public int TotalSucceeded => Succeeded + Converted + DdxBatchConverted;
    }

    /// <summary>
    ///     Options controlling an extraction run.
    /// </summary>
    internal sealed class ExtractionOptions
    {
        public required string OutputDir { get; init; }
        public required bool ConvertFiles { get; init; }
        public required bool DdxConversionAvailable { get; init; }
        public required bool XmaConversionAvailable { get; init; }
        public required bool NifConversionAvailable { get; init; }
    }

    /// <summary>
    ///     Callbacks for progress reporting from the engine to the UI layer.
    /// </summary>
    internal sealed class ProgressCallbacks
    {
        /// <summary>Called when a file's extraction status changes (entry, newStatus).</summary>
        public required Action<BsaFileEntry, BsaExtractionStatus> OnStatusChanged { get; init; }

        /// <summary>Called when extraction progress updates (current, total, fileName).</summary>
        public required Action<int, int, string> OnProgress { get; init; }

        /// <summary>Called with a general status message (e.g., "Converting DDX batch...").</summary>
        public required Action<string> OnStatusMessage { get; init; }

        /// <summary>
        ///     Called after per-file extraction progress to update status for non-conversion files.
        ///     Parameters: (entry, succeeded, statusMessage, isPendingConversion).
        /// </summary>
        public required Action<BsaFileEntry, bool, string, bool> OnFileComplete { get; init; }
    }

    /// <summary>
    ///     Determines whether a file extension needs conversion via the per-file channel (XMA or NIF).
    /// </summary>
    internal static bool NeedsChannelConversion(string extension, ExtractionOptions options)
    {
        return NeedsXmaConversion(extension, options) || NeedsNifConversion(extension, options);
    }

    /// <summary>
    ///     Determines whether a file extension needs DDX batch conversion.
    /// </summary>
    internal static bool NeedsDdxConversion(string extension, ExtractionOptions options)
    {
        return options.ConvertFiles && options.DdxConversionAvailable && extension == ".ddx";
    }

    /// <summary>
    ///     Determines whether a file extension needs XMA conversion.
    /// </summary>
    internal static bool NeedsXmaConversion(string extension, ExtractionOptions options)
    {
        return options.ConvertFiles && options.XmaConversionAvailable && extension == ".xma";
    }

    /// <summary>
    ///     Determines whether a file extension needs NIF conversion.
    /// </summary>
    internal static bool NeedsNifConversion(string extension, ExtractionOptions options)
    {
        return options.ConvertFiles && options.NifConversionAvailable && extension == ".nif";
    }

    /// <summary>
    ///     Determines whether a file is pending any kind of conversion (DDX, XMA, or NIF).
    /// </summary>
    internal static bool IsPendingConversion(string extension, ExtractionOptions options)
    {
        return NeedsDdxConversion(extension, options) ||
               NeedsXmaConversion(extension, options) ||
               NeedsNifConversion(extension, options);
    }

    /// <summary>
    ///     Computes the output path for a file, accounting for extension changes during conversion.
    /// </summary>
    internal static string ComputeOutputPath(string outputDir, string fullPath, string extension, ExtractionOptions options)
    {
        var outputPath = Path.Combine(outputDir, fullPath);

        if (NeedsXmaConversion(extension, options))
        {
            outputPath = Path.ChangeExtension(outputPath, ".wav");
        }
        // NIF and DDX keep same extension during extraction

        return outputPath;
    }

    /// <summary>
    ///     Extracts a single file from the BSA archive.
    ///     Handles DDX (write to disk for batch), channel conversion (XMA/NIF), and direct writes.
    /// </summary>
    internal static async Task ExtractSingleFileAsync(
        BsaFileEntry entry,
        BsaExtractor extractor,
        ExtractionOptions options,
        ExtractionCounters counters,
        ChannelWriter<(BsaFileEntry entry, byte[] data, string outputPath, string conversionType)> conversionWriter,
        Dictionary<string, BsaFileEntry> ddxEntries,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(entry.FileName).ToLowerInvariant();
        var needsDdxConvert = NeedsDdxConversion(extension, options);
        var needsXmaConvert = NeedsXmaConversion(extension, options);
        var needsNifConvert = NeedsNifConversion(extension, options);
        var needsChannelConvert = needsXmaConvert || needsNifConvert;
        var outputPath = ComputeOutputPath(options.OutputDir, entry.FullPath, extension, options);

        // Extract to memory
        var data = extractor.ExtractFile(entry.Record);

        if (needsDdxConvert)
        {
            // Write raw DDX to disk -- batch conversion happens after extraction
            var dir = Path.GetDirectoryName(outputPath)!;
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(outputPath, data, cancellationToken);

            lock (ddxEntries)
            {
                ddxEntries[entry.FullPath] = entry;
            }

            counters.AddSize(data.Length);
            // Status will be updated during batch conversion
        }
        else if (needsChannelConvert)
        {
            // Queue XMA/NIF for per-file conversion
            var conversionType = needsXmaConvert ? "xma" : "nif";
            await conversionWriter.WriteAsync((entry, data, outputPath, conversionType), cancellationToken);
        }
        else
        {
            // Write directly
            var dir = Path.GetDirectoryName(outputPath)!;
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(outputPath, data, cancellationToken);

            counters.AddSize(data.Length);
            counters.IncrementSucceeded();
        }
    }

    /// <summary>
    ///     Runs the parallel extraction of all selected entries.
    ///     Returns the DDX entries dictionary for subsequent batch conversion.
    /// </summary>
    internal static async Task<Dictionary<string, BsaFileEntry>> RunExtractionAsync(
        IReadOnlyList<BsaFileEntry> selectedEntries,
        BsaExtractor extractor,
        ExtractionOptions options,
        ExtractionCounters counters,
        ProgressCallbacks callbacks,
        CancellationToken cancellationToken)
    {
        // Create a channel for files that need per-file conversion (XMA, NIF only)
        // DDX uses batch conversion after extraction for much better performance
        var conversionChannel =
            Channel.CreateBounded<(BsaFileEntry entry, byte[] data, string outputPath, string conversionType)>(
                new BoundedChannelOptions(10) { FullMode = BoundedChannelFullMode.Wait });

        var total = selectedEntries.Count;
        var processed = 0;

        // Track DDX files for batch conversion after extraction
        var ddxEntries = new Dictionary<string, BsaFileEntry>(StringComparer.OrdinalIgnoreCase);

        // Start conversion workers for XMA/NIF (run concurrently with extraction)
        var conversionTask = Task.CompletedTask;
        if (options.ConvertFiles && (options.XmaConversionAvailable || options.NifConversionAvailable))
        {
            conversionTask = RunConversionWorkersAsync(
                conversionChannel.Reader,
                extractor,
                counters,
                callbacks,
                cancellationToken);
        }

        // Extract files - can run multiple extractions in parallel
        var extractionTasks = new List<Task>();
        var semaphore = new SemaphoreSlim(4); // Limit concurrent extractions

        foreach (var entry in selectedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await semaphore.WaitAsync(cancellationToken);

            var task = Task.Run(async () =>
            {
                var extractionSucceeded = false;
                var statusMessage = "Extracted";
                try
                {
                    callbacks.OnStatusChanged(entry, BsaExtractionStatus.Extracting);

                    await ExtractSingleFileAsync(
                        entry, extractor, options, counters,
                        conversionChannel.Writer, ddxEntries,
                        cancellationToken);

                    extractionSucceeded = true;
                }
                catch (Exception ex)
                {
                    counters.IncrementFailed();
                    statusMessage = ex.Message;
                    extractionSucceeded = false;
                }

                // Update progress
                var current = Interlocked.Increment(ref processed);
                var ext = Path.GetExtension(entry.FileName).ToLowerInvariant();
                var pendingConversion = IsPendingConversion(ext, options);

                callbacks.OnProgress(current, total, entry.FileName);
                callbacks.OnFileComplete(entry, extractionSucceeded, statusMessage, pendingConversion);

                semaphore.Release();
            }, cancellationToken);

            extractionTasks.Add(task);
        }

        // Wait for all extractions to complete
        await Task.WhenAll(extractionTasks);

        // Signal completion to XMA/NIF conversion workers
        conversionChannel.Writer.Complete();

        // Wait for XMA/NIF conversions to finish
        await conversionTask;

        return ddxEntries;
    }

    /// <summary>
    ///     Runs batch DDX conversion on previously extracted DDX files.
    /// </summary>
    internal static async Task RunDdxBatchConversionAsync(
        string outputDir,
        Dictionary<string, BsaFileEntry> ddxEntries,
        ExtractionCounters counters,
        ProgressCallbacks callbacks,
        CancellationToken cancellationToken)
    {
        if (ddxEntries.Count == 0)
        {
            return;
        }

        var ddxOutputDir = Path.Combine(Path.GetTempPath(), $"ddx_batch_{Guid.NewGuid():N}");
        try
        {
            // Set all DDX entries to "Converting" status
            callbacks.OnStatusMessage($"Converting {ddxEntries.Count:N0} DDX textures (batch)...");
            foreach (var ddxEntry in ddxEntries.Values)
            {
                callbacks.OnStatusChanged(ddxEntry, BsaExtractionStatus.Converting);
            }

            var converter = new DdxConverter();
            await converter.ConvertBatchAsync(
                outputDir, ddxOutputDir,
                (inputPath, status, error) =>
                {
                    // Map batch callback to entry via relative path
                    var relativePath = Path.GetRelativePath(outputDir, inputPath);
                    BsaFileEntry? ddxEntry;
                    lock (ddxEntries)
                    {
                        ddxEntries.TryGetValue(relativePath, out ddxEntry);
                    }

                    if (ddxEntry != null)
                    {
                        var isSuccess = status == "OK";
                        if (isSuccess)
                        {
                            counters.IncrementDdxBatchConverted();
                        }

                        callbacks.OnStatusChanged(
                            ddxEntry,
                            isSuccess ? BsaExtractionStatus.Done : BsaExtractionStatus.Failed);
                        ddxEntry.StatusMessage = isSuccess
                            ? "Converted"
                            : $"DDX failed ({error ?? status})";
                    }
                },
                cancellationToken, pcFriendly: true);

            // Merge converted DDS files back into output dir, delete raw DDX
            DdxBatchHelper.MergeConversions(outputDir, ddxOutputDir);
        }
        catch (FileNotFoundException)
        {
            // DDXConv not available (shouldn't happen since we checked)
        }
        finally
        {
            if (Directory.Exists(ddxOutputDir))
            {
                Directory.Delete(ddxOutputDir, true);
            }
        }
    }

    /// <summary>
    ///     Runs conversion workers that process XMA and NIF files from the channel.
    ///     DDX files use batch conversion separately for better performance.
    /// </summary>
    internal static async Task RunConversionWorkersAsync(
        ChannelReader<(BsaFileEntry entry, byte[] data, string outputPath, string conversionType)> reader,
        BsaExtractor extractor,
        ExtractionCounters counters,
        ProgressCallbacks callbacks,
        CancellationToken cancellationToken)
    {
        // Run multiple conversion workers in parallel
        const int workerCount = 2;
        var workers = new Task[workerCount];

        for (var i = 0; i < workerCount; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                await foreach (var (entry, data, outputPath, conversionType) in reader.ReadAllAsync(cancellationToken))
                {
                    var conversionSucceeded = false;
                    string statusMessage;

                    try
                    {
                        callbacks.OnStatusChanged(entry, BsaExtractionStatus.Converting);

                        ConversionResult result;
                        string originalExtension;

                        switch (conversionType)
                        {
                            case "xma":
                                result = await extractor.ConvertXmaAsync(data);
                                originalExtension = ".xma";
                                break;
                            case "nif":
                                result = await extractor.ConvertNifAsync(data);
                                originalExtension = ".nif";
                                break;
                            default:
                                result = new ConversionResult
                                {
                                    Success = false, Notes =
                                        $"Unknown conversion type: {conversionType}"
                                };
                                originalExtension = "";
                                break;
                        }

                        var dir = Path.GetDirectoryName(outputPath)!;
                        Directory.CreateDirectory(dir);

                        if (result.Success && result.OutputData != null)
                        {
                            await File.WriteAllBytesAsync(outputPath, result.OutputData, cancellationToken);
                            counters.IncrementConverted();
                            conversionSucceeded = true;
                            statusMessage = "Converted";
                        }
                        else
                        {
                            // Conversion failed - save original file
                            var fallbackPath = conversionType == "nif"
                                ? outputPath // NIF keeps same extension
                                : Path.ChangeExtension(outputPath, originalExtension);
                            await File.WriteAllBytesAsync(fallbackPath, data, cancellationToken);
                            conversionSucceeded = true; // File was saved, just not converted
                            statusMessage =
                                $"Saved as {originalExtension.ToUpperInvariant().TrimStart('.')} ({result.Notes})";
                        }
                    }
                    catch (Exception ex)
                    {
                        conversionSucceeded = false;
                        statusMessage = ex.Message;
                    }

                    // Update UI status
                    callbacks.OnStatusChanged(
                        entry,
                        conversionSucceeded ? BsaExtractionStatus.Done : BsaExtractionStatus.Failed);
                    entry.StatusMessage = statusMessage;
                }
            }, cancellationToken);
        }

        await Task.WhenAll(workers);
    }

    /// <summary>
    ///     Builds the extraction summary message.
    /// </summary>
    internal static string BuildSummaryMessage(ExtractionCounters counters)
    {
        var succeeded = counters.TotalSucceeded;
        var totalConverted = counters.TotalConverted;
        var converted = counters.Converted;
        var ddxBatchConverted = counters.DdxBatchConverted;
        var failed = counters.Failed;

        var message = $"Successfully extracted {succeeded:N0} files ({FormatSize(counters.TotalSize)})";
        if (totalConverted > 0)
        {
            message += $"\n{totalConverted:N0} files converted";
            if (ddxBatchConverted > 0)
            {
                message += $" ({ddxBatchConverted:N0} DDX batch";
                if (converted > 0)
                {
                    message += $", {converted:N0} XMA/NIF";
                }

                message += ")";
            }
            else
            {
                message += " (XMA->WAV, NIF endian swap)";
            }

            message += ".";
        }

        if (failed > 0)
        {
            message += $"\n{failed:N0} files failed.";
        }

        return message;
    }

    /// <summary>
    ///     Formats a byte count as a human-readable size string.
    /// </summary>
    internal static string FormatSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
            _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
        };
    }

    /// <summary>
    ///     Checks conversion tool availability and returns a list of unavailable tools.
    /// </summary>
    internal static List<string> CheckConversionAvailability(bool xmaConversionAvailable)
    {
        var unavailable = new List<string>();
        if (!xmaConversionAvailable) unavailable.Add("XMA->WAV (FFmpeg not found)");
        return unavailable;
    }
}

#endif
