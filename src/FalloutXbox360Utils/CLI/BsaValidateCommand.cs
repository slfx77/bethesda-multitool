// Copyright (c) 2026 FalloutXbox360Utils Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Security.Cryptography;
using FalloutXbox360Utils.Core.Formats.Bsa;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI command for BSA round-trip validation (bsa validate).
/// </summary>
internal static class BsaValidateCommand
{
    public static Command CreateValidateCommand()
    {
        var command = new Command("validate", "Validate BSA round-trip (extract -> repack -> compare)");

        var inputArg = new Argument<string>("input") { Description = "Path to BSA file" };
        var keepTempOption = new Option<bool>("--keep-temp")
            { Description = "Keep temporary files after validation" };
        var verboseOption = new Option<bool>("-v", "--verbose") { Description = "Verbose output" };

        command.Arguments.Add(inputArg);
        command.Options.Add(keepTempOption);
        command.Options.Add(verboseOption);

        command.SetAction(async (parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var keepTemp = parseResult.GetValue(keepTempOption);
            var verbose = parseResult.GetValue(verboseOption);
            await RunValidateAsync(input, keepTemp, verbose);
        });

        return command;
    }

    private static async Task RunValidateAsync(string input, bool keepTemp, bool verbose)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"bsa_validate_{Guid.NewGuid():N}");
        var extractDir = Path.Combine(tempDir, "extracted");
        var repackedPath = Path.Combine(tempDir, "repacked.bsa");

        try
        {
            AnsiConsole.MarkupLine("[cyan]BSA Round-Trip Validation[/]");
            AnsiConsole.MarkupLine("[dim]Input:[/] {0}", input);
            AnsiConsole.WriteLine();

            // Step 1: Parse and analyze original
            AnsiConsole.MarkupLine("[yellow]Step 1:[/] Parsing original BSA...");
            var originalArchive = BsaParser.Parse(input);

            AnsiConsole.MarkupLine("  Version: {0}", originalArchive.Header.Version);
            AnsiConsole.MarkupLine("  Platform: {0}", originalArchive.Platform);
            AnsiConsole.MarkupLine("  Compressed: {0}", originalArchive.Header.DefaultCompressed);
            AnsiConsole.MarkupLine("  Folders: {0:N0}", originalArchive.Header.FolderCount);
            AnsiConsole.MarkupLine("  Files: {0:N0}", originalArchive.Header.FileCount);
            AnsiConsole.WriteLine();

            // Step 2: Extract all files
            AnsiConsole.MarkupLine("[yellow]Step 2:[/] Extracting files...");
            Directory.CreateDirectory(extractDir);

            using var extractor = new BsaExtractor(input);
            var extractResults = await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("[green]Extracting[/]", maxValue: originalArchive.TotalFiles);
                    var progress = new Progress<(int current, int total, string fileName)>(p =>
                    {
                        task.Value = p.current;
                    });
                    return await extractor.ExtractAllAsync(extractDir, true, progress);
                });

            var extractedCount = extractResults.Count(r => r.Success);
            var failedCount = extractResults.Count(r => !r.Success);
            AnsiConsole.MarkupLine("  Extracted: {0:N0} files", extractedCount);
            if (failedCount > 0)
            {
                AnsiConsole.MarkupLine("  [red]Failed: {0:N0} files[/]", failedCount);
                foreach (var failed in extractResults.Where(r => !r.Success))
                {
                    AnsiConsole.MarkupLine("    [red]\u2022 {0}: {1}[/]", failed.SourcePath,
                        failed.Error ?? "Unknown error");
                }
            }

            AnsiConsole.WriteLine();

            // Step 3: Build content hashes of extracted files
            AnsiConsole.MarkupLine("[yellow]Step 3:[/] Computing content hashes of extracted files...");
            var extractedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var outputPath in extractResults.Where(r => r.Success).Select(result => result.OutputPath))
            {
                var relativePath = Path.GetRelativePath(extractDir, outputPath)
                    .Replace('/', '\\').ToLowerInvariant();
                var hash = await ComputeFileHashAsync(outputPath);
                extractedHashes[relativePath] = hash;
            }

            AnsiConsole.MarkupLine("  Hashed: {0:N0} files", extractedHashes.Count);
            AnsiConsole.WriteLine();

            // Step 4: Repack using BsaWriter
            AnsiConsole.MarkupLine("[yellow]Step 4:[/] Repacking BSA...");

            using var writer = new BsaWriter(
                originalArchive.Header.DefaultCompressed,
                originalArchive.Header.FileFlags,
                originalArchive.Header.EmbedFileNames);

            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("[green]Adding files[/]", maxValue: extractedHashes.Count);
                    var count = 0;

                    foreach (var (relativePath, _) in extractedHashes)
                    {
                        var fullPath = Path.Combine(extractDir, relativePath);
                        var data = await File.ReadAllBytesAsync(fullPath);
                        writer.AddFile(relativePath, data);
                        task.Value = ++count;
                    }
                });

            writer.Write(repackedPath);
            AnsiConsole.MarkupLine("  Repacked to: {0}", repackedPath);
            AnsiConsole.WriteLine();

            // Step 5: Parse repacked BSA and compare
            AnsiConsole.MarkupLine("[yellow]Step 5:[/] Comparing original vs repacked...");
            var repackedArchive = BsaParser.Parse(repackedPath);

            var issues = new List<string>();

            // Compare header fields
            if (originalArchive.Header.Version != repackedArchive.Header.Version)
            {
                issues.Add($"Version mismatch: {originalArchive.Header.Version} vs {repackedArchive.Header.Version}");
            }

            // Account for extraction failures in file count comparison
            var expectedFileCount = originalArchive.Header.FileCount - (uint)failedCount;
            if (expectedFileCount != repackedArchive.Header.FileCount)
            {
                issues.Add(
                    $"File count mismatch: expected {expectedFileCount} (original {originalArchive.Header.FileCount} - {failedCount} failed), got {repackedArchive.Header.FileCount}");
            }

            // Folder count might differ if all files in a folder failed to extract
            var expectedMinFolderCount = extractedHashes.Select(kv =>
            {
                var lastSlash = kv.Key.LastIndexOf('\\');
                return lastSlash >= 0 ? kv.Key[..lastSlash] : "";
            }).Distinct().Count();
            if (repackedArchive.Header.FolderCount < expectedMinFolderCount)
            {
                issues.Add(
                    $"Folder count too low: {repackedArchive.Header.FolderCount} vs expected at least {expectedMinFolderCount}");
            }

            // Compare file content by extracting repacked and hashing
            AnsiConsole.MarkupLine("[yellow]Step 6:[/] Verifying repacked content...");
            var repackExtractDir = Path.Combine(tempDir, "repacked_extracted");
            Directory.CreateDirectory(repackExtractDir);

            using var repackExtractor = new BsaExtractor(repackedPath);
            var repackExtractResults = await repackExtractor.ExtractAllAsync(repackExtractDir, true);

            var repackedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var outputPath in repackExtractResults.Where(r => r.Success).Select(result => result.OutputPath))
            {
                var relativePath = Path.GetRelativePath(repackExtractDir, outputPath)
                    .Replace('/', '\\').ToLowerInvariant();
                var hash = await ComputeFileHashAsync(outputPath);
                repackedHashes[relativePath] = hash;
            }

            // Compare hashes
            var missingInRepacked = extractedHashes.Keys.Except(repackedHashes.Keys).ToList();
            var extraInRepacked = repackedHashes.Keys.Except(extractedHashes.Keys).ToList();
            var hashMismatches = new List<string>();

            foreach (var (path, originalHash) in extractedHashes)
            {
                if (repackedHashes.TryGetValue(path, out var repackedHash) && originalHash != repackedHash)
                {
                    hashMismatches.Add(path);
                }
            }

            if (missingInRepacked.Count > 0)
            {
                issues.Add($"Missing in repacked: {missingInRepacked.Count} files");
                if (verbose)
                {
                    foreach (var path in missingInRepacked.Take(10))
                    {
                        issues.Add($"  - {path}");
                    }
                }
            }

            if (extraInRepacked.Count > 0)
            {
                issues.Add($"Extra in repacked: {extraInRepacked.Count} files");
                if (verbose)
                {
                    foreach (var path in extraInRepacked.Take(10))
                    {
                        issues.Add($"  + {path}");
                    }
                }
            }

            if (hashMismatches.Count > 0)
            {
                issues.Add($"Content hash mismatches: {hashMismatches.Count} files");
                if (verbose)
                {
                    foreach (var path in hashMismatches.Take(10))
                    {
                        issues.Add($"  \u2260 {path}");
                    }
                }
            }

            AnsiConsole.WriteLine();

            // Report results
            var extractionIssues = failedCount > 0;
            var roundTripIssues = issues.Count > 0;

            if (!roundTripIssues && !extractionIssues)
            {
                AnsiConsole.MarkupLine("[green]\u2713 Validation PASSED[/]");
                AnsiConsole.MarkupLine("  Round-trip produces identical content.");
                AnsiConsole.MarkupLine("  Files: {0:N0}", extractedHashes.Count);
            }
            else if (!roundTripIssues && extractionIssues)
            {
                AnsiConsole.MarkupLine("[yellow]\u26a0 Validation PARTIAL[/]");
                AnsiConsole.MarkupLine("  Round-trip OK for {0:N0}/{1:N0} files.", extractedHashes.Count,
                    originalArchive.Header.FileCount);
                AnsiConsole.MarkupLine("  [yellow]{0:N0} file(s) failed extraction (extractor bug - see above)[/]",
                    failedCount);
            }
            else if (roundTripIssues && extractionIssues)
            {
                AnsiConsole.MarkupLine("[red]\u2717 Validation FAILED[/]");
                AnsiConsole.MarkupLine("  {0} round-trip issues found:", issues.Count);
                foreach (var issue in issues)
                {
                    AnsiConsole.MarkupLine("[red]  \u2022 {0}[/]", issue);
                }

                AnsiConsole.MarkupLine("  [yellow]Plus {0} extraction failures (see above)[/]", failedCount);
            }
            else
            {
                AnsiConsole.MarkupLine("[red]\u2717 Validation FAILED[/]");
                AnsiConsole.MarkupLine("  {0} issues found:", issues.Count);
                foreach (var issue in issues)
                {
                    AnsiConsole.MarkupLine("[red]  \u2022 {0}[/]", issue);
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]Error during validation:[/] {0}", ex.Message);
            if (verbose)
            {
                AnsiConsole.WriteException(ex);
            }
        }
        finally
        {
            // Cleanup
            if (!keepTemp && Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, true);
                    AnsiConsole.MarkupLine("[dim]Cleaned up temp files.[/]");
                }
                catch
                {
                    AnsiConsole.MarkupLine("[yellow]Warning:[/] Could not clean up temp dir: {0}", tempDir);
                }
            }
            else if (keepTemp)
            {
                AnsiConsole.MarkupLine("[dim]Temp files kept at:[/] {0}", tempDir);
            }
        }
    }

    private static async Task<string> ComputeFileHashAsync(string filePath)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }
}
