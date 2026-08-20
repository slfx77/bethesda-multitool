using System.Collections.Concurrent;
using BethesdaMultitool.Core.Formats;
using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Ddx;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Xma;
using BethesdaMultitool.Core.Orchestration;
using DDXConv;

namespace BethesdaMultitool.Core.Repack.Processors;

/// <summary>
///     Processor for BSA files - extracts, converts contents, and repacks.
/// </summary>
public sealed class BsaProcessor : IRepackProcessor
{
    /// <summary>
    ///     Number of audio encoding threads.
    ///     Dynamically calculated based on CPU cores (half of logical processors, min 2, max 8).
    /// </summary>
    private static readonly int AudioEncodingThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    public string Name => "BSA";

    public async Task<int> ProcessAsync(
        RepackerOptions options,
        IProgress<RepackerProgress> progress,
        CancellationToken cancellationToken)
    {
        var sourceDataDir = Path.Combine(options.SourceFolder, "Data");
        var outputDataDir = Path.Combine(options.OutputFolder, "Data");

        if (!Directory.Exists(sourceDataDir))
        {
            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Bsa,
                Message = "Data folder not found, skipping",
                IsComplete = true,
                Success = true
            });
            return 0;
        }

        // Find all BSA files
        var bsaFiles = Directory.GetFiles(sourceDataDir, "*.bsa", SearchOption.TopDirectoryOnly);

        if (bsaFiles.Length == 0)
        {
            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Bsa,
                Message = "No BSA files found",
                IsComplete = true,
                Success = true
            });
            return 0;
        }

        Directory.CreateDirectory(outputDataDir);

        var processed = 0;
        var failed = 0;

        foreach (var sourceBsa in bsaFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bsaName = Path.GetFileName(sourceBsa);

            // Skip if specific BSAs are selected and this one isn't in the list
            if (options.SelectedBsaFiles is { Count: > 0 } &&
                !options.SelectedBsaFiles.Contains(bsaName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var destBsa = Path.Combine(outputDataDir, bsaName);

            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Bsa,
                ItemsProcessed = processed,
                TotalItems = bsaFiles.Length,
                Message = $"Processing {bsaName}..."
            });

            try
            {
                // Run BSA processing on thread pool to keep UI responsive
                await Task.Run(
                    () => ProcessBsaAsync(sourceBsa, destBsa, progress, cancellationToken),
                    cancellationToken);
                processed++;
            }
            catch (Exception ex)
            {
                failed++;
                progress.Report(new RepackerProgress
                {
                    Phase = RepackPhase.Bsa,
                    Message = $"Failed: {bsaName}: {ex.Message}"
                });
            }
        }

        progress.Report(new RepackerProgress
        {
            Phase = RepackPhase.Bsa,
            ItemsProcessed = processed,
            TotalItems = bsaFiles.Length,
            Message = failed > 0
                ? $"Processed {processed} BSA files ({failed} failed)"
                : $"Processed {processed} BSA files",
            IsComplete = true,
            Success = failed == 0
        });

        return processed;
    }

    private static async Task ProcessBsaAsync(
        string sourceBsaPath,
        string destBsaPath,
        IProgress<RepackerProgress> progress,
        CancellationToken cancellationToken)
    {
        var bsaName = Path.GetFileName(sourceBsaPath);

        // Phase 1: Extract all files from BSA
        progress.Report(new RepackerProgress
        {
            Phase = RepackPhase.Bsa,
            Message = $"{bsaName}: Extracting..."
        });

        using var extractor = new BsaExtractor(sourceBsaPath);
        var archive = extractor.Archive;

        // Build list of all files with their data
        var allFiles = new List<(string RelativePath, byte[] Data)>();
        var xmaFiles = new List<(int Index, string RelativePath, byte[] Data)>();
        var ddxFiles = new List<(int Index, string RelativePath, byte[] Data)>();
        var nifFiles = new List<(int Index, string RelativePath, byte[] Data)>();

        var extractIndex = 0;
        var totalFiles = archive.TotalFiles;

        foreach (var folder in archive.Folders)
        {
            foreach (var file in folder.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = file.FullPath;
                var fileData = extractor.ExtractFile(file);

                var extension = Path.GetExtension(relativePath).ToLowerInvariant();
                if (extension == ".xma")
                {
                    // Queue XMA for parallel conversion
                    xmaFiles.Add((allFiles.Count, relativePath, fileData));
                    allFiles.Add((relativePath, [])); // Placeholder
                }
                else if (extension == ".ddx")
                {
                    // Queue DDX for parallel conversion
                    ddxFiles.Add((allFiles.Count, relativePath, fileData));
                    allFiles.Add((relativePath, [])); // Placeholder
                }
                else if (extension is ".nif" or ".kf" or ".psa")
                {
                    // Queue NIF/KF/PSA for parallel conversion (big-endian to little-endian)
                    // All three are Gamebryo format files
                    nifFiles.Add((allFiles.Count, relativePath, fileData));
                    allFiles.Add((relativePath, [])); // Placeholder
                }
                else
                {
                    allFiles.Add((relativePath, fileData));
                }

                extractIndex++;

                // Report extraction progress every 500 files
                if (extractIndex % 500 == 0 || extractIndex == totalFiles)
                {
                    progress.Report(new RepackerProgress
                    {
                        Phase = RepackPhase.Bsa,
                        CurrentItem = $"{extractIndex}/{totalFiles} files",
                        Message = $"{bsaName}: Extracting {extractIndex}/{totalFiles}"
                    });
                }
            }
        }

        // Phase 2a: Convert XMA audio in memory, per file, in parallel.
        //
        // This used to shell out to Windows PowerShell 5.1 with a `ForEach-Object -Parallel`
        // script — a PowerShell 7+ feature. The script failed instantly (AmbiguousParameterSet),
        // produced zero .wav files, and the read-back loop silently kept every original .xma.
        // That is exactly how the shipped Sound.bsa and Voices1-4.bsa ended up 100% XMA2 —
        // total in-game silence. FFmpeg absence is now a hard error instead of a silent keep.
        if (xmaFiles.Count > 0)
        {
            if (!XmaWavConverter.IsAvailable)
            {
                var error = $"FFmpeg is required to convert the {xmaFiles.Count} XMA audio files in {bsaName}. " +
                            "Install ffmpeg and make sure it is on PATH — repacking without it would ship a silent archive.";
                progress.Report(new RepackerProgress
                {
                    Phase = RepackPhase.Bsa,
                    Message = $"{bsaName}: FFmpeg not found",
                    Error = error
                });
                throw new InvalidOperationException(error);
            }

            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Bsa,
                Message = $"{bsaName}: Converting {xmaFiles.Count} audio files..."
            });

            var convertedCount = 0;
            var failedCount = 0;
            var convertedAudio = new ConcurrentDictionary<int, (string Path, byte[] Data)>();
            var looseSongs = new ConcurrentDictionary<int, (string Path, byte[] Data)>();

            await ParallelWork.ForEachAsync("bsa-xma-convert", xmaFiles,
                ConcurrencyPolicy.Fixed(AudioEncodingThreads), async (xmaFile, _) =>
                {
                    var (index, relativePath, xmaData) = xmaFile;

                    // Retail conventions: voice BSAs ship OGG Vorbis dialogue; sound fx are WAV
                    // (the engine always probes .wav first for one-shots). Radio songs are
                    // NEITHER: the ESM's MUSC records reference them as "songs\...\x.mp3", FNV
                    // cannot read MP3 out of a BSA, and retail ships them as loose files under
                    // Data\sound\songs\. They also weigh ~1.8 GB as PCM, which pushed Sound.bsa
                    // past the 4 GiB u32 offset limit. So: transcode to MP3, deliver loose,
                    // exclude from the archive.
                    var isVoice = relativePath.StartsWith(@"sound\voice\", StringComparison.OrdinalIgnoreCase);
                    var isSong = relativePath.StartsWith(@"sound\songs\", StringComparison.OrdinalIgnoreCase);

                    // Voice ships as Ogg, songs as MP3 (delivered loose — FNV cannot read MP3
                    // from a BSA), everything else as WAV.
                    static async Task<ConversionResult> ConvertAsync(
                        byte[] data, bool voice, bool song)
                    {
                        if (voice) return await XmaOggConverter.ConvertAsync(data);
                        if (song) return await XmaMp3Converter.ConvertAsync(data);
                        return await XmaWavConverter.ConvertAsync(data);
                    }

                    static string ExtensionFor(bool voice, bool song)
                    {
                        if (voice) return ".ogg";
                        return song ? ".mp3" : ".wav";
                    }

                    try
                    {
                        var result = await ConvertAsync(xmaData, isVoice, isSong);

                        if (result is { Success: true, OutputData.Length: > 0 })
                        {
                            var ext = ExtensionFor(isVoice, isSong);
                            var newPath = BuildConvertedAudioPath(relativePath, ext);
                            if (isSong)
                            {
                                looseSongs[index] = (newPath, result.OutputData);
                            }
                            else
                            {
                                convertedAudio[index] = (newPath, result.OutputData);
                            }
                        }
                        else
                        {
                            // Keep the original .xma entry: unreachable in-game but preserves data.
                            Interlocked.Increment(ref failedCount);
                            convertedAudio[index] = (relativePath, xmaData);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Interlocked.Increment(ref failedCount);
                        convertedAudio[index] = (relativePath, xmaData);
                    }

                    var count = Interlocked.Increment(ref convertedCount);
                    if (count % 100 == 0 || count == xmaFiles.Count)
                    {
                        progress.Report(new RepackerProgress
                        {
                            Phase = RepackPhase.Bsa,
                            CurrentItem = Path.GetFileName(relativePath),
                            Message = $"{bsaName}: Audio {count}/{xmaFiles.Count}"
                        });
                    }
                }, cancellationToken: cancellationToken);

            foreach (var kvp in convertedAudio)
            {
                allFiles[kvp.Key] = kvp.Value;
            }

            // Write loose songs to the output Data folder and mark their archive slots for
            // removal (empty path sentinel, filtered before the BSA write).
            var outputDataDir = Path.GetDirectoryName(destBsaPath)!;
            foreach (var (index, (relPath, data)) in looseSongs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var loosePath = Path.Combine(outputDataDir, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(loosePath)!);
                await File.WriteAllBytesAsync(loosePath, data, cancellationToken);
                allFiles[index] = (string.Empty, []);
            }

            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Bsa,
                Message = $"{bsaName}: Audio {xmaFiles.Count - failedCount - looseSongs.Count} converted in archive, " +
                          $"{looseSongs.Count} songs delivered loose (MP3)" +
                          (failedCount > 0 ? $", {failedCount} failed (kept as .xma)" : "")
            });
        }

        // Phase 2b: Convert DDX files in memory.
        //
        // This used to stage every DDX into a FLAT temp directory named "{index:D6}_{filename}"
        // and let ConvertBatchAsync(pcFriendly: true) do the specular merge on disk. That merge
        // pairs a normal map with its specular companion by filename ("x_n.dds" -> "x_s.dds"),
        // so the index prefix made the companion name unresolvable for EVERY texture: the pair
        // "001234_rock_n.dds" / "005678_rock_s.dds" never matched. The merge then fell back to a
        // flat gray-128 alpha, and since FNV reads the specular mask out of the normal map's
        // alpha channel, every converted surface rendered with a uniform 50% specular.
        //
        // Converting in memory keeps the archive path intact, so the companion is resolved
        // against the archive itself and it also drops two full read/write passes over ~22k files.
        if (ddxFiles.Count > 0)
        {
            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Bsa,
                Message = $"{bsaName}: Converting {ddxFiles.Count} texture files..."
            });

            // Archive-path -> raw DDX bytes, for resolving "_s" specular companions.
            var ddxByPath = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, relativePath, data) in ddxFiles)
            {
                ddxByPath[relativePath] = data;
            }

            var convertedCount = 0;
            var converted = new ConcurrentDictionary<int, (string Path, byte[] Data)>();

            ParallelWork.ForEach("bsa-ddx-convert", ddxFiles, ConcurrencyPolicy.FullCores, ddxFile =>
            {
                var (index, relativePath, ddxData) = ddxFile;
                // DdxParser holds per-conversion state, so give each work item its own.
                var converter = new DdxConverter();

                try
                {
                    var result = converter.ConvertFromMemoryWithResult(ddxData);
                    if (result.Success && result.OutputData is { } ddsData)
                    {
                        // FNV's runtime DDS loader cannot bind ATI2/BC5, so a 360 normal map has
                        // to be re-encoded to DXT5 with the specular mask packed into alpha.
                        if (NormalMapMerge.IsNormalMapPath(relativePath) && NormalMapMerge.IsAti2(ddsData))
                        {
                            ddsData = MergeSpecularIntoNormal(converter, ddsData, relativePath, ddxByPath);
                        }

                        converted[index] = (Path.ChangeExtension(relativePath, ".dds"), ddsData);
                    }
                    else
                    {
                        // Keep the original bytes if conversion produced nothing.
                        converted[index] = (relativePath, ddxData);
                    }
                }
                catch
                {
                    converted[index] = (relativePath, ddxData);
                }

                var count = Interlocked.Increment(ref convertedCount);
                if (count % 100 == 0 || count == ddxFiles.Count)
                {
                    progress.Report(new RepackerProgress
                    {
                        Phase = RepackPhase.Bsa,
                        CurrentItem = Path.GetFileName(relativePath),
                        Message = $"{bsaName}: Textures {count}/{ddxFiles.Count}"
                    });
                }
            }, cancellationToken: cancellationToken);

            foreach (var kvp in converted)
            {
                allFiles[kvp.Key] = kvp.Value;
            }
        }

        // Phase 2c: Convert NIF files in parallel (big-endian to little-endian)
        if (nifFiles.Count > 0)
        {
            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Bsa,
                Message = $"{bsaName}: Converting {nifFiles.Count} mesh files..."
            });

            var convertedCount = 0;
            var convertedFiles = new ConcurrentDictionary<int, (string Path, byte[] Data)>();

            // Use more threads for NIF since it's CPU-bound in-process conversion
            await ParallelWork.ForEachAsync("bsa-repack", nifFiles, ConcurrencyPolicy.CoresMinusOne,
                async (nifFile, _) =>
                {
                    var (index, relativePath, nifData) = nifFile;
                    var fileName = Path.GetFileName(relativePath);

                    try
                    {
                        // Parse NIF to check if it's big-endian
                        var nifInfo = NifParser.Parse(nifData);
                        if (nifInfo != null && nifInfo.IsBigEndian)
                        {
                            // Convert big-endian NIF to little-endian
                            var result = NifConverter.Convert(nifData);

                            if (result.Success && result.OutputData != null)
                            {
                                convertedFiles[index] = (relativePath, result.OutputData);
                            }
                            else
                            {
                                // Keep original if conversion fails
                                convertedFiles[index] = (relativePath, nifData);
                            }
                        }
                        else
                        {
                            // Already little-endian or couldn't parse, keep as-is
                            convertedFiles[index] = (relativePath, nifData);
                        }
                    }
                    catch
                    {
                        // Keep original on any error
                        convertedFiles[index] = (relativePath, nifData);
                    }

                    var count = Interlocked.Increment(ref convertedCount);

                    // Report progress every 100 files to avoid flooding
                    if (count % 100 == 0 || count == nifFiles.Count)
                    {
                        progress.Report(new RepackerProgress
                        {
                            Phase = RepackPhase.Bsa,
                            CurrentItem = fileName,
                            Message = $"{bsaName}: Meshes {count}/{nifFiles.Count}"
                        });
                    }

                    await Task.CompletedTask; // Satisfy async signature
                }, cancellationToken: cancellationToken);

            // Apply converted files
            foreach (var kvp in convertedFiles)
            {
                allFiles[kvp.Key] = kvp.Value;
            }
        }

        // Phase 3: Write output BSA. Empty-path entries are slots whose content was
        // delivered loose instead (radio songs) — they must not enter the archive.
        var archiveFiles = allFiles.Where(f => f.RelativePath.Length > 0).ToList();

        progress.Report(new RepackerProgress
        {
            Phase = RepackPhase.Bsa,
            Message = $"{bsaName}: Writing {archiveFiles.Count} files..."
        });

        using var writer = BsaWriter.CreateWithAutoFlags(archiveFiles.Select(f => f.RelativePath));

        var writeIndex = 0;
        foreach (var (relativePath, data) in archiveFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            writer.AddFile(relativePath, data);
            writeIndex++;

            // Report write progress every 1000 files
            if (writeIndex % 1000 == 0 || writeIndex == archiveFiles.Count)
            {
                progress.Report(new RepackerProgress
                {
                    Phase = RepackPhase.Bsa,
                    CurrentItem = $"{writeIndex}/{archiveFiles.Count}",
                    Message = $"{bsaName}: Writing {writeIndex}/{archiveFiles.Count}"
                });
            }
        }

        writer.Write(destBsaPath);

        progress.Report(new RepackerProgress
        {
            Phase = RepackPhase.Bsa,
            Message = $"{bsaName}: Done ({archiveFiles.Count} files in archive)"
        });
    }

    /// <summary>
    ///     Builds the renamed archive path for a converted audio entry. Trims trailing whitespace
    ///     from the basename: ~25 source entries carry a trailing space before the extension
    ///     ("..._2 .xma"), and the engine could never request "..._2 .ogg".
    /// </summary>
    private static string BuildConvertedAudioPath(string relativePath, string newExtension)
    {
        var directory = Path.GetDirectoryName(relativePath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(relativePath).TrimEnd();
        var newName = baseName + newExtension;
        return directory.Length > 0 ? Path.Combine(directory, newName) : newName;
    }

    /// <summary>
    ///     Re-encodes a converted ATI2/BC5 normal map to the DXT5 form Fallout NV loads, packing
    ///     the companion <c>*_s.ddx</c> specular mask into the alpha channel. Falls back to the
    ///     merge's neutral gray alpha when the archive has no companion (~10% of 360 normal maps)
    ///     or when the companion itself fails to convert.
    /// </summary>
    private static byte[] MergeSpecularIntoNormal(
        DdxConverter converter,
        byte[] normalDds,
        string normalPath,
        Dictionary<string, byte[]> ddxByPath)
    {
        byte[]? specDds = null;

        var specPath = NormalMapMerge.ComputeSpecularPath(normalPath);
        if (specPath is not null && ddxByPath.TryGetValue(specPath, out var specRaw))
        {
            try
            {
                var specResult = converter.ConvertFromMemoryWithResult(specRaw);
                if (specResult.Success)
                {
                    specDds = specResult.OutputData;
                }
            }
            catch
            {
                // Leave specDds null — the merge substitutes neutral gray.
            }
        }

        return DdsPostProcessor.MergeNormalSpecularMapsFromMemory(normalDds, specDds);
    }
}
