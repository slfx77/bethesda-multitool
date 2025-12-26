using System.Diagnostics;

namespace Xbox360MemoryCarver.Core.Converters;

/// <summary>
///     Converts DDX files by invoking DDXConv as a subprocess.
///     This approach uses the 32-bit DDXConv.exe with XnaNative.dll
///     for reliable LZX decompression, which can't be loaded into a 64-bit process.
/// </summary>
#pragma warning disable IDE0032, RCS1085 // Use auto property - readonly field with complex initialization
public class DdxSubprocessConverter
{
    // Constants for repeated string literals (S1192)
    private const string DdxConvExeName = "DDXConv.exe";
    private const string DdxConvFolderName = "DDXConv";
    private const string TargetFramework = "net9.0";

    private readonly bool _verbose;
    private readonly bool _saveAtlas;

    public int Processed { get; private set; }
    public int Succeeded { get; private set; }
    public int Failed { get; private set; }

    public DdxSubprocessConverter(bool verbose = false, string? ddxConvPath = null, bool saveAtlas = false)
    {
        _verbose = verbose;
        _saveAtlas = saveAtlas;
        DdxConvPath = ddxConvPath ?? FindDdxConvPath();

        if (string.IsNullOrEmpty(DdxConvPath) || !File.Exists(DdxConvPath))
            throw new FileNotFoundException(
                $"{DdxConvExeName} not found. Please build {DdxConvFolderName} or specify its path.",
                DdxConvPath ?? DdxConvExeName);
    }
#pragma warning restore IDE0032, RCS1085

    /// <summary>
    ///     Get the path to the DDXConv executable being used.
    /// </summary>
    public string DdxConvPath { get; }

    /// <summary>
    ///     Try to find DDXConv.exe in common locations relative to this assembly.
    /// </summary>
    private static string FindDdxConvPath()
    {
        // Check environment variable first
        var envPath = Environment.GetEnvironmentVariable("DDXCONV_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath)) return envPath;

        var assemblyDir = AppContext.BaseDirectory;
        var workspaceRoot = FindWorkspaceRoot(assemblyDir);

        var candidates = BuildCandidatePaths(assemblyDir, workspaceRoot);

        foreach (var path in candidates)
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath)) return fullPath;
        }

        return string.Empty;
    }

    private static List<string> BuildCandidatePaths(string assemblyDir, string? workspaceRoot)
    {
        var candidates = new List<string>
        {
            // Same directory
            Path.Combine(assemblyDir, DdxConvExeName),
            // Sibling DDXConv folder
            Path.Combine(assemblyDir, "..", DdxConvFolderName, DdxConvExeName)
        };

        // Add workspace-relative paths if we found the workspace root
        if (!string.IsNullOrEmpty(workspaceRoot))
        {
            candidates.Add(Path.Combine(workspaceRoot, "src", DdxConvFolderName, DdxConvFolderName, "bin", "Release",
                TargetFramework, DdxConvExeName));
            candidates.Add(Path.Combine(workspaceRoot, "src", DdxConvFolderName, DdxConvFolderName, "bin", "Debug",
                TargetFramework, DdxConvExeName));
        }

        // Development layout relative to assembly
        candidates.Add(Path.Combine(assemblyDir, "..", "..", "..", "..", "..", DdxConvFolderName, DdxConvFolderName,
            "bin", "Release", TargetFramework, DdxConvExeName));
        candidates.Add(Path.Combine(assemblyDir, "..", "..", "..", "..", "..", DdxConvFolderName, DdxConvFolderName,
            "bin", "Debug", TargetFramework, DdxConvExeName));

        return candidates;
    }

    /// <summary>
    ///     Try to find the workspace root by looking for solution or project files.
    /// </summary>
    private static string? FindWorkspaceRoot(string startDir)
    {
        var dir = startDir;
        while (!string.IsNullOrEmpty(dir))
        {
            // Look for solution file
            if (Directory.GetFiles(dir, "*.slnx").Length > 0 ||
                Directory.GetFiles(dir, "*.sln").Length > 0)
                return dir;

            var parent = Directory.GetParent(dir);
            if (parent == null) break;

            dir = parent.FullName;
        }

        return null;
    }

    /// <summary>
    ///     Check if DDXConv is available.
    /// </summary>
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

    /// <summary>
    ///     Convert a DDX file to DDS format.
    /// </summary>
    public bool ConvertFile(string inputPath, string outputPath)
    {
        Processed++;

        try
        {
            var args = BuildConversionArguments(inputPath, outputPath);

            if (_verbose) Console.WriteLine($"Running: {DdxConvPath} {args}");

            using var process = StartDdxConvProcess(args);
            if (process == null) return RecordFailure("Failed to start DDXConv process");

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (_verbose && !string.IsNullOrEmpty(stdout)) Console.WriteLine(stdout);

            if (process.ExitCode != 0) return RecordFailure($"DDXConv exited with code {process.ExitCode}", stderr);

            if (!File.Exists(outputPath)) return RecordFailure($"Output file was not created: {outputPath}");

            Succeeded++;
            return true;
        }
        catch (Exception ex)
        {
            return RecordFailure($"Exception during conversion: {ex.Message}");
        }
    }

    private string BuildConversionArguments(string inputPath, string outputPath)
    {
        var args = $"\"{inputPath}\" \"{outputPath}\"";
        if (_verbose) args += " --verbose";

        if (_saveAtlas) args += " --atlas";

        return args;
    }

    private Process? StartDdxConvProcess(string args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = DdxConvPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        return Process.Start(startInfo);
    }

    private bool RecordFailure(string message, string? stderr = null)
    {
        Failed++;
        if (_verbose)
        {
            Console.WriteLine(message);
            if (!string.IsNullOrEmpty(stderr)) Console.WriteLine($"Error: {stderr}");
        }

        return false;
    }

    /// <summary>
    ///     Convert a DDX file to DDS format asynchronously.
    /// </summary>
    public Task<bool> ConvertFileAsync(string inputPath, string outputPath)
    {
        return Task.Run(() => ConvertFile(inputPath, outputPath));
    }

    /// <summary>
    ///     Convert DDX data from memory with detailed result information.
    /// </summary>
    public DdxConversionResult ConvertFromMemoryWithResult(byte[] ddxData)
    {
        Processed++;

        string? tempInputPath = null;
        string? tempOutputPath = null;

        try
        {
            (tempInputPath, tempOutputPath) = CreateTempFiles();
            File.WriteAllBytes(tempInputPath, ddxData);

            var args = $"\"{tempInputPath}\" \"{tempOutputPath}\" --verbose";
            if (_saveAtlas) args += " --atlas";

            using var process = StartDdxConvProcess(args);
            if (process == null)
            {
                Failed++;
                return DdxConversionResult.Failure("Failed to start DDXConv process");
            }

            return ProcessConversionResult(process, tempOutputPath);
        }
        catch (Exception ex)
        {
            Failed++;
            return DdxConversionResult.Failure($"Exception: {ex.Message}");
        }
        finally
        {
            CleanupTempFiles(tempInputPath, tempOutputPath);
        }
    }

    private static (string inputPath, string outputPath) CreateTempFiles()
    {
        var tempPath = Path.GetTempPath();
        return (
            Path.Combine(tempPath, $"ddx_{Guid.NewGuid():N}.ddx"),
            Path.Combine(tempPath, $"dds_{Guid.NewGuid():N}.dds")
        );
    }

    private DdxConversionResult ProcessConversionResult(Process process, string tempOutputPath)
    {
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        var consoleOutput = stdout + (string.IsNullOrEmpty(stderr) ? "" : $"\nSTDERR: {stderr}");

        if (_verbose && !string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout.TrimEnd());

        var (isPartial, notes) = AnalyzeConsoleOutput(consoleOutput);

        if (process.ExitCode != 0 && !File.Exists(tempOutputPath))
        {
            Failed++;
            return new DdxConversionResult
            {
                Success = false,
                IsPartial = isPartial,
                Notes = notes ?? $"DDXConv exited with code {process.ExitCode}",
                ConsoleOutput = consoleOutput
            };
        }

        if (!File.Exists(tempOutputPath))
        {
            Failed++;
            return new DdxConversionResult
            {
                Success = false, Notes = "Output file was not created", ConsoleOutput = consoleOutput
            };
        }

        var ddsData = File.ReadAllBytes(tempOutputPath);
        var atlasData = ReadAtlasDataIfExists(tempOutputPath);

        Succeeded++;

        return new DdxConversionResult
        {
            Success = true,
            DdsData = ddsData,
            AtlasData = atlasData,
            IsPartial = isPartial,
            Notes = notes,
            ConsoleOutput = _verbose ? consoleOutput : null
        };
    }

    private static (bool isPartial, string? notes) AnalyzeConsoleOutput(string consoleOutput)
    {
        var isPartial = false;
        string? notes = null;

        var hasAtlasOnly = consoleOutput.Contains("atlas-only", StringComparison.OrdinalIgnoreCase);
        var hasPartial = consoleOutput.Contains("partial", StringComparison.OrdinalIgnoreCase);

        if (hasAtlasOnly || hasPartial)
        {
            isPartial = true;
            if (consoleOutput.Contains("atlas-only", StringComparison.Ordinal))
                notes = ExtractNotesFromOutput(consoleOutput, "atlas-only");
            else if (consoleOutput.Contains("mip levels recovered", StringComparison.Ordinal))
                notes = ExtractNotesFromOutput(consoleOutput, "mip levels");
        }

        if (consoleOutput.Contains("truncated", StringComparison.OrdinalIgnoreCase))
        {
            isPartial = true;
            notes = (notes != null ? notes + "; " : "") + "truncated data";
        }

        return (isPartial, notes);
    }

    private byte[]? ReadAtlasDataIfExists(string tempOutputPath)
    {
        if (!_saveAtlas) return null;

        var tempAtlasPath = tempOutputPath.Replace(".dds", "_full_atlas.dds");
        return File.Exists(tempAtlasPath) ? File.ReadAllBytes(tempAtlasPath) : null;
    }

    private static void CleanupTempFiles(string? tempInputPath, string? tempOutputPath)
    {
        try
        {
            if (tempInputPath != null && File.Exists(tempInputPath)) File.Delete(tempInputPath);

            if (tempOutputPath != null && File.Exists(tempOutputPath)) File.Delete(tempOutputPath);

            if (tempOutputPath != null)
            {
                var tempAtlasPath = tempOutputPath.Replace(".dds", "_full_atlas.dds");
                if (File.Exists(tempAtlasPath)) File.Delete(tempAtlasPath);
            }
        }
        catch
        {
            // Intentionally empty: cleanup is best-effort and failures are ignored
        }
    }

    /// <summary>
    ///     Convert DDX data from memory to DDS format asynchronously with detailed result.
    /// </summary>
    public Task<DdxConversionResult> ConvertFromMemoryWithResultAsync(byte[] ddxData)
    {
        return Task.Run(() => ConvertFromMemoryWithResult(ddxData));
    }

    private static string? ExtractNotesFromOutput(string output, string keyword)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var matchingLine = lines.FirstOrDefault(line => line.Contains(keyword, StringComparison.OrdinalIgnoreCase));

        if (matchingLine == null) return null;

        var note = matchingLine.Trim();
        if (note.StartsWith('['))
        {
            var bracketEnd = note.IndexOf(']');
            if (bracketEnd > 0 && bracketEnd < note.Length - 1) note = note[(bracketEnd + 1)..].Trim();
        }

        return note;
    }

    /// <summary>
    ///     Check if the provided data starts with a valid DDX signature.
    /// </summary>
    public static bool IsDdxFile(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length < 4) return false;

        var magic = BitConverter.ToUInt32(data, 0);
        return magic is 0x4F445833 or 0x52445833;
    }

    /// <summary>
    ///     Check if the provided data starts with a valid DDX signature (span version).
    /// </summary>
    public static bool IsDdxFile(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return false;

        var magic = BitConverter.ToUInt32(data);
        return magic is 0x4F445833 or 0x52445833;
    }
}

/// <summary>
///     Result of DDX to DDS conversion.
/// </summary>
public class DdxConversionResult
{
    public bool Success { get; init; }
    public byte[]? DdsData { get; init; }
    public byte[]? AtlasData { get; init; }
    public bool IsPartial { get; init; }
    public string? Notes { get; init; }
    public string? ConsoleOutput { get; init; }

    public static DdxConversionResult Failure(string notes)
    {
        return new DdxConversionResult { Success = false, Notes = notes };
    }
}
