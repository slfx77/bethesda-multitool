using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;

namespace EsmAnalyzer.Helpers;

/// <summary>
///     Result of loading and validating an ESM file.
/// </summary>
public sealed class EsmFileLoadResult
{
    public required byte[] Data { get; init; }
    public required EsmFileHeader Header { get; init; }
    public required MainRecordHeader Tes4Header { get; init; }
    public required int FirstGrupOffset { get; init; }
    public required string FilePath { get; init; }
    public bool IsBigEndian => Header.IsBigEndian;
}

/// <summary>
///     Shared file loading and validation logic for ESM commands.
/// </summary>
public static class EsmFileLoader
{
    /// <summary>
    ///     Loads and validates an ESM file, printing status messages.
    /// </summary>
    /// <returns>Null if loading fails, otherwise the loaded file data.</returns>
    public static EsmFileLoadResult? Load(string filePath, bool printStatus = true)
    {
        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {filePath}");
            return null;
        }

        var data = File.ReadAllBytes(filePath);
        var header = EsmParser.ParseFileHeader(data);

        if (header == null)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Failed to parse ESM header");
            return null;
        }

        var tes4Header = EsmParser.ParseRecordHeader(data, header.IsBigEndian);
        if (tes4Header == null)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Failed to parse TES4 record header");
            return null;
        }

        var firstGrupOffset = EsmParser.MainRecordHeaderSize + (int)tes4Header.DataSize;

        if (printStatus)
            AnsiConsole.MarkupLine(
                $"Endianness: {(header.IsBigEndian ? "[yellow]Big-endian (Xbox 360)[/]" : "[green]Little-endian (PC)[/]")}");

        return new EsmFileLoadResult
        {
            Data = data,
            Header = header,
            Tes4Header = tes4Header,
            FirstGrupOffset = firstGrupOffset,
            FilePath = filePath
        };
    }

    /// <summary>
    ///     Loads two ESM files for comparison (Xbox 360 and PC).
    /// </summary>
    public static (EsmFileLoadResult? xbox, EsmFileLoadResult? pc) LoadPair(string xboxPath, string pcPath,
        bool printStatus = true)
    {
        if (!File.Exists(xboxPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Xbox 360 file not found: {xboxPath}");
            return (null, null);
        }

        if (!File.Exists(pcPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] PC file not found: {pcPath}");
            return (null, null);
        }

        var xbox = Load(xboxPath, false);
        var pc = Load(pcPath, false);

        if (printStatus && xbox != null && pc != null)
        {
            AnsiConsole.MarkupLine(
                $"Xbox 360: {Path.GetFileName(xboxPath)} ({(xbox.IsBigEndian ? "Big-endian" : "Little-endian")})");
            AnsiConsole.MarkupLine(
                $"PC:       {Path.GetFileName(pcPath)} ({(pc.IsBigEndian ? "Big-endian" : "Little-endian")})");
        }

        return (xbox, pc);
    }

    /// <summary>
    ///     Parses a hex offset string (with optional 0x prefix).
    /// </summary>
    public static int? ParseOffset(string? offsetStr)
    {
        if (string.IsNullOrWhiteSpace(offsetStr)) return null;

        try
        {
            return Convert.ToInt32(offsetStr.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]WARN:[/] Invalid offset '{offsetStr}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Parses a FormID from hex string.
    /// </summary>
    public static uint? ParseFormId(string? formIdStr)
    {
        if (string.IsNullOrWhiteSpace(formIdStr)) return null;

        try
        {
            return Convert.ToUInt32(formIdStr.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]WARN:[/] Invalid FormID '{formIdStr}': {ex.Message}");
            return null;
        }
    }
}