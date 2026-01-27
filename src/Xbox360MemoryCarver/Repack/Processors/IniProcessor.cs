using System.Text;
using System.Text.RegularExpressions;

namespace Xbox360MemoryCarver.Repack.Processors;

/// <summary>
///     Processor for generating hybrid INI files.
///     Takes Xbox 360 INI settings and converts references for PC compatibility:
///     - DDX → DDS (textures)
///     - XMA → MP3 (loose Music folder) or OGG (BSA content)
///     - Fixes known Xbox-specific settings
/// </summary>
public sealed partial class IniProcessor : IRepackProcessor
{
    public string Name => "INI";

    public async Task<int> ProcessAsync(
        RepackerOptions options,
        IProgress<RepackerProgress> progress,
        CancellationToken cancellationToken)
    {
        // Look for Fallout.ini in the source folder (game root, not Data folder)
        var sourceIniPath = Path.Combine(options.SourceFolder, "Fallout.ini");

        if (!File.Exists(sourceIniPath))
        {
            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Ini,
                Message = "Fallout.ini not found in source folder, skipping",
                IsComplete = true,
                Success = true
            });
            return 0;
        }

        progress.Report(new RepackerProgress
        {
            Phase = RepackPhase.Ini,
            Message = "Processing Fallout.ini",
            CurrentItem = "Fallout.ini",
            ItemsProcessed = 0,
            TotalItems = 1
        });

        try
        {
            // Read source INI
            var iniContent = await File.ReadAllTextAsync(sourceIniPath, cancellationToken);

            // Apply conversions
            var convertedContent = ConvertIniContent(iniContent);

            // Ensure output directory exists
            // Output to game root as Fallout_default.ini (matches PC convention)
            Directory.CreateDirectory(options.OutputFolder);

            // Write converted INI as Fallout_default.ini
            // Users can copy this to My Games\FalloutNV\Fallout.ini
            var outputIniPath = Path.Combine(options.OutputFolder, "Fallout_default.ini");
            await File.WriteAllTextAsync(outputIniPath, convertedContent, Encoding.UTF8, cancellationToken);

            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Ini,
                Message = "Generated hybrid Fallout_default.ini for PC",
                ItemsProcessed = 1,
                TotalItems = 1,
                IsComplete = true,
                Success = true
            });

            return 1;
        }
        catch (Exception ex)
        {
            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Ini,
                Message = "Failed to process INI",
                Error = ex.Message,
                IsComplete = true,
                Success = false
            });
            return 0;
        }
    }

    /// <summary>
    ///     Converts Xbox 360 INI content to PC-compatible format.
    /// </summary>
    private static string ConvertIniContent(string content)
    {
        var lines = content.Split('\n');
        var result = new StringBuilder();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var convertedLine = ConvertIniLine(line);
            result.AppendLine(convertedLine);
        }

        return result.ToString();
    }

    /// <summary>
    ///     Converts a single INI line, handling specific settings and file extension replacements.
    /// </summary>
    private static string ConvertIniLine(string line)
    {
        // Skip empty lines and comments
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith(';'))
        {
            return line;
        }

        // Check for key=value pattern
        var equalsIndex = line.IndexOf('=');
        if (equalsIndex < 0)
        {
            return line; // Section header or malformed line
        }

        var key = line[..equalsIndex].Trim();
        var value = line[(equalsIndex + 1)..];

        // Apply specific setting overrides
        switch (key.ToLowerInvariant())
        {
            // Fix intro movie to known working PC file
            case "sintromovie":
                // Xbox has "Fallout INTRO Vsk.bik" which doesn't exist on PC
                // PC uses "FNV_FE_Intro.bik"
                if (value.Contains("Vsk", StringComparison.OrdinalIgnoreCase))
                {
                    return $"{key}=FNV_FE_Intro.bik";
                }
                break;

            // Replace Xbox-specific thread assignments (PC uses different threading)
            case "inumhwthreads":
            case "iaithread1hwthread":
            case "iaithread2hwthread":
            case "iaudiohwthread":
                // Keep as-is, game will adjust based on actual hardware
                break;

            // Ensure bUseMyGamesDirectory is enabled for PC (saves go to My Games folder)
            case "busemygamesdirectory":
                return $"{key}=1";

            // SArchiveList needs to match our converted BSA names
            // The BSA names stay the same, just content is converted
            case "sarchivelist":
                // Keep existing, but ensure Voices2-4 are included
                if (!value.Contains("Voices2", StringComparison.OrdinalIgnoreCase))
                {
                    // Append missing voice BSAs
                    value = value.TrimEnd();
                    if (!value.EndsWith(','))
                    {
                        value += ", ";
                    }
                    value += "Fallout - Voices2.bsa, Fallout - Voices3.bsa, Fallout - Voices4.bsa";
                    return $"{key}={value}";
                }
                break;
        }

        // Apply general file extension replacements in values
        var convertedValue = ConvertFileReferences(value);

        if (convertedValue != value)
        {
            return $"{key}={convertedValue}";
        }

        return line;
    }

    /// <summary>
    ///     Converts file extension references in INI values.
    /// </summary>
    private static string ConvertFileReferences(string value)
    {
        // Replace DDX → DDS (textures)
        value = DdxExtensionRegex().Replace(value, ".dds");

        // Replace XMA → MP3 for Music folder references
        // XMA → OGG for BSA content references
        // Heuristic: Music folder paths typically contain "Music\" or "music\"
        if (value.Contains("Music", StringComparison.OrdinalIgnoreCase) ||
            value.Contains(@"\Music\", StringComparison.OrdinalIgnoreCase))
        {
            value = XmaExtensionRegex().Replace(value, ".mp3");
        }
        else
        {
            // For general audio references (BSA content), use OGG
            // However, most BSA audio is WAV which doesn't need conversion
            // Only XMA files get converted to OGG
            value = XmaExtensionRegex().Replace(value, ".ogg");
        }

        return value;
    }

    [GeneratedRegex(@"\.ddx\b", RegexOptions.IgnoreCase)]
    private static partial Regex DdxExtensionRegex();

    [GeneratedRegex(@"\.xma\b", RegexOptions.IgnoreCase)]
    private static partial Regex XmaExtensionRegex();
}
