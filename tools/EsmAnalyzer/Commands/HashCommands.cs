using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Commands for hashing files to validate conversion stability.
/// </summary>
public static class HashCommands
{
    public static Command CreateHashCommand()
    {
        var command = new Command("hash", "Compute a file hash (default: SHA256)");

        var fileArg = new Argument<string>("file") { Description = "Path to the file" };
        var algoOption = new Option<string>("-a", "--algo")
        {
            Description = "Hash algorithm: sha256|sha1|md5",
            DefaultValueFactory = _ => "sha256"
        };
        var outputOption = new Option<string?>("-o", "--output")
        {
            Description = "Optional output file to write the hash"
        };

        command.Arguments.Add(fileArg);
        command.Options.Add(algoOption);
        command.Options.Add(outputOption);

        command.SetAction(parseResult => ComputeHash(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(algoOption)!,
            parseResult.GetValue(outputOption)));

        return command;
    }

    public static Command CreateHashCompareCommand()
    {
        var command = new Command("hash-compare", "Compare hashes of two files (default: SHA256)");

        var leftArg = new Argument<string>("left") { Description = "Path to the first file" };
        var rightArg = new Argument<string>("right") { Description = "Path to the second file" };
        var algoOption = new Option<string>("-a", "--algo")
        {
            Description = "Hash algorithm: sha256|sha1|md5",
            DefaultValueFactory = _ => "sha256"
        };

        command.Arguments.Add(leftArg);
        command.Arguments.Add(rightArg);
        command.Options.Add(algoOption);

        command.SetAction(parseResult => CompareHashes(
            parseResult.GetValue(leftArg)!,
            parseResult.GetValue(rightArg)!,
            parseResult.GetValue(algoOption)!));

        return command;
    }

    private static int ComputeHash(string filePath, string algo, string? outputPath)
    {
        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {filePath}");
            return 1;
        }

        var hashBytes = HashFile(filePath, algo, out var algoName);
        if (hashBytes == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Unsupported algorithm: {algo}");
            return 1;
        }

        var hashHex = ToHex(hashBytes);
        AnsiConsole.MarkupLine($"[cyan]{algoName}[/] {Path.GetFileName(filePath)}: {hashHex}");

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            File.WriteAllText(outputPath!, $"{algoName} {hashHex}  {Path.GetFileName(filePath)}\n");
            AnsiConsole.MarkupLine($"[grey]Wrote hash to {outputPath}[/]");
        }

        return 0;
    }

    private static int CompareHashes(string leftPath, string rightPath, string algo)
    {
        if (!File.Exists(leftPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {leftPath}");
            return 1;
        }

        if (!File.Exists(rightPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {rightPath}");
            return 1;
        }

        var leftHash = HashFile(leftPath, algo, out var algoName);
        var rightHash = HashFile(rightPath, algo, out _);
        if (leftHash == null || rightHash == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Unsupported algorithm: {algo}");
            return 1;
        }

        var leftHex = ToHex(leftHash);
        var rightHex = ToHex(rightHash);

        var match = leftHex.Equals(rightHex, StringComparison.OrdinalIgnoreCase);
        AnsiConsole.MarkupLine($"[cyan]{algoName}[/] match: {(match ? "[green]YES[/]" : "[red]NO[/]")}");
        AnsiConsole.MarkupLine($"Left : {leftHex}");
        AnsiConsole.MarkupLine($"Right: {rightHex}");

        return match ? 0 : 1;
    }

    private static byte[]? HashFile(string filePath, string algo, out string algoName)
    {
        algoName = algo.ToUpperInvariant();
        using var stream = File.OpenRead(filePath);
        HashAlgorithm? hasher = algo.ToLowerInvariant() switch
        {
            "sha256" => SHA256.Create(),
            "sha1" => SHA1.Create(),
            "md5" => MD5.Create(),
            _ => null
        };

        if (hasher == null) return null;
        using (hasher)
        {
            return hasher.ComputeHash(stream);
        }
    }

    private static string ToHex(byte[] bytes)
    {
        return string.Concat(bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }
}
