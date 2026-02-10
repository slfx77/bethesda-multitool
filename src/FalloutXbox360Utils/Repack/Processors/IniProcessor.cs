using System.Text;
using System.Text.RegularExpressions;

namespace FalloutXbox360Utils.Repack.Processors;

/// <summary>
///     Processor for generating hybrid INI files.
///     Takes Xbox 360 INI settings and converts references for PC compatibility:
///     - DDX → DDS (textures)
///     - XMA → MP3 (loose Music folder) or WAV (BSA content)
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
        var processed = 0;

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

        var totalItems = options.UpdateUserIni ? 2 : 1;

        progress.Report(new RepackerProgress
        {
            Phase = RepackPhase.Ini,
            Message = "Processing Fallout.ini",
            CurrentItem = "Fallout.ini",
            ItemsProcessed = 0,
            TotalItems = totalItems
        });

        try
        {
            // Read source INI
            var iniContent = await File.ReadAllTextAsync(sourceIniPath, cancellationToken);

            // Get list of BSA files from source for SArchiveList
            var sourceBsaFiles = GetSourceBsaFiles(options.SourceFolder);

            // Apply conversions
            var convertedContent = ConvertIniContent(iniContent, sourceBsaFiles);

            // Ensure output directory exists
            // Output to game root as Fallout_default.ini (matches PC convention)
            Directory.CreateDirectory(options.OutputFolder);

            // Write converted INI as Fallout_default.ini
            // Users can copy this to My Games\FalloutNV\Fallout.ini
            var outputIniPath = Path.Combine(options.OutputFolder, "Fallout_default.ini");
            await File.WriteAllTextAsync(outputIniPath, convertedContent, Encoding.UTF8, cancellationToken);
            processed++;

            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Ini,
                Message = "Generated hybrid Fallout_default.ini for PC",
                ItemsProcessed = processed,
                TotalItems = totalItems
            });

            // Update user INI if requested
            if (options.UpdateUserIni)
            {
                progress.Report(new RepackerProgress
                {
                    Phase = RepackPhase.Ini,
                    Message = "Updating user Fallout.ini",
                    CurrentItem = "User Fallout.ini",
                    ItemsProcessed = processed,
                    TotalItems = totalItems
                });

                var userIniResult = await UpdateUserIniAsync(sourceBsaFiles, cancellationToken);
                if (userIniResult != null)
                {
                    progress.Report(new RepackerProgress
                    {
                        Phase = RepackPhase.Ini,
                        Message = userIniResult,
                        ItemsProcessed = ++processed,
                        TotalItems = totalItems
                    });
                }
            }

            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Ini,
                Message = options.UpdateUserIni
                    ? "Generated Fallout_default.ini and updated user INI"
                    : "Generated hybrid Fallout_default.ini for PC",
                ItemsProcessed = processed,
                TotalItems = totalItems,
                IsComplete = true,
                Success = true
            });

            return processed;
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
    ///     Gets the list of BSA files from the source Data folder.
    /// </summary>
    private static List<string> GetSourceBsaFiles(string sourceFolder)
    {
        var dataPath = Path.Combine(sourceFolder, "Data");
        if (!Directory.Exists(dataPath))
        {
            return [];
        }

        return Directory.GetFiles(dataPath, "*.bsa", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(n => n != null)
            .Cast<string>()
            .ToList();
    }

    /// <summary>
    ///     Gets the path to the user's Fallout.ini in My Games folder.
    /// </summary>
    private static string GetUserIniPath()
    {
        var myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(myDocs, "My Games", "FalloutNV", "Fallout.ini");
    }

    /// <summary>
    ///     Updates the user's Fallout.ini with the converted BSA list.
    ///     Creates a backup before modification.
    /// </summary>
    private static async Task<string?> UpdateUserIniAsync(List<string> sourceBsaFiles,
        CancellationToken cancellationToken)
    {
        var userIniPath = GetUserIniPath();
        var userIniDir = Path.GetDirectoryName(userIniPath);

        if (userIniDir == null)
        {
            return "Could not determine user INI directory";
        }

        // Create directory if it doesn't exist
        Directory.CreateDirectory(userIniDir);

        if (!File.Exists(userIniPath))
        {
            return "User Fallout.ini not found - run the game once to generate it";
        }

        // Create backup
        var backupPath = userIniPath + ".backup";
        File.Copy(userIniPath, backupPath, true);

        // Read existing content
        var content = await File.ReadAllTextAsync(userIniPath, cancellationToken);

        // Update SArchiveList
        var updatedContent = UpdateSArchiveList(content, sourceBsaFiles);

        if (updatedContent == content)
        {
            return "User INI already up to date";
        }

        // Write updated content
        await File.WriteAllTextAsync(userIniPath, updatedContent, Encoding.UTF8, cancellationToken);

        return $"Updated user INI (backup: {Path.GetFileName(backupPath)})";
    }

    /// <summary>
    ///     Updates the SArchiveList in INI content to include all BSA files from the source.
    /// </summary>
    private static string UpdateSArchiveList(string content, List<string> sourceBsaFiles)
    {
        if (sourceBsaFiles.Count == 0)
        {
            return content;
        }

        var lines = content.Split('\n');
        var result = new StringBuilder();
        var foundArchiveList = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            if (IsSArchiveListLine(line))
            {
                foundArchiveList = true;
                var updatedLine = MergeBsaListIntoLine(line, sourceBsaFiles);
                if (updatedLine != null)
                {
                    result.AppendLine(updatedLine);
                    continue;
                }
            }

            result.AppendLine(line);
        }

        return foundArchiveList
            ? result.ToString()
            : InsertSArchiveListIntoContent(result.ToString(), sourceBsaFiles);
    }

    private static bool IsSArchiveListLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("SArchiveList", StringComparison.OrdinalIgnoreCase);
    }

    private static string? MergeBsaListIntoLine(string line, List<string> sourceBsaFiles)
    {
        var equalsIndex = line.IndexOf('=');
        if (equalsIndex < 0)
        {
            return null;
        }

        var key = line[..equalsIndex];
        var existingValue = line[(equalsIndex + 1)..];

        var existingBsas = existingValue.Split(',')
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        existingBsas.UnionWith(sourceBsaFiles);

        var newValue = string.Join(", ", existingBsas.OrderBy(s => s));
        return $"{key}={newValue}";
    }

    private static string InsertSArchiveListIntoContent(string content, List<string> sourceBsaFiles)
    {
        var newBsaList = string.Join(", ", sourceBsaFiles.OrderBy(s => s));
        var archiveIndex = content.IndexOf("[Archive]", StringComparison.OrdinalIgnoreCase);

        if (archiveIndex < 0)
        {
            return content;
        }

        var nextSectionIndex = content.IndexOf("\n[", archiveIndex + 9, StringComparison.Ordinal);
        var insertIndex = nextSectionIndex >= 0 ? nextSectionIndex : content.Length;

        return content[..insertIndex] + $"SArchiveList={newBsaList}\r\n" + content[insertIndex..];
    }

    /// <summary>
    ///     Converts Xbox 360 INI content to PC-compatible format.
    /// </summary>
    private static string ConvertIniContent(string content, List<string> sourceBsaFiles)
    {
        var lines = content.Split('\n');
        var result = new StringBuilder();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var convertedLine = ConvertIniLine(line);
            result.AppendLine(convertedLine);
        }

        // Ensure all source BSA files are in SArchiveList
        var convertedContent = result.ToString();
        return UpdateSArchiveList(convertedContent, sourceBsaFiles);
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
        // XMA → WAV for BSA content references
        // Heuristic: Music folder paths typically contain "Music\" or "music\"
        if (value.Contains("Music", StringComparison.OrdinalIgnoreCase) ||
            value.Contains(@"\Music\", StringComparison.OrdinalIgnoreCase))
        {
            value = XmaExtensionRegex().Replace(value, ".mp3");
        }
        else
        {
            // For general audio references (BSA content), use WAV
            // However, most BSA audio is WAV which doesn't need conversion
            // Only XMA files get converted to WAV
            value = XmaExtensionRegex().Replace(value, ".wav");
        }

        return value;
    }

    [GeneratedRegex(@"\.ddx\b", RegexOptions.IgnoreCase)]
    private static partial Regex DdxExtensionRegex();

    [GeneratedRegex(@"\.xma\b", RegexOptions.IgnoreCase)]
    private static partial Regex XmaExtensionRegex();
}
