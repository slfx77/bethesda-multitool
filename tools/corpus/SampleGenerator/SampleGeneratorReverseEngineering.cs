using static SampleGenerator.SampleGeneratorConfig;

namespace SampleGenerator;

/// <summary>
///     Collects the binaries this project reverse-engineers into
///     <c>Sample/ReverseEngineering/</c>, so the Sample tree shows everything on hand rather than
///     leaving the RE inputs scattered through <c>tools/GhidraProject/</c>.
///     <para>
///         <b>Copied, never moved.</b> Around forty Ghidra scripts under <c>tools/GhidraProject/</c>
///         reference these files by path; relocating them would break every one for the sake of
///         tidiness. The same choice as the debug symbols — the originals stay put.
///     </para>
///     <para>
///         These are not shipped build content, which is why they are not part of any build
///         directory: one is a memory dump of a running process, and the rest are loose executables
///         staged for decompilation.
///     </para>
/// </summary>
internal static class SampleGeneratorReverseEngineering
{
    /// <summary>
    ///     Source (relative to <c>tools/GhidraProject/</c>) to destination (relative to
    ///     <c>Sample/ReverseEngineering/</c>), with what each one is.
    /// </summary>
    internal static readonly (string From, string To, string What)[] Binaries =
    [
        (@"runtime_dump\FalloutNV_runtime_image.bin",
            @"Fallout - New Vegas (PC)\FalloutNV_runtime_image.bin",
            "the DRM-FREE image: FalloutNV.exe dumped from memory by the NvseFaceGenProbe plugin's " +
            "FGProbeDumpModule console command, after Steam's DRM has unwrapped it. This is the " +
            "one wired into Ghidra as PEPCProject_FalloutNVRuntime."),

        (@"FalloutNV_PC.exe", @"Fallout - New Vegas (PC)\FalloutNV_PC.exe",
            "the on-disk PC executable, still DRM-wrapped"),

        (@"Geck.exe", @"Fallout - New Vegas (PC)\Geck.exe",
            "the GECK editor, which links the same middleware the game does (SpeedTreeRT, FaceGen)"),

        (@"Fallout3_PC.exe", @"Fallout 3 (PC)\Fallout3_PC.exe",
            "the Fallout 3 PC executable"),

        (@"Fallout_Debug.exe", @"Fallout - New Vegas (X360)\Fallout_Debug.exe",
            "the Xbox 360 Debug build, extracted from its XEX"),

        (@"Fallout_Release_MemDebug.exe", @"Fallout - New Vegas (X360)\Fallout_Release_MemDebug.exe",
            "the Xbox 360 MemDebug build — the one with PDB symbols, extracted from its XEX"),
    ];

    /// <summary>Outcome of collecting the RE binaries.</summary>
    internal readonly record struct ReverseEngineeringResult(int Copied, int Missing, long Bytes);

    internal static ReverseEngineeringResult Apply(bool dryRun)
    {
        var ghidra = FindGhidraProject();
        if (ghidra is null)
        {
            return default;
        }

        var copied = 0;
        var missing = 0;
        long bytes = 0;

        foreach (var (from, to, what) in Binaries)
        {
            var source = Path.Combine(ghidra, from);
            if (!File.Exists(source))
            {
                Console.WriteLine($"  skip  {from} (not present)");
                missing++;
                continue;
            }

            var destination = SampleGeneratorPathSafety.ResolveDestinationPath(
                SampleReverseEngineering, to);
            var length = new FileInfo(source).Length;

            if (File.Exists(destination) && new FileInfo(destination).Length == length)
            {
                continue;
            }

            if (dryRun)
            {
                Console.WriteLine($"  would copy  {from} -> ReverseEngineering\\{to} — {what}");
                copied++;
                bytes += length;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
            File.SetAttributes(destination, FileAttributes.Normal);
            Console.WriteLine($"  copied {from} -> ReverseEngineering\\{to}");
            copied++;
            bytes += length;
        }

        if (!dryRun && copied > 0)
        {
            WriteReadme();
        }

        return new ReverseEngineeringResult(copied, missing, bytes);
    }

    /// <summary>
    ///     Records what each binary is and where it came from, because a bare directory of
    ///     executables cannot say which one is DRM-free and which is not.
    /// </summary>
    private static void WriteReadme()
    {
        var path = Path.Combine(SampleReverseEngineering, "README.md");
        using var writer = new StreamWriter(path);
        writer.WriteLine("# Reverse-engineering binaries");
        writer.WriteLine();
        writer.WriteLine("Copies of the binaries loaded into Ghidra. The originals stay in");
        writer.WriteLine("`tools/GhidraProject/`, which is where the decompile scripts reference them,");
        writer.WriteLine("and the `.gpr` projects there are the working state.");
        writer.WriteLine();
        writer.WriteLine("These are not shipped build content, so they are not part of any build");
        writer.WriteLine("directory: one is a memory dump of a running process, the rest are loose");
        writer.WriteLine("executables staged for decompilation.");
        writer.WriteLine();
        writer.WriteLine("| File | What it is |");
        writer.WriteLine("| --- | --- |");
        foreach (var (_, to, what) in Binaries)
        {
            writer.WriteLine($"| `{to.Replace('\\', '/')}` | {what} |");
        }

        writer.WriteLine();
        writer.WriteLine("Regenerate the runtime dump by building `tools/NvseFaceGenProbe`, copying");
        writer.WriteLine("`NvseFaceGenProbe.dll` into `<Fallout New Vegas>\\Data\\NVSE\\Plugins\\`, and");
        writer.WriteLine("running `FGProbeDumpModule` at the in-game console.");
    }

    private static string? FindGhidraProject()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tools", "GhidraProject");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
