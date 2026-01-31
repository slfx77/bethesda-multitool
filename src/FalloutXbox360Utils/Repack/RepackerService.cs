using FalloutXbox360Utils.Repack.Processors;

namespace FalloutXbox360Utils.Repack;

/// <summary>
///     Service that orchestrates the Xbox 360 to PC conversion process.
/// </summary>
public sealed class RepackerService
{
    /// <summary>
    ///     Validates that the source folder is a valid Xbox 360 FalloutNV installation.
    /// </summary>
    public static ValidationResult ValidateSourceFolder(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
        {
            return new ValidationResult(false, "Folder does not exist");
        }

        // Check for default.xex (Xbox 360 executable)
        var xexPath = Path.Combine(folderPath, "default.xex");
        if (!File.Exists(xexPath))
        {
            return new ValidationResult(false, "default.xex not found - not an Xbox 360 game folder");
        }

        // Check for Data/FalloutNV.esm
        var esmPath = Path.Combine(folderPath, "Data", "FalloutNV.esm");
        if (!File.Exists(esmPath))
        {
            return new ValidationResult(false, "Data/FalloutNV.esm not found - not a Fallout: New Vegas installation");
        }

        return new ValidationResult(true, "Valid Xbox 360 Fallout: New Vegas installation");
    }

    /// <summary>
    ///     Gets information about the source folder contents.
    /// </summary>
    public static SourceInfo GetSourceInfo(string folderPath)
    {
        var dataPath = Path.Combine(folderPath, "Data");
        var videoPath = Path.Combine(dataPath, "Video");
        var musicPath = Path.Combine(dataPath, "Music");

        return new SourceInfo
        {
            VideoFiles = Directory.Exists(videoPath)
                ? Directory.GetFiles(videoPath, "*.bik", SearchOption.AllDirectories).Length
                : 0,
            MusicFiles = Directory.Exists(musicPath)
                ? Directory.GetFiles(musicPath, "*.xma", SearchOption.AllDirectories).Length
                : 0,
            BsaFiles = Directory.Exists(dataPath)
                ? Directory.GetFiles(dataPath, "*.bsa", SearchOption.TopDirectoryOnly).Length
                : 0,
            EsmFiles = Directory.Exists(dataPath)
                ? Directory.GetFiles(dataPath, "*.esm", SearchOption.TopDirectoryOnly).Length
                : 0,
            EspFiles = Directory.Exists(dataPath)
                ? Directory.GetFiles(dataPath, "*.esp", SearchOption.TopDirectoryOnly).Length
                : 0
        };
    }

    /// <summary>
    ///     Runs the full repacking process.
    /// </summary>
    public async Task<RepackResult> RepackAsync(
        RepackerOptions options,
        IProgress<RepackerProgress> progress,
        CancellationToken cancellationToken = default)
    {
        var result = new RepackResult();

        try
        {
            // Validate
            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Validating,
                Message = "Validating source folder..."
            });

            var validation = ValidateSourceFolder(options.SourceFolder);
            if (!validation.IsValid)
            {
                return new RepackResult
                {
                    Success = false,
                    Error = validation.Message
                };
            }

            // Create output folder
            Directory.CreateDirectory(options.OutputFolder);

            // Process Video
            if (options.ProcessVideo)
            {
                var videoProcessor = new VideoProcessor();
                result.VideoFilesProcessed = await videoProcessor.ProcessAsync(options, progress, cancellationToken);
            }

            // Process Music
            if (options.ProcessMusic)
            {
                var musicProcessor = new MusicProcessor();
                result.MusicFilesProcessed = await musicProcessor.ProcessAsync(options, progress, cancellationToken);
            }

            // Process BSA
            if (options.ProcessBsa)
            {
                var bsaProcessor = new BsaProcessor();
                result.BsaFilesProcessed = await bsaProcessor.ProcessAsync(options, progress, cancellationToken);
            }

            // Process ESM
            if (options.ProcessEsm)
            {
                var esmProcessor = new EsmProcessor();
                result.EsmFilesProcessed = await esmProcessor.ProcessAsync(options, progress, cancellationToken);
            }

            // Process ESP
            if (options.ProcessEsp)
            {
                var espProcessor = new EsmProcessor(true);
                result.EspFilesProcessed = await espProcessor.ProcessAsync(options, progress, cancellationToken);
            }

            // Process INI
            if (options.ProcessIni)
            {
                var iniProcessor = new IniProcessor();
                result.IniFilesProcessed = await iniProcessor.ProcessAsync(options, progress, cancellationToken);
            }

            result.Success = true;

            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Complete,
                Message = "Repacking complete",
                IsComplete = true,
                Success = true
            });
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Error = "Operation cancelled";

            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Complete,
                Message = "Operation cancelled",
                IsComplete = true,
                Success = false,
                Error = "Cancelled"
            });
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;

            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Complete,
                Message = $"Error: {ex.Message}",
                IsComplete = true,
                Success = false,
                Error = ex.Message
            });
        }

        return result;
    }
}
