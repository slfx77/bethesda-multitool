using System.Collections.Concurrent;
using System.Diagnostics;
using DDXConv;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Formats.Ddx;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Xma;
using BethesdaMultitool.Core.Orchestration;
using BethesdaMultitool.Core.Utils;

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

        // Phase 2a: Convert XMA files using batch mode (single FFmpeg script)
        if (xmaFiles.Count > 0 && XmaWavConverter.IsAvailable)
        {
            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Bsa,
                Message = $"{bsaName}: Converting {xmaFiles.Count} audio files..."
            });

            // Create temp directories for batch conversion
            var tempId = Guid.NewGuid().ToString("N")[..8];
            var tempInputDir = Path.Combine(Path.GetTempPath(), $"bsa_xma_in_{tempId}");
            var tempOutputDir = Path.Combine(Path.GetTempPath(), $"bsa_xma_out_{tempId}");
            Directory.CreateDirectory(tempInputDir);
            Directory.CreateDirectory(tempOutputDir);

            try
            {
                // Build mapping from temp file path to (index, original relative path)
                var fileMapping = new Dictionary<string, (int Index, string RelativePath)>();

                // Write all XMA files to temp input directory
                foreach (var (index, relativePath, xmaData) in xmaFiles)
                {
                    // Use a flat structure with unique names
                    var tempFileName = $"{index:D6}_{Path.GetFileName(relativePath)}";
                    var tempInputPath = Path.Combine(tempInputDir, tempFileName);
                    await File.WriteAllBytesAsync(tempInputPath, xmaData, cancellationToken);
                    fileMapping[tempInputPath] = (index, relativePath);
                }

                // Run batch FFmpeg conversion using PowerShell
                var ffmpegPath = FfmpegLocator.FfmpegPath!;
                var convertedCount = 0;

                // Process files using parallel FFmpeg invocations via shell
                // This is faster than .NET Process overhead per file
                var scriptPath = Path.Combine(tempInputDir, "convert.ps1");
                var script = $@"
$inputDir = '{tempInputDir.Replace("'", "''")}'
$outputDir = '{tempOutputDir.Replace("'", "''")}'
$ffmpeg = '{ffmpegPath.Replace("'", "''")}'
Get-ChildItem -Path $inputDir -Filter '*.xma' | ForEach-Object -Parallel {{
    $in = $_.FullName
    $out = Join-Path '{tempOutputDir.Replace("'", "''")}' ($_.BaseName + '.wav')
    & '{ffmpegPath.Replace("'", "''")}' -y -hide_banner -loglevel error -i $in -c:a pcm_s16le $out 2>$null
    Write-Output $_.Name
}} -ThrottleLimit {AudioEncodingThreads}
";
                await File.WriteAllTextAsync(scriptPath, script, cancellationToken);

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        var count = Interlocked.Increment(ref convertedCount);
                        if (count % 100 == 0 || count == xmaFiles.Count)
                        {
                            progress.Report(new RepackerProgress
                            {
                                Phase = RepackPhase.Bsa,
                                CurrentItem = e.Data,
                                Message = $"{bsaName}: Audio {count}/{xmaFiles.Count}"
                            });
                        }
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                await process.WaitForExitAsync(cancellationToken);

                // Read back converted files
                foreach (var (tempInputPath, (index, relativePath)) in fileMapping)
                {
                    var tempFileName = Path.GetFileNameWithoutExtension(Path.GetFileName(tempInputPath));
                    var tempOutputPath = Path.Combine(tempOutputDir, tempFileName + ".wav");

                    if (File.Exists(tempOutputPath))
                    {
                        var wavData = await File.ReadAllBytesAsync(tempOutputPath, cancellationToken);
                        if (wavData.Length > 0)
                        {
                            var newPath = Path.ChangeExtension(relativePath, ".wav");
                            allFiles[index] = (newPath, wavData);
                            continue;
                        }
                    }

                    // Keep original if conversion failed
                    var original = xmaFiles.First(f => f.Index == index);
                    allFiles[index] = (relativePath, original.Data);
                }
            }
            finally
            {
                // Clean up temp directories
                try
                {
                    if (Directory.Exists(tempInputDir))
                        Directory.Delete(tempInputDir, true);
                    if (Directory.Exists(tempOutputDir))
                        Directory.Delete(tempOutputDir, true);
                }
                catch
                {
                    /* Best effort cleanup */
                }
            }
        }
        else if (xmaFiles.Count > 0)
        {
            // FFmpeg not available, keep originals
            foreach (var (index, relativePath, data) in xmaFiles)
            {
                allFiles[index] = (relativePath, data);
            }
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
            await ParallelWork.ForEachAsync("bsa-repack", nifFiles, ConcurrencyPolicy.CoresMinusOne, async (nifFile, _) =>
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

        // Phase 3: Write output BSA
        progress.Report(new RepackerProgress
        {
            Phase = RepackPhase.Bsa,
            Message = $"{bsaName}: Writing {allFiles.Count} files..."
        });

        using var writer = BsaWriter.CreateWithAutoFlags(allFiles.Select(f => f.RelativePath));

        var writeIndex = 0;
        foreach (var (relativePath, data) in allFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            writer.AddFile(relativePath, data);
            writeIndex++;

            // Report write progress every 1000 files
            if (writeIndex % 1000 == 0 || writeIndex == allFiles.Count)
            {
                progress.Report(new RepackerProgress
                {
                    Phase = RepackPhase.Bsa,
                    CurrentItem = $"{writeIndex}/{allFiles.Count}",
                    Message = $"{bsaName}: Writing {writeIndex}/{allFiles.Count}"
                });
            }
        }

        writer.Write(destBsaPath);

        progress.Report(new RepackerProgress
        {
            Phase = RepackPhase.Bsa,
            Message = $"{bsaName}: Done ({allFiles.Count} files)"
        });
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
