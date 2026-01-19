using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Xbox360MemoryCarver.Core.Converters;

/// <summary>
///     Result from batch DDX to DDS conversion.
/// </summary>
public sealed class BatchConversionResult
{
    public int TotalFiles { get; set; }
    public int Converted { get; set; }
    public int Failed { get; set; }
    public int Unsupported { get; set; }
    public int ExitCode { get; set; }
    public bool WasCancelled { get; set; }
    public List<string> Errors { get; } = [];
}

/// <summary>
///     Converts DDX files by invoking DDXConv as a subprocess.
/// </summary>
public partial class DdxSubprocessConverter
{
    /// <summary>
    ///     Callback for batch conversion progress updates.
    /// </summary>
    /// <param name="inputPath">The input file path that was converted.</param>
    /// <param name="status">Status: OK, FAIL, or UNSUPPORTED.</param>
    /// <param name="error">Error message if conversion failed.</param>
    public delegate void BatchProgressCallback(string inputPath, string status, string? error);

    private const string DdxConvExeName = "DDXConv.exe";
    private const string DdxConvFolderName = "DDXConv";
    private const string TargetFramework = "net10.0";
    private readonly bool _saveAtlas;

    private readonly bool _verbose;

    public DdxSubprocessConverter(bool verbose = false, string? ddxConvPath = null, bool saveAtlas = false)
    {
        _verbose = verbose;
        _saveAtlas = saveAtlas;
        DdxConvPath = ddxConvPath ?? FindDdxConvPath();

        if (string.IsNullOrEmpty(DdxConvPath) || !File.Exists(DdxConvPath))
        {
            throw new FileNotFoundException($"{DdxConvExeName} not found.", DdxConvExeName);
        }
    }

    public int Processed { get; private set; }
    public int Succeeded { get; private set; }
    public int Failed { get; private set; }
    public string DdxConvPath { get; }

    private static string FindDdxConvPath()
    {
        var envPath = Environment.GetEnvironmentVariable("DDXCONV_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
        {
            return envPath;
        }

        var assemblyDir = AppContext.BaseDirectory;
        var workspaceRoot = FindWorkspaceRoot(assemblyDir);

        foreach (var path in BuildCandidatePaths(assemblyDir, workspaceRoot))
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return string.Empty;
    }

    private static List<string> BuildCandidatePaths(string assemblyDir, string? workspaceRoot)
    {
        var candidates = new List<string>
        {
            Path.Combine(assemblyDir, DdxConvExeName),
            Path.Combine(assemblyDir, "..", DdxConvFolderName, DdxConvExeName)
        };

        if (!string.IsNullOrEmpty(workspaceRoot))
        {
            candidates.Add(Path.Combine(workspaceRoot, "src", DdxConvFolderName, DdxConvFolderName, "bin", "Release",
                TargetFramework, DdxConvExeName));
            candidates.Add(Path.Combine(workspaceRoot, "src", DdxConvFolderName, DdxConvFolderName, "bin", "Debug",
                TargetFramework, DdxConvExeName));
        }

        candidates.Add(Path.Combine(assemblyDir, "..", "..", "..", "..", "..", DdxConvFolderName, DdxConvFolderName,
            "bin", "Release", TargetFramework, DdxConvExeName));
        candidates.Add(Path.Combine(assemblyDir, "..", "..", "..", "..", "..", DdxConvFolderName, DdxConvFolderName,
            "bin", "Debug", TargetFramework, DdxConvExeName));
        return candidates;
    }

    private static string? FindWorkspaceRoot(string startDir)
    {
        var dir = startDir;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.GetFiles(dir, "*.slnx").Length > 0 || Directory.GetFiles(dir, "*.sln").Length > 0)
            {
                return dir;
            }

            var parent = Directory.GetParent(dir);
            if (parent == null)
            {
                break;
            }

            dir = parent.FullName;
        }

        return null;
    }

    public static bool IsAvailable()
    {
        try
        {
            _ = new DdxSubprocessConverter();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool ConvertFile(string inputPath, string outputPath)
    {
        Processed++;
        try
        {
            var args = BuildConversionArguments(inputPath, outputPath);
            using var process = StartDdxConvProcess(args);
            if (process == null)
            {
                Failed++;
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (_verbose && !string.IsNullOrEmpty(stdout))
            {
                Console.WriteLine(stdout);
            }

            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                Failed++;
                return false;
            }

            Succeeded++;
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DdxConverter] Exception converting {inputPath}: {ex.GetType().Name}: {ex.Message}");
            Failed++;
            return false;
        }
    }

    private string BuildConversionArguments(string inputPath, string outputPath)
    {
        var args = $"\"{inputPath}\" \"{outputPath}\"";
        if (_verbose)
        {
            args += " --verbose";
        }

        if (_saveAtlas)
        {
            args += " --atlas";
        }

        return args;
    }

    private Process? StartDdxConvProcess(string args)
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = DdxConvPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
    }

    /// <summary>
    ///     Converts all DDX files in the input directory to DDS using DDXConv's batch mode.
    ///     This spawns a single DDXConv process for the entire directory, which is much faster
    ///     than spawning individual processes for each file.
    /// </summary>
    /// <param name="inputDir">Input directory containing DDX files.</param>
    /// <param name="outputDir">Output directory for DDS files.</param>
    /// <param name="progressCallback">Callback invoked for each file as it completes.</param>
    /// <param name="cancellationToken">Cancellation token to stop the conversion.</param>
    /// <returns>Batch conversion result with statistics.</returns>
    public async Task<BatchConversionResult> ConvertBatchAsync(
        string inputDir,
        string outputDir,
        BatchProgressCallback? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        var result = new BatchConversionResult();

        // Build arguments for batch mode with --progress flag for machine-parseable output
        var args = $"\"{inputDir}\" \"{outputDir}\" --progress";

        var psi = new ProcessStartInfo
        {
            FileName = DdxConvPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process();
        process.StartInfo = psi;

        // Regex patterns for parsing [PROGRESS] lines
        var progressRegex = ProgressLineRegex();
        var doneRegex = DoneLineRegex();

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data))
            {
                return;
            }

            // Parse "[PROGRESS] STATUS path [error]" lines
            var progressMatch = progressRegex.Match(e.Data);
            if (progressMatch.Success)
            {
                var status = progressMatch.Groups[1].Value;
                var inputPath = progressMatch.Groups[2].Value;
                var error = progressMatch.Groups[3].Success ? progressMatch.Groups[3].Value : null;

                switch (status)
                {
                    case "OK":
                        result.Converted++;
                        break;
                    case "FAIL":
                        result.Failed++;
                        break;
                    case "UNSUPPORTED":
                        result.Unsupported++;
                        break;
                }

                progressCallback?.Invoke(inputPath, status, error);
                return;
            }

            // Parse "[PROGRESS] DONE converted failed unsupported" line
            var doneMatch = doneRegex.Match(e.Data);
            if (doneMatch.Success)
                // Final stats from DDXConv (we track our own, but can verify)
            {
                return;
            }

            // Parse "[PROGRESS] START count" line
            if (e.Data.StartsWith("[PROGRESS] START ", StringComparison.Ordinal)
                && int.TryParse(e.Data.AsSpan(17), out var total))
            {
                result.TotalFiles = total;
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                result.Errors.Add(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            // Wait for process to exit, with cancellation support
            await process.WaitForExitAsync(cancellationToken);
            result.ExitCode = process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            // Kill the process if cancelled
            try
            {
                process.Kill(true);
            }
            catch
            {
                // Best effort
            }

            result.WasCancelled = true;
            throw;
        }

        return result;
    }

    [GeneratedRegex(@"^\[PROGRESS\] (OK|FAIL|UNSUPPORTED) ""([^""]+)""(?: (.+))?$", RegexOptions.Compiled)]
    private static partial Regex ProgressLineRegex();

    [GeneratedRegex(@"^\[PROGRESS\] DONE (\d+) (\d+) (\d+)$", RegexOptions.Compiled)]
    private static partial Regex DoneLineRegex();

    public ConversionResult ConvertFromMemoryWithResult(byte[] ddxData)
    {
        Processed++;
        string? tempInputPath = null, tempOutputPath = null;

        try
        {
            var tempPath = Path.GetTempPath();
            tempInputPath = Path.Combine(tempPath, $"ddx_{Guid.NewGuid():N}.ddx");
            tempOutputPath = Path.Combine(tempPath, $"dds_{Guid.NewGuid():N}.dds");

            File.WriteAllBytes(tempInputPath, ddxData);
            var args = $"\"{tempInputPath}\" \"{tempOutputPath}\" --verbose";
            if (_saveAtlas)
            {
                args += " --atlas";
            }

            using var process = StartDdxConvProcess(args);
            if (process == null)
            {
                Failed++;
                return ConversionResult.Failure("Failed to start DDXConv");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            var consoleOutput = stdout + (string.IsNullOrEmpty(stderr) ? "" : $"\nSTDERR: {stderr}");
            if (_verbose && !string.IsNullOrWhiteSpace(stdout))
            {
                Console.WriteLine(stdout.TrimEnd());
            }

            var (isPartial, notes) = AnalyzeOutput(consoleOutput);

            if (!File.Exists(tempOutputPath))
            {
                Failed++;
                return new ConversionResult
                {
                    Success = false,
                    IsPartial = isPartial,
                    Notes = notes ?? $"Exit code {process.ExitCode}",
                    ConsoleOutput = consoleOutput
                };
            }

            var ddsData = File.ReadAllBytes(tempOutputPath);
            var atlasPath = tempOutputPath.Replace(".dds", "_full_atlas.dds");
            var atlasData = _saveAtlas && File.Exists(atlasPath) ? File.ReadAllBytes(atlasPath) : null;

            Succeeded++;
            return new ConversionResult
            {
                Success = true,
                OutputData = ddsData,
                AtlasData = atlasData,
                IsPartial = isPartial,
                Notes = notes,
                ConsoleOutput = _verbose ? consoleOutput : null
            };
        }
        catch (Exception ex)
        {
            Failed++;
            return ConversionResult.Failure($"Exception: {ex.Message}");
        }
        finally
        {
            CleanupTempFiles(tempInputPath, tempOutputPath);
        }
    }

    private static (bool isPartial, string? notes) AnalyzeOutput(string output)
    {
        var isPartial = output.Contains("atlas-only", StringComparison.OrdinalIgnoreCase) ||
                        output.Contains("partial", StringComparison.OrdinalIgnoreCase);
        string? notes = null;
        if (output.Contains("truncated", StringComparison.OrdinalIgnoreCase))
        {
            isPartial = true;
            notes = "truncated data";
        }

        return (isPartial, notes);
    }

    private static void CleanupTempFiles(string? input, string? output)
    {
        try
        {
            if (input != null && File.Exists(input))
            {
                File.Delete(input);
            }

            if (output != null && File.Exists(output))
            {
                File.Delete(output);
            }

            if (output != null)
            {
                var atlas = output.Replace(".dds", "_full_atlas.dds");
                if (File.Exists(atlas))
                {
                    File.Delete(atlas);
                }
            }
        }
        catch
        {
            /* Best-effort cleanup */
        }
    }

    public Task<ConversionResult> ConvertFromMemoryWithResultAsync(byte[] ddxData)
    {
        return Task.Run(() => ConvertFromMemoryWithResult(ddxData));
    }
}
