using System.Diagnostics;

namespace Xbox360MemoryCarver.Core.Converters;

/// <summary>
/// Converts DDX files by invoking DDXConv as a subprocess.
/// This approach uses the 32-bit DDXConv.exe with XnaNative.dll
/// for reliable LZX decompression, which can't be loaded into a 64-bit process.
/// </summary>
public class DdxSubprocessConverter
{
    private readonly string _ddxConvPath;
    private readonly bool _verbose;
    private readonly bool _saveAtlas;

    private int _processed;
    private int _succeeded;
    private int _failed;

    public int Processed => _processed;
    public int Succeeded => _succeeded;
    public int Failed => _failed;

    public DdxSubprocessConverter(bool verbose = false, string? ddxConvPath = null, bool saveAtlas = false)
    {
        _verbose = verbose;
        _saveAtlas = saveAtlas;
        _ddxConvPath = ddxConvPath ?? FindDdxConvPath();

        if (string.IsNullOrEmpty(_ddxConvPath) || !File.Exists(_ddxConvPath))
        {
            throw new FileNotFoundException(
                "DDXConv.exe not found. Please build DDXConv or specify its path.",
                _ddxConvPath ?? "DDXConv.exe");
        }
    }

    /// <summary>
    /// Try to find DDXConv.exe in common locations relative to this assembly.
    /// </summary>
    private static string FindDdxConvPath()
    {
        // Check environment variable first
        var envPath = Environment.GetEnvironmentVariable("DDXCONV_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            return envPath;

        var assemblyDir = AppContext.BaseDirectory;

        // For development: find workspace root by looking for .sln or .csproj
        var workspaceRoot = FindWorkspaceRoot(assemblyDir);

        // Check various possible locations
        var candidates = new List<string>
        {
            // Same directory
            Path.Combine(assemblyDir, "DDXConv.exe"),
            // Sibling DDXConv folder
            Path.Combine(assemblyDir, "..", "DDXConv", "DDXConv.exe"),
        };

        // Add workspace-relative paths if we found the workspace root
        if (!string.IsNullOrEmpty(workspaceRoot))
        {
            candidates.Add(Path.Combine(workspaceRoot, "src", "DDXConv", "DDXConv", "bin", "Release", "net9.0", "DDXConv.exe"));
            candidates.Add(Path.Combine(workspaceRoot, "src", "DDXConv", "DDXConv", "bin", "Debug", "net9.0", "DDXConv.exe"));
        }

        // Development layout relative to assembly
        candidates.Add(Path.Combine(assemblyDir, "..", "..", "..", "..", "..", "DDXConv", "DDXConv", "bin", "Release", "net9.0", "DDXConv.exe"));
        candidates.Add(Path.Combine(assemblyDir, "..", "..", "..", "..", "..", "DDXConv", "DDXConv", "bin", "Debug", "net9.0", "DDXConv.exe"));

        foreach (var path in candidates)
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
                return fullPath;
        }

        return string.Empty;
    }

    /// <summary>
    /// Try to find the workspace root by looking for solution or project files.
    /// </summary>
    private static string? FindWorkspaceRoot(string startDir)
    {
        var dir = startDir;
        while (!string.IsNullOrEmpty(dir))
        {
            // Look for solution file
            if (Directory.GetFiles(dir, "*.slnx").Length > 0 ||
                Directory.GetFiles(dir, "*.sln").Length > 0)
            {
                return dir;
            }

            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }
        return null;
    }

    /// <summary>
    /// Check if DDXConv is available.
    /// </summary>
    public static bool IsAvailable()
    {
        try
        {
            var converter = new DdxSubprocessConverter();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Convert a DDX file to DDS format.
    /// </summary>
    public bool ConvertFile(string inputPath, string outputPath)
    {
        _processed++;

        try
        {
            var args = $"\"{inputPath}\" \"{outputPath}\"";
            if (_verbose)
                args += " --verbose";
            if (_saveAtlas)
                args += " --atlas";

            if (_verbose)
                Console.WriteLine($"Running: {_ddxConvPath} {args}");

            var startInfo = new ProcessStartInfo
            {
                FileName = _ddxConvPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                _failed++;
                if (_verbose)
                    Console.WriteLine("Failed to start DDXConv process");
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (_verbose && !string.IsNullOrEmpty(stdout))
                Console.WriteLine(stdout);

            if (process.ExitCode != 0)
            {
                _failed++;
                if (_verbose)
                {
                    Console.WriteLine($"DDXConv exited with code {process.ExitCode}");
                    if (!string.IsNullOrEmpty(stderr))
                        Console.WriteLine($"Error: {stderr}");
                }
                return false;
            }

            if (!File.Exists(outputPath))
            {
                _failed++;
                if (_verbose)
                    Console.WriteLine($"Output file was not created: {outputPath}");
                return false;
            }

            _succeeded++;
            return true;
        }
        catch (Exception ex)
        {
            _failed++;
            if (_verbose)
                Console.WriteLine($"Exception during conversion: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Convert a DDX file to DDS format asynchronously.
    /// </summary>
    public async Task<bool> ConvertFileAsync(string inputPath, string outputPath)
    {
        return await Task.Run(() => ConvertFile(inputPath, outputPath));
    }

    /// <summary>
    /// Convert DDX data from memory with detailed result information.
    /// </summary>
    public DdxConversionResult ConvertFromMemoryWithResult(byte[] ddxData)
    {
        _processed++;

        string? tempInputPath = null;
        string? tempOutputPath = null;

        try
        {
            tempInputPath = Path.Combine(Path.GetTempPath(), $"ddx_{Guid.NewGuid():N}.ddx");
            tempOutputPath = Path.Combine(Path.GetTempPath(), $"dds_{Guid.NewGuid():N}.dds");

            File.WriteAllBytes(tempInputPath, ddxData);

            var args = $"\"{tempInputPath}\" \"{tempOutputPath}\" --verbose";
            if (_saveAtlas)
                args += " --atlas";

            var startInfo = new ProcessStartInfo
            {
                FileName = _ddxConvPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                _failed++;
                return new DdxConversionResult
                {
                    Success = false,
                    Notes = "Failed to start DDXConv process"
                };
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            var consoleOutput = stdout + (string.IsNullOrEmpty(stderr) ? "" : $"\nSTDERR: {stderr}");

            if (_verbose && !string.IsNullOrWhiteSpace(stdout))
                Console.WriteLine(stdout.TrimEnd());

            bool isPartial = false;
            string? notes = null;

            if (consoleOutput.Contains("atlas-only", StringComparison.OrdinalIgnoreCase) ||
                consoleOutput.Contains("partial", StringComparison.OrdinalIgnoreCase))
            {
                isPartial = true;
                if (consoleOutput.Contains("atlas-only"))
                    notes = ExtractNotesFromOutput(consoleOutput, "atlas-only");
                else if (consoleOutput.Contains("mip levels recovered"))
                    notes = ExtractNotesFromOutput(consoleOutput, "mip levels");
            }

            if (consoleOutput.Contains("truncated", StringComparison.OrdinalIgnoreCase))
            {
                isPartial = true;
                notes = (notes != null ? notes + "; " : "") + "truncated data";
            }

            if (process.ExitCode != 0 && !File.Exists(tempOutputPath))
            {
                _failed++;
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
                _failed++;
                return new DdxConversionResult
                {
                    Success = false,
                    Notes = "Output file was not created",
                    ConsoleOutput = consoleOutput
                };
            }

            var ddsData = File.ReadAllBytes(tempOutputPath);

            byte[]? atlasData = null;
            var tempAtlasPath = tempOutputPath.Replace(".dds", "_full_atlas.dds");
            if (_saveAtlas && File.Exists(tempAtlasPath))
            {
                atlasData = File.ReadAllBytes(tempAtlasPath);
            }

            _succeeded++;

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
        catch (Exception ex)
        {
            _failed++;
            return new DdxConversionResult
            {
                Success = false,
                Notes = $"Exception: {ex.Message}"
            };
        }
        finally
        {
            try
            {
                if (tempInputPath != null && File.Exists(tempInputPath))
                    File.Delete(tempInputPath);
                if (tempOutputPath != null && File.Exists(tempOutputPath))
                    File.Delete(tempOutputPath);
                if (tempOutputPath != null)
                {
                    var tempAtlasPath = tempOutputPath.Replace(".dds", "_full_atlas.dds");
                    if (File.Exists(tempAtlasPath))
                        File.Delete(tempAtlasPath);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Convert DDX data from memory to DDS format asynchronously with detailed result.
    /// </summary>
    public async Task<DdxConversionResult> ConvertFromMemoryWithResultAsync(byte[] ddxData)
    {
        return await Task.Run(() => ConvertFromMemoryWithResult(ddxData));
    }

    private static string? ExtractNotesFromOutput(string output, string keyword)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                var note = line.Trim();
                if (note.StartsWith('['))
                {
                    var bracketEnd = note.IndexOf(']');
                    if (bracketEnd > 0 && bracketEnd < note.Length - 1)
                        note = note[(bracketEnd + 1)..].Trim();
                }
                return note;
            }
        }
        return null;
    }

    /// <summary>
    /// Check if the provided data starts with a valid DDX signature.
    /// </summary>
    public static bool IsDdxFile(byte[] data)
    {
        if (data.Length < 4)
            return false;

        uint magic = BitConverter.ToUInt32(data, 0);
        return magic == 0x4F445833 || magic == 0x52445833;
    }

    /// <summary>
    /// Check if the provided data starts with a valid DDX signature (span version).
    /// </summary>
    public static bool IsDdxFile(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return false;

        uint magic = BitConverter.ToUInt32(data);
        return magic == 0x4F445833 || magic == 0x52445833;
    }

    /// <summary>
    /// Get the path to the DDXConv executable being used.
    /// </summary>
    public string DdxConvPath => _ddxConvPath;
}

/// <summary>
/// Result of DDX to DDS conversion.
/// </summary>
public class DdxConversionResult
{
    public bool Success { get; set; }
    public byte[]? DdsData { get; set; }
    public byte[]? AtlasData { get; set; }
    public bool IsPartial { get; set; }
    public string? Notes { get; set; }
    public string? ConsoleOutput { get; set; }
}
