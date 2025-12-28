using System.Diagnostics;

namespace Xbox360MemoryCarver.Core.Converters;

/// <summary>
///     Converts DDX files by invoking DDXConv as a subprocess.
/// </summary>
public class DdxSubprocessConverter
{
    private const string DdxConvExeName = "DDXConv.exe";
    private const string DdxConvFolderName = "DDXConv";
    private const string TargetFramework = "net9.0";

    private readonly bool _verbose;
    private readonly bool _saveAtlas;

    public int Processed { get; private set; }
    public int Succeeded { get; private set; }
    public int Failed { get; private set; }
    public string DdxConvPath { get; }

    public DdxSubprocessConverter(bool verbose = false, string? ddxConvPath = null, bool saveAtlas = false)
    {
        _verbose = verbose;
        _saveAtlas = saveAtlas;
        DdxConvPath = ddxConvPath ?? FindDdxConvPath();

        if (string.IsNullOrEmpty(DdxConvPath) || !File.Exists(DdxConvPath))
            throw new FileNotFoundException($"{DdxConvExeName} not found.", DdxConvPath ?? DdxConvExeName);
    }

    private static string FindDdxConvPath()
    {
        var envPath = Environment.GetEnvironmentVariable("DDXCONV_PATH");
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
            Path.Combine(assemblyDir, DdxConvExeName),
            Path.Combine(assemblyDir, "..", DdxConvFolderName, DdxConvExeName)
        };

        if (!string.IsNullOrEmpty(workspaceRoot))
        {
            candidates.Add(Path.Combine(workspaceRoot, "src", DdxConvFolderName, DdxConvFolderName, "bin", "Release", TargetFramework, DdxConvExeName));
            candidates.Add(Path.Combine(workspaceRoot, "src", DdxConvFolderName, DdxConvFolderName, "bin", "Debug", TargetFramework, DdxConvExeName));
        }

        candidates.Add(Path.Combine(assemblyDir, "..", "..", "..", "..", "..", DdxConvFolderName, DdxConvFolderName, "bin", "Release", TargetFramework, DdxConvExeName));
        candidates.Add(Path.Combine(assemblyDir, "..", "..", "..", "..", "..", DdxConvFolderName, DdxConvFolderName, "bin", "Debug", TargetFramework, DdxConvExeName));
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
        try { _ = new DdxSubprocessConverter(); return true; }
        catch { return false; }
    }

    public bool ConvertFile(string inputPath, string outputPath)
    {
        Processed++;
        try
        {
            var args = BuildConversionArguments(inputPath, outputPath);
            using var process = StartDdxConvProcess(args);
            if (process == null) { Failed++; return false; }

            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (_verbose && !string.IsNullOrEmpty(stdout)) Console.WriteLine(stdout);
            if (process.ExitCode != 0 || !File.Exists(outputPath)) { Failed++; return false; }

            Succeeded++;
            return true;
        }
        catch { Failed++; return false; }
    }

    private string BuildConversionArguments(string inputPath, string outputPath)
    {
        var args = $"\"{inputPath}\" \"{outputPath}\"";
        if (_verbose) args += " --verbose";
        if (_saveAtlas) args += " --atlas";
        return args;
    }

    private Process? StartDdxConvProcess(string args) => Process.Start(new ProcessStartInfo
    {
        FileName = DdxConvPath, Arguments = args, UseShellExecute = false,
        RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
    });

    public Task<bool> ConvertFileAsync(string inputPath, string outputPath) => Task.Run(() => ConvertFile(inputPath, outputPath));

    public DdxConversionResult ConvertFromMemoryWithResult(byte[] ddxData)
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
            if (_saveAtlas) args += " --atlas";

            using var process = StartDdxConvProcess(args);
            if (process == null) { Failed++; return DdxConversionResult.Failure("Failed to start DDXConv"); }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            var consoleOutput = stdout + (string.IsNullOrEmpty(stderr) ? "" : $"\nSTDERR: {stderr}");
            if (_verbose && !string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout.TrimEnd());

            var (isPartial, notes) = AnalyzeOutput(consoleOutput);

            if (!File.Exists(tempOutputPath)) { Failed++; return new DdxConversionResult { Success = false, IsPartial = isPartial, Notes = notes ?? $"Exit code {process.ExitCode}", ConsoleOutput = consoleOutput }; }

            var ddsData = File.ReadAllBytes(tempOutputPath);
            var atlasPath = tempOutputPath.Replace(".dds", "_full_atlas.dds");
            var atlasData = _saveAtlas && File.Exists(atlasPath) ? File.ReadAllBytes(atlasPath) : null;

            Succeeded++;
            return new DdxConversionResult { Success = true, DdsData = ddsData, AtlasData = atlasData, IsPartial = isPartial, Notes = notes, ConsoleOutput = _verbose ? consoleOutput : null };
        }
        catch (Exception ex) { Failed++; return DdxConversionResult.Failure($"Exception: {ex.Message}"); }
        finally { CleanupTempFiles(tempInputPath, tempOutputPath); }
    }

    private static (bool isPartial, string? notes) AnalyzeOutput(string output)
    {
        var isPartial = output.Contains("atlas-only", StringComparison.OrdinalIgnoreCase) || output.Contains("partial", StringComparison.OrdinalIgnoreCase);
        string? notes = null;
        if (output.Contains("truncated", StringComparison.OrdinalIgnoreCase)) { isPartial = true; notes = "truncated data"; }
        return (isPartial, notes);
    }

    private static void CleanupTempFiles(string? input, string? output)
    {
        try
        {
            if (input != null && File.Exists(input)) File.Delete(input);
            if (output != null && File.Exists(output)) File.Delete(output);
            if (output != null) { var atlas = output.Replace(".dds", "_full_atlas.dds"); if (File.Exists(atlas)) File.Delete(atlas); }
        }
        catch { /* Best-effort cleanup */ }
    }

    public Task<DdxConversionResult> ConvertFromMemoryWithResultAsync(byte[] ddxData) => Task.Run(() => ConvertFromMemoryWithResult(ddxData));

    public static bool IsDdxFile(byte[] data) => data?.Length >= 4 && BitConverter.ToUInt32(data, 0) is 0x4F445833 or 0x52445833;
    public static bool IsDdxFile(ReadOnlySpan<byte> data) => data.Length >= 4 && BitConverter.ToUInt32(data) is 0x4F445833 or 0x52445833;
}
