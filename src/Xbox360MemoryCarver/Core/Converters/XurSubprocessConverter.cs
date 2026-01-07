using System.Diagnostics;

namespace Xbox360MemoryCarver.Core.Converters;

/// <summary>
///     Converts XUR (Xbox UI Runtime) binary files to XUI (XML source) by invoking XUIHelper.CLI as a subprocess.
/// </summary>
public class XurSubprocessConverter
{
    private const string XuiHelperExeName = "XUIHelper.CLI.exe";
    private const string XuiHelperFolderName = "XUIHelper";
    private const string XuiHelperCliProject = "XUIHelper.CLI";
    private const string TargetFramework = "net8.0";

    private readonly bool _verbose;

    public XurSubprocessConverter(bool verbose = false, string? xuiHelperPath = null)
    {
        _verbose = verbose;
        XuiHelperPath = xuiHelperPath ?? FindXuiHelperPath();

        if (string.IsNullOrEmpty(XuiHelperPath) || !File.Exists(XuiHelperPath))
            throw new FileNotFoundException($"{XuiHelperExeName} not found.", XuiHelperPath ?? XuiHelperExeName);
    }

    public int Processed { get; private set; }
    public int Succeeded { get; private set; }
    public int Failed { get; private set; }
    public string XuiHelperPath { get; }

    private static string FindXuiHelperPath()
    {
        var envPath = Environment.GetEnvironmentVariable("XUIHELPER_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath)) return envPath;

        var assemblyDir = AppContext.BaseDirectory;
        var workspaceRoot = FindWorkspaceRoot(assemblyDir);

        foreach (var path in BuildCandidatePaths(assemblyDir, workspaceRoot))
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
            Path.Combine(assemblyDir, XuiHelperExeName),
            // Sibling folder
            Path.Combine(assemblyDir, "..", XuiHelperFolderName, XuiHelperExeName)
        };

        if (!string.IsNullOrEmpty(workspaceRoot))
        {
            // From workspace root - CLI project output
            candidates.Add(Path.Combine(workspaceRoot, "src", XuiHelperFolderName, XuiHelperCliProject, "bin",
                "Release",
                TargetFramework, XuiHelperExeName));
            candidates.Add(Path.Combine(workspaceRoot, "src", XuiHelperFolderName, XuiHelperCliProject, "bin", "Debug",
                TargetFramework, XuiHelperExeName));
        }

        // Relative paths from bin output
        candidates.Add(Path.Combine(assemblyDir, "..", "..", "..", "..", "..", XuiHelperFolderName, XuiHelperCliProject,
            "bin", "Release", TargetFramework, XuiHelperExeName));
        candidates.Add(Path.Combine(assemblyDir, "..", "..", "..", "..", "..", XuiHelperFolderName, XuiHelperCliProject,
            "bin", "Debug", TargetFramework, XuiHelperExeName));

        return candidates;
    }

    private static string? FindWorkspaceRoot(string startDir)
    {
        var dir = startDir;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.GetFiles(dir, "*.slnx").Length > 0 || Directory.GetFiles(dir, "*.sln").Length > 0) return dir;

            var parent = Directory.GetParent(dir);
            if (parent == null) break;

            dir = parent.FullName;
        }

        return null;
    }

    public static bool IsAvailable()
    {
        try
        {
            _ = new XurSubprocessConverter();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Detects XUR version from file header.
    /// </summary>
    /// <param name="data">File data</param>
    /// <returns>XUR version (5 or 8), or 0 if not a valid XUR</returns>
    public static int DetectXurVersion(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) return 0;

        // Check magic: XUIB (0x58554942) or XUIS (scene) in big-endian
        var isXuib = data[0] == 'X' && data[1] == 'U' && data[2] == 'I' && data[3] == 'B';
        var isXuis = data[0] == 'X' && data[1] == 'U' && data[2] == 'I' && data[3] == 'S';

        if (!isXuib && !isXuis) return 0;

        // Version is at offset 4-7, big-endian
        var version = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];
        return version is 5 or 8 ? version : 0;
    }

    /// <summary>
    ///     Gets the extension group name for a given XUR version.
    /// </summary>
    public static string GetExtensionGroup(int version)
    {
        return version switch
        {
            5 => "V5",
            8 => "V8",
            _ => "V5" // Default to V5
        };
    }

    public bool ConvertFile(string inputPath, string outputPath, int? version = null)
    {
        Processed++;
        try
        {
            // Auto-detect version if not specified
            if (version == null)
            {
                var data = File.ReadAllBytes(inputPath);
                version = DetectXurVersion(data);
                if (version == 0)
                {
                    if (_verbose) Console.WriteLine($"[XurConverter] Could not detect XUR version for {inputPath}");

                    Failed++;
                    return false;
                }
            }

            var extensionGroup = GetExtensionGroup(version.Value);
            var args = BuildConversionArguments(inputPath, outputPath, extensionGroup);

            using var process = StartXuiHelperProcess(args);
            if (process == null)
            {
                Failed++;
                return false;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (_verbose && !string.IsNullOrEmpty(stdout)) Console.WriteLine(stdout);

            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                if (_verbose) Console.WriteLine($"[XurConverter] Conversion failed: {stderr}");

                Failed++;
                return false;
            }

            Succeeded++;
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XurConverter] Exception converting {inputPath}: {ex.GetType().Name}: {ex.Message}");
            Failed++;
            return false;
        }
    }

    private static string BuildConversionArguments(string inputPath, string outputPath, string extensionGroup)
    {
        // conv -s <source> -f xuiv12 -o <output> -g <group>
        return $"conv -s \"{inputPath}\" -f xuiv12 -o \"{outputPath}\" -g \"{extensionGroup}\"";
    }

    private Process? StartXuiHelperProcess(string args)
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = XuiHelperPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(XuiHelperPath)
        });
    }

    public Task<bool> ConvertFileAsync(string inputPath, string outputPath, int? version = null)
    {
        return Task.Run(() => ConvertFile(inputPath, outputPath, version));
    }

    public XurConversionResult ConvertFromMemoryWithResult(byte[] xurData)
    {
        Processed++;
        string? tempInputPath = null, tempOutputPath = null;

        try
        {
            var version = DetectXurVersion(xurData);
            if (version == 0)
            {
                Failed++;
                return XurConversionResult.Failure("Could not detect XUR version");
            }

            var tempPath = Path.GetTempPath();
            tempInputPath = Path.Combine(tempPath, $"xur_{Guid.NewGuid():N}.xur");
            tempOutputPath = Path.Combine(tempPath, $"xui_{Guid.NewGuid():N}.xui");

            File.WriteAllBytes(tempInputPath, xurData);

            var extensionGroup = GetExtensionGroup(version);
            var args = BuildConversionArguments(tempInputPath, tempOutputPath, extensionGroup);

            using var process = StartXuiHelperProcess(args);
            if (process == null)
            {
                Failed++;
                return XurConversionResult.Failure("Failed to start XUIHelper.CLI");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            var consoleOutput = stdout + (string.IsNullOrEmpty(stderr) ? "" : $"\nSTDERR: {stderr}");
            if (_verbose && !string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout.TrimEnd());

            if (!File.Exists(tempOutputPath))
            {
                Failed++;
                return new XurConversionResult
                {
                    Success = false,
                    Notes = $"Exit code {process.ExitCode}: {stderr}",
                    ConsoleOutput = consoleOutput
                };
            }

            var xuiData = File.ReadAllBytes(tempOutputPath);

            Succeeded++;
            return new XurConversionResult
            {
                Success = true,
                XuiData = xuiData,
                XurVersion = version,
                ConsoleOutput = _verbose ? consoleOutput : null
            };
        }
        catch (Exception ex)
        {
            Failed++;
            return XurConversionResult.Failure($"Exception: {ex.Message}");
        }
        finally
        {
            CleanupTempFiles(tempInputPath, tempOutputPath);
        }
    }

    private static void CleanupTempFiles(string? input, string? output)
    {
        try
        {
            if (input != null && File.Exists(input)) File.Delete(input);

            if (output != null && File.Exists(output)) File.Delete(output);
        }
        catch
        {
            /* Best-effort cleanup */
        }
    }

    public Task<XurConversionResult> ConvertFromMemoryWithResultAsync(byte[] xurData)
    {
        return Task.Run(() => ConvertFromMemoryWithResult(xurData));
    }

    /// <summary>
    ///     Checks if data is a valid XUR file (XUIB or XUIS magic).
    /// </summary>
    public static bool IsXurFile(byte[] data)
    {
        return data?.Length >= 4 &&
               data[0] == 'X' && data[1] == 'U' && data[2] == 'I' &&
               (data[3] == 'B' || data[3] == 'S');
    }

    /// <summary>
    ///     Checks if data is a valid XUR file (XUIB or XUIS magic).
    /// </summary>
    public static bool IsXurFile(ReadOnlySpan<byte> data)
    {
        return data.Length >= 4 &&
               data[0] == 'X' && data[1] == 'U' && data[2] == 'I' &&
               (data[3] == 'B' || data[3] == 'S');
    }
}
