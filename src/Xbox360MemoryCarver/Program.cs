using System.CommandLine;
using System.Diagnostics;
using System.Text;
using Xbox360MemoryCarver.Core.Carving;
using Xbox360MemoryCarver.Core.Minidump;
using Xbox360MemoryCarver.Core.Parsers;

namespace Xbox360MemoryCarver;

/// <summary>
///     Cross-platform CLI entry point for Xbox 360 Memory Carver.
///     On Windows with GUI build, this delegates to the GUI app unless --no-gui is specified.
/// </summary>
public static class Program
{
    /// <summary>
    ///     File path to auto-load when GUI starts (set via --file parameter).
    /// </summary>
    public static string? AutoLoadFile { get; internal set; }

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

#if WINDOWS_GUI
        // On Windows GUI build, check if we should launch GUI or CLI
        var isCliMode = args.Length > 0 && (args.Any(a => a.Equals("--no-gui", StringComparison.OrdinalIgnoreCase)) 
                                          || args.Any(a => a.Equals("-n", StringComparison.OrdinalIgnoreCase)));

        if (!isCliMode)
        {
            // Check for --file parameter for GUI mode
            AutoLoadFile = GetFlagValueInternal(args, "--file") ?? GetFlagValueInternal(args, "-f");

            // Also check for a single positional argument that's a .dmp file
            if (string.IsNullOrEmpty(AutoLoadFile) && args.Length > 0 && !args[0].StartsWith('-'))
            {
                var potentialFile = args[0];
                if (File.Exists(potentialFile) && potentialFile.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
                {
                    AutoLoadFile = potentialFile;
                }
            }

            return App.GuiEntryPoint.Run(args);
        }

        static string? GetFlagValueInternal(string[] args, string flag)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }
#endif

        // CLI mode
        return await RunCliAsync(args);
    }

    private static async Task<int> RunCliAsync(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("Xbox 360 Memory Carver - CLI Mode");
        Console.WriteLine("=================================");

        var rootCommand = new RootCommand("Xbox 360 Memory Dump File Carver");

        // Input path argument
        var inputArgument = new Argument<string?>(
            "input",
            () => null,
            "Path to memory dump file (.dmp) or DDX file/directory");

        // Output directory option
        var outputOption = new Option<string>(
            ["-o", "--output"],
            () => "output",
            "Output directory for carved files");

        // No GUI flag (required for CLI mode on Windows)
        var noGuiOption = new Option<bool>(
            ["-n", "--no-gui"],
            "Run in command-line mode without GUI (Windows only)");

        // DDX conversion mode
        var ddxOption = new Option<bool>(
            ["--ddx"],
            "Convert DDX textures to DDS format instead of carving");

        // Auto-convert DDX to DDS during carving
        var convertDdxOption = new Option<bool>(
            ["--convert-ddx"],
            () => true,
            "Automatically convert carved DDX textures to DDS format");

        // File types to extract
        var typesOption = new Option<string[]>(
            ["-t", "--types"],
            "File types to extract (e.g., dds ddx xma nif)");

        // Verbose output
        var verboseOption = new Option<bool>(
            ["-v", "--verbose"],
            "Enable verbose output");

        // Max files per type
        var maxFilesOption = new Option<int>(
            ["--max-files"],
            () => 10000,
            "Maximum files to extract per type");

        // Scan for compiled scripts (experimental)
        var scanScriptsOption = new Option<bool>(
            ["--scan-scripts"],
            "Scan for compiled script bytecode (experimental)");

        // Export compiled scripts
        var exportScriptsOption = new Option<bool>(
            ["--export-scripts"],
            "Export found compiled scripts as binary files");

        // Scan for ScriptInfo structures (experimental)
        var scanScriptInfoOption = new Option<bool>(
            ["--scan-scriptinfo"],
            "Scan for ScriptInfo structures to find compiled scripts (experimental)");

        // Analyze bytecode files (for debugging)
        var analyzeBytecodeOption = new Option<bool>(
            ["--analyze-bytecode"],
            "Analyze bytecode files in a directory to understand the format");

        rootCommand.AddArgument(inputArgument);
        rootCommand.AddOption(outputOption);
        rootCommand.AddOption(noGuiOption);
        rootCommand.AddOption(ddxOption);
        rootCommand.AddOption(convertDdxOption);
        rootCommand.AddOption(typesOption);
        rootCommand.AddOption(verboseOption);
        rootCommand.AddOption(maxFilesOption);
        rootCommand.AddOption(scanScriptsOption);
        rootCommand.AddOption(exportScriptsOption);
        rootCommand.AddOption(scanScriptInfoOption);
        rootCommand.AddOption(analyzeBytecodeOption);

        rootCommand.SetHandler(async context =>
        {
            var input = context.ParseResult.GetValueForArgument(inputArgument);
            var output = context.ParseResult.GetValueForOption(outputOption)!;
            var convertDdx = context.ParseResult.GetValueForOption(convertDdxOption);
            var types = context.ParseResult.GetValueForOption(typesOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var maxFiles = context.ParseResult.GetValueForOption(maxFilesOption);
            var scanScripts = context.ParseResult.GetValueForOption(scanScriptsOption);
            var exportScripts = context.ParseResult.GetValueForOption(exportScriptsOption);
            var scanScriptInfo = context.ParseResult.GetValueForOption(scanScriptInfoOption);
            var analyzeBytecode = context.ParseResult.GetValueForOption(analyzeBytecodeOption);

            if (string.IsNullOrEmpty(input))
            {
                Console.WriteLine("Error: Input path is required.");
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("  Xbox360MemoryCarver <input.dmp> -o <output_dir> [options]");
                Console.WriteLine();
                Console.WriteLine("Examples:");
                Console.WriteLine("  Xbox360MemoryCarver dump.dmp -o extracted");
                Console.WriteLine("  Xbox360MemoryCarver dump.dmp -o extracted -t ddx xma nif");
                Console.WriteLine("  Xbox360MemoryCarver dump.dmp -o extracted --convert-ddx -v");
                Console.WriteLine("  Xbox360MemoryCarver dump.dmp --scan-scripts -v");
                Console.WriteLine("  Xbox360MemoryCarver dump.dmp --scan-scriptinfo -v");
                Console.WriteLine("  Xbox360MemoryCarver scripts_dir --analyze-bytecode");
#if WINDOWS_GUI
                Console.WriteLine();
                Console.WriteLine("For GUI mode, run without arguments or with --file:");
                Console.WriteLine("  Xbox360MemoryCarver");
                Console.WriteLine("  Xbox360MemoryCarver --file dump.dmp");
#endif
                context.ExitCode = 1;
                return;
            }

            if (!File.Exists(input) && !Directory.Exists(input))
            {
                Console.WriteLine($"Error: Input path not found: {input}");
                context.ExitCode = 1;
                return;
            }

            try
            {
                if (analyzeBytecode)
                {
                    await BytecodeAnalyzer.AnalyzeBytecodeFilesAsync(input, maxFiles: 10);
                }
                else if (scanScriptInfo)
                {
                    await ScanForScriptInfoAsync(input, output, exportScripts, verbose);
                }
                else if (scanScripts)
                {
                    await ScanForCompiledScriptsAsync(input, output, exportScripts, verbose);
                }
                else
                {
                    await CarveFilesAsync(input, output, types?.ToList(), convertDdx, verbose, maxFiles);
                }
                context.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                if (verbose) Console.WriteLine(ex.StackTrace);

                context.ExitCode = 1;
            }
        });

        return await rootCommand.InvokeAsync(args);
    }

    private static async Task ScanForScriptInfoAsync(
        string inputPath,
        string outputDir,
        bool exportScripts,
        bool verbose)
    {
        if (!File.Exists(inputPath))
        {
            Console.WriteLine($"Error: File not found: {inputPath}");
            return;
        }

        Console.WriteLine($"Scanning for ScriptInfo structures in: {Path.GetFileName(inputPath)}");
        Console.WriteLine("NOTE: Xbox 360 uses PowerPC (big-endian) - all values read as big-endian");
        Console.WriteLine(new string('-', 60));

        var stopwatch = Stopwatch.StartNew();

        // Parse minidump to get memory region mapping
        Console.WriteLine("Parsing minidump structure...");
        var minidump = MinidumpParser.Parse(inputPath);
        if (!minidump.IsValid)
        {
            Console.WriteLine("Warning: Could not parse minidump structure - bytecode extraction may fail");
        }
        else
        {
            Console.WriteLine($"  Architecture: {(minidump.IsXbox360 ? "Xbox 360 (PowerPC)" : "Unknown")}");
            Console.WriteLine($"  Memory regions: {minidump.MemoryRegions.Count}");
            Console.WriteLine($"  Modules: {minidump.Modules.Count}");
        }

        // Read the entire file into memory for scanning
        var fileData = await File.ReadAllBytesAsync(inputPath);
        Console.WriteLine($"Loaded {fileData.Length:N0} bytes ({fileData.Length / 1024.0 / 1024.0:F2} MB)");

        Console.WriteLine();
        Console.WriteLine("Scanning for ScriptInfo structures...");
        Console.WriteLine("(Looking for: dataLen 8-10KB, type 0/1/0x100, compiled=1, refs 1-50, valid data ptr)");
        Console.WriteLine();

        var matches = ScriptInfoScanner.ScanForScriptInfo(fileData, maxResults: 500, verbose: verbose);

        stopwatch.Stop();
        Console.WriteLine();
        Console.WriteLine($"Scan completed in {stopwatch.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine();

        if (matches.Count == 0)
        {
            Console.WriteLine("No ScriptInfo structures found matching criteria.");
            return;
        }

        // Group by script type
        var byType = matches.GroupBy(m => m.ScriptType).OrderByDescending(g => g.Count());

        Console.WriteLine($"Found {matches.Count} potential ScriptInfo structure(s):");
        Console.WriteLine();
        
        foreach (var group in byType)
        {
            Console.WriteLine($"  {group.Key} scripts: {group.Count()}");
        }

        Console.WriteLine();
        Console.WriteLine("  Offset       DataLen  Refs  Vars  Type     DataPtr");
        Console.WriteLine("  " + new string('-', 62));

        var displayCount = verbose ? matches.Count : Math.Min(30, matches.Count);
        foreach (var match in matches.Take(displayCount))
        {
            Console.WriteLine($"  0x{match.Offset:X8}  {match.DataLength,7}  {match.NumRefs,4}  {match.VarCount,4}  {match.ScriptType,-8} 0x{match.DataPointer:X8}");
        }

        if (!verbose && matches.Count > 30)
        {
            Console.WriteLine($"  ... and {matches.Count - 30} more (use -v to see all)");
        }

        // Show hex dump of first few matches for analysis
        if (verbose && matches.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Hex dump of first 3 ScriptInfo structures (with context):");
            Console.WriteLine();

            foreach (var match in matches.Take(3))
            {
                Console.WriteLine($"=== ScriptInfo at 0x{match.Offset:X8} ===");
                Console.WriteLine($"    DataLen={match.DataLength}, Refs={match.NumRefs}, Vars={match.VarCount}");
                Console.WriteLine($"    TextPtr=0x{match.TextPointer:X8}, DataPtr=0x{match.DataPointer:X8}");
                
                // Show bytes before, at, and after the ScriptInfo
                var start = Math.Max(0, match.Offset - 16);
                var end = Math.Min(fileData.Length, match.Offset + 48);
                
                for (var i = start; i < end; i += 16)
                {
                    var lineEnd = Math.Min(i + 16, end);
                    var hex = string.Join(" ", fileData.Skip(i).Take(lineEnd - i).Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
                    var marker = (i <= match.Offset && match.Offset < i + 16) ? " <-- ScriptInfo" : "";
                    Console.WriteLine($"    {i:X8}: {hex,-48}{marker}");
                }
                
                // Try to show bytecode if we can map the address
                if (minidump.IsValid)
                {
                    var bytecode = ScriptInfoScanner.TryExtractBytecode(fileData, match, minidump);
                    if (bytecode != null)
                    {
                        Console.WriteLine($"    Bytecode ({bytecode.Length} bytes), first 48:");
                        Console.WriteLine($"      {BytecodeAnalyzer.AnalyzeBytecode(bytecode, tryBothEndian: true)}");
                    }
                    else
                    {
                        Console.WriteLine($"    Bytecode: Could not map virtual address to file offset");
                    }
                }
                Console.WriteLine();
            }
        }

        // Export if requested
        if (exportScripts)
        {
            var scriptsDir = Path.Combine(outputDir, "scriptinfo_scan");
            Directory.CreateDirectory(scriptsDir);

            Console.WriteLine();
            Console.WriteLine($"Exporting to: {scriptsDir}");

            // Write summary
            var summaryPath = Path.Combine(scriptsDir, "scriptinfo_summary.csv");
            await using var writer = new StreamWriter(summaryPath);
            await writer.WriteLineAsync("Offset,DataLength,NumRefs,VarCount,Type,TextPtr,DataPtr");
            
            foreach (var match in matches)
            {
                await writer.WriteLineAsync($"0x{match.Offset:X8},{match.DataLength},{match.NumRefs},{match.VarCount},{match.ScriptType},0x{match.TextPointer:X8},0x{match.DataPointer:X8}");
            }

            Console.WriteLine($"Summary written to: {summaryPath}");

            // Try to extract bytecode using minidump mapping and decompile
            if (minidump.IsValid)
            {
                var extracted = 0;
                var decompiled = 0;
                var failed = 0;
                
                Console.WriteLine($"Extracting and decompiling bytecode...");
                
                foreach (var match in matches)
                {
                    var bytecode = ScriptInfoScanner.TryExtractBytecode(fileData, match, minidump);
                    if (bytecode != null)
                    {
                        var baseName = $"script_0x{match.Offset:X8}_{match.ScriptType}";
                        
                        // Write binary
                        await File.WriteAllBytesAsync(Path.Combine(scriptsDir, baseName + ".bin"), bytecode);
                        extracted++;
                        
                        // Try to decompile
                        try
                        {
                            var decompiler = new ScriptDecompiler(bytecode, 0, bytecode.Length, isBigEndian: true);
                            var result = decompiler.Decompile();
                            
                            if (!string.IsNullOrWhiteSpace(result.DecompiledText))
                            {
                                var header = new StringBuilder();
                                header.AppendLine($"; Decompiled script from ScriptInfo at 0x{match.Offset:X8}");
                                header.AppendLine($"; Type: {match.ScriptType}");
                                header.AppendLine($"; DataLength: {match.DataLength}, NumRefs: {match.NumRefs}, VarCount: {match.VarCount}");
                                header.AppendLine($"; Bytecode size: {bytecode.Length} bytes");
                                if (!result.Success)
                                    header.AppendLine($"; NOTE: Partial decompilation - {result.ErrorMessage}");
                                header.AppendLine(";");
                                header.AppendLine();
                                
                                await File.WriteAllTextAsync(
                                    Path.Combine(scriptsDir, baseName + ".txt"), 
                                    header.ToString() + result.DecompiledText);
                                decompiled++;
                            }
                        }
                        catch
                        {
                            // Ignore decompile errors
                        }
                    }
                    else
                    {
                        failed++;
                    }
                }

                Console.WriteLine($"Extracted {extracted} bytecode file(s), decompiled {decompiled}, {failed} could not be mapped");
            }
            else
            {
                Console.WriteLine("Skipping bytecode extraction - minidump structure not valid");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Done!");
    }

    private static async Task ScanForCompiledScriptsAsync(
        string inputPath,
        string outputDir,
        bool exportScripts,
        bool verbose)
    {
        if (!File.Exists(inputPath))
        {
            Console.WriteLine($"Error: File not found: {inputPath}");
            return;
        }

        Console.WriteLine($"Scanning for compiled scripts in: {Path.GetFileName(inputPath)}");
        Console.WriteLine("NOTE: Xbox 360 uses PowerPC (big-endian) - reading bytecode as big-endian");
        Console.WriteLine(new string('-', 60));

        var stopwatch = Stopwatch.StartNew();

        // Read the entire file into memory for scanning
        var fileData = await File.ReadAllBytesAsync(inputPath);
        Console.WriteLine($"Loaded {fileData.Length:N0} bytes ({fileData.Length / 1024.0 / 1024.0:F2} MB)");

        // Scan for compiled scripts
        Console.WriteLine();
        Console.WriteLine("Scanning for compiled script bytecode (opcode 0x1D = ScriptName)...");

        var matches = CompiledScriptScanner.ScanForCompiledScripts(fileData, maxResults: 500, isBigEndian: true);

        stopwatch.Stop();
        Console.WriteLine($"Scan completed in {stopwatch.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine();

        if (matches.Count == 0)
        {
            Console.WriteLine("No compiled scripts found.");
            Console.WriteLine();
            Console.WriteLine("Note: This scanner looks for bytecode starting with opcode 0x1D (ScriptName).");
            Console.WriteLine("If this is a Debug build dump, scripts may be in source text format instead.");
            return;
        }

        // Sort by confidence
        matches = [.. matches.OrderByDescending(m => m.Confidence)];

        Console.WriteLine($"Found {matches.Count} potential compiled script(s):");
        Console.WriteLine();
        Console.WriteLine("  Offset       Size    Stmts  Begin/End  Confidence");
        Console.WriteLine("  " + new string('-', 55));

        var highConfidence = 0;
        var mediumConfidence = 0;
        var lowConfidence = 0;

        foreach (var match in matches)
        {
            var confidenceStr = match.Confidence >= 0.7f ? "HIGH" :
                                match.Confidence >= 0.5f ? "MEDIUM" : "LOW";

            if (match.Confidence >= 0.7f) highConfidence++;
            else if (match.Confidence >= 0.5f) mediumConfidence++;
            else lowConfidence++;

            if (verbose || match.Confidence >= 0.5f)
            {
                Console.WriteLine($"  0x{match.Offset:X8}  {match.Size,6}  {match.StatementCount,5}  {match.BeginCount,5}/{match.EndCount,-5}  {match.Confidence:P0} ({confidenceStr})");
            }
        }

        if (!verbose && lowConfidence > 0)
        {
            Console.WriteLine($"  ... and {lowConfidence} low-confidence matches (use -v to see all)");
        }

        Console.WriteLine();
        Console.WriteLine($"Summary: {highConfidence} high, {mediumConfidence} medium, {lowConfidence} low confidence");

        // Export if requested
        if (exportScripts && highConfidence > 0)
        {
            var scriptsDir = Path.Combine(outputDir, "compiled_scripts");
            Directory.CreateDirectory(scriptsDir);

            Console.WriteLine();
            Console.WriteLine($"Exporting high-confidence scripts to: {scriptsDir}");

            var exported = 0;
            var decompiled = 0;

            foreach (var match in matches.Where(m => m.Confidence >= 0.7f))
            {
                var filename = $"script_0x{match.Offset:X8}";
                var binPath = Path.Combine(scriptsDir, filename + ".bin");
                var txtPath = Path.Combine(scriptsDir, filename + ".txt");

                // Export binary
                var scriptData = new byte[match.Size];
                Array.Copy(fileData, match.Offset, scriptData, 0, match.Size);
                await File.WriteAllBytesAsync(binPath, scriptData);
                exported++;

                // Try to decompile
                try
                {
                    // Xbox 360 uses big-endian bytecode
                    var decompiler = new ScriptDecompiler(fileData, match.Offset, match.Size, isBigEndian: true);
                    var result = decompiler.Decompile();

                    if (result.Success || !string.IsNullOrEmpty(result.DecompiledText))
                    {
                        var header = $"; Decompiled script from offset 0x{match.Offset:X8}\n";
                        header += $"; Size: {match.Size} bytes, Statements: {match.StatementCount}\n";
                        header += $"; Event blocks: {string.Join(", ", result.EventBlocks)}\n";
                        if (!result.Success)
                            header += $"; WARNING: Partial decompilation - {result.ErrorMessage}\n";
                        header += ";\n";

                        await File.WriteAllTextAsync(txtPath, header + result.DecompiledText);
                        decompiled++;
                    }
                }
                catch (Exception ex)
                {
                    if (verbose)
                        Console.WriteLine($"  Warning: Failed to decompile {filename}: {ex.Message}");
                }

                if (verbose)
                {
                    Console.WriteLine($"  Exported: {filename} ({match.Size} bytes)");
                }
            }

            Console.WriteLine($"Exported {exported} binary file(s), decompiled {decompiled} script(s)");

            // Also create a summary file
            var summaryPath = Path.Combine(scriptsDir, "scripts_summary.txt");
            await using var summaryWriter = new StreamWriter(summaryPath);
            await summaryWriter.WriteLineAsync($"Compiled Script Scan Results (Xbox 360 Big-Endian)");
            await summaryWriter.WriteLineAsync($"Source: {Path.GetFileName(inputPath)}");
            await summaryWriter.WriteLineAsync($"Date: {DateTime.Now}");
            await summaryWriter.WriteLineAsync();
            await summaryWriter.WriteLineAsync($"Found {matches.Count} potential scripts, {highConfidence} high confidence");
            await summaryWriter.WriteLineAsync();
            await summaryWriter.WriteLineAsync("High Confidence Scripts:");
            await summaryWriter.WriteLineAsync(new string('-', 60));

            foreach (var match in matches.Where(m => m.Confidence >= 0.7f))
            {
                await summaryWriter.WriteLineAsync($"Offset: 0x{match.Offset:X8}");
                await summaryWriter.WriteLineAsync($"  Size: {match.Size} bytes");
                await summaryWriter.WriteLineAsync($"  Statements: {match.StatementCount}");
                await summaryWriter.WriteLineAsync($"  Begin/End blocks: {match.BeginCount}/{match.EndCount}");
                await summaryWriter.WriteLineAsync($"  Confidence: {match.Confidence:P0}");

                // Analyze and show opcode breakdown
                var info = CompiledScriptScanner.AnalyzeCompiledScript(fileData, match.Offset);
                if (info != null)
                {
                    var opcodeCounts = info.Opcodes
                        .GroupBy(o => o.OpcodeName)
                        .OrderByDescending(g => g.Count())
                        .Take(10);

                    await summaryWriter.WriteLineAsync($"  Top opcodes:");
                    foreach (var group in opcodeCounts)
                    {
                        await summaryWriter.WriteLineAsync($"    {group.Key}: {group.Count()}");
                    }
                }

                await summaryWriter.WriteLineAsync();
            }

            Console.WriteLine($"Summary written to: {summaryPath}");
        }

        Console.WriteLine();
        Console.WriteLine("Done!");
    }

    private static async Task CarveFilesAsync(
        string inputPath,
        string outputDir,
        List<string>? fileTypes,
        bool convertDdx,
        bool verbose,
        int maxFiles)
    {
        var files = new List<string>();

        if (File.Exists(inputPath))
            files.Add(inputPath);
        else if (Directory.Exists(inputPath))
            files.AddRange(Directory.GetFiles(inputPath, "*.dmp", SearchOption.TopDirectoryOnly));

        if (files.Count == 0)
        {
            Console.WriteLine("No dump files found.");
            return;
        }

        Console.WriteLine($"Found {files.Count} file(s) to process");

        foreach (var file in files)
        {
            Console.WriteLine();
            Console.WriteLine($"Processing: {Path.GetFileName(file)}");
            Console.WriteLine(new string('-', 50));

            var carver = new MemoryCarver(
                outputDir,
                maxFiles,
                convertDdx,
                fileTypes,
                verbose);

            var progress = new Progress<double>(p =>
            {
                if (verbose) Console.Write($"\rProgress: {p * 100:F1}%");
            });

            var stopwatch = Stopwatch.StartNew();
            var results = await carver.CarveDumpAsync(file, progress);
            stopwatch.Stop();

            Console.WriteLine();
            Console.WriteLine($"Extracted {results.Count} files in {stopwatch.Elapsed.TotalSeconds:F2}s");

            // Print stats
            Console.WriteLine();
            Console.WriteLine("File type summary:");
            foreach (var (type, count) in carver.Stats.OrderByDescending(x => x.Value))
                if (count > 0)
                    Console.WriteLine($"  {type}: {count}");

            if (convertDdx && (carver.DdxConvertedCount > 0 || carver.DdxConvertFailedCount > 0))
            {
                Console.WriteLine();
                Console.WriteLine(
                    $"DDX conversions: {carver.DdxConvertedCount} successful, {carver.DdxConvertFailedCount} failed");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Done!");
    }
}
