using System.Diagnostics;

namespace Xbox360MemoryCarver.Converters;

/// <summary>
/// Converts DDX files by invoking DDXConv as a subprocess.
/// This approach uses the proven 32-bit DDXConv with XnaNative.dll
/// for reliable LZX decompression.
/// </summary>
public class DdxSubprocessConverter
{
    private readonly string _ddxConvPath;
    private readonly bool _verbose;
    
    private int _processed;
    private int _succeeded;
    private int _failed;

    public int Processed => _processed;
    public int Succeeded => _succeeded;
    public int Failed => _failed;

    public DdxSubprocessConverter(bool verbose = false, string? ddxConvPath = null)
    {
        _verbose = verbose;
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
            candidates.Add(Path.Combine(workspaceRoot, "DDXConv", "DDXConv", "bin", "Release", "net9.0", "DDXConv.exe"));
            candidates.Add(Path.Combine(workspaceRoot, "DDXConv", "DDXConv", "bin", "Debug", "net9.0", "DDXConv.exe"));
        }
        
        // Development layout relative to assembly
        candidates.Add(Path.Combine(assemblyDir, "..", "..", "..", "..", "DDXConv", "DDXConv", "bin", "Release", "net9.0", "DDXConv.exe"));
        candidates.Add(Path.Combine(assemblyDir, "..", "..", "..", "..", "DDXConv", "DDXConv", "bin", "Debug", "net9.0", "DDXConv.exe"));

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
            // Look for solution file or Xbox360MemoryCarver.csproj
            if (Directory.GetFiles(dir, "*.slnx").Length > 0 ||
                Directory.GetFiles(dir, "*.sln").Length > 0 ||
                File.Exists(Path.Combine(dir, "Xbox360MemoryCarver.csproj")))
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
            // DDXConv uses positional args: <input_file> [output_file] [options]
            var args = $"\"{inputPath}\" \"{outputPath}\"";
            if (_verbose)
                args += " --verbose";

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

            // Verify output file was created
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
    /// Convert DDX data from memory to DDS format.
    /// Uses temp files internally to leverage the subprocess converter.
    /// </summary>
    public byte[]? ConvertFromMemory(byte[] ddxData)
    {
        _processed++;

        string? tempInputPath = null;
        string? tempOutputPath = null;

        try
        {
            // Create temp files for the conversion
            tempInputPath = Path.Combine(Path.GetTempPath(), $"ddx_{Guid.NewGuid():N}.ddx");
            tempOutputPath = Path.Combine(Path.GetTempPath(), $"dds_{Guid.NewGuid():N}.dds");

            // Write DDX data to temp file
            File.WriteAllBytes(tempInputPath, ddxData);

            // Convert using subprocess (don't double-count stats)
            _processed--; // ConvertFile will increment
            if (!ConvertFile(tempInputPath, tempOutputPath))
            {
                return null;
            }

            // Read converted DDS data
            var ddsData = File.ReadAllBytes(tempOutputPath);
            return ddsData;
        }
        catch (Exception ex)
        {
            _failed++;
            if (_verbose)
                Console.WriteLine($"Memory conversion failed: {ex.Message}");
            return null;
        }
        finally
        {
            // Clean up temp files
            try
            {
                if (tempInputPath != null && File.Exists(tempInputPath))
                    File.Delete(tempInputPath);
                if (tempOutputPath != null && File.Exists(tempOutputPath))
                    File.Delete(tempOutputPath);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    /// <summary>
    /// Convert DDX data from memory to DDS format asynchronously.
    /// </summary>
    public async Task<byte[]?> ConvertFromMemoryAsync(byte[] ddxData)
    {
        return await Task.Run(() => ConvertFromMemory(ddxData));
    }

    /// <summary>
    /// Check if the provided data starts with a valid DDX signature.
    /// </summary>
    public static bool IsDdxFile(byte[] data)
    {
        if (data.Length < 4)
            return false;

        // Check for 3XDO (0x4F445833) or 3XDR (0x52445833)
        uint magic = BitConverter.ToUInt32(data, 0);
        return magic == 0x4F445833 || magic == 0x52445833;
    }

    /// <summary>
    /// Get the path to the DDXConv executable being used.
    /// </summary>
    public string DdxConvPath => _ddxConvPath;
}
