using BethesdaMultitool.Core.Repack.Processors;

namespace BethesdaMultitool.Core.Repack;

/// <summary>
///     Service that orchestrates the Xbox 360 to PC conversion process.
/// </summary>
public static class RepackerService
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
    public static async Task<RepackResult> RepackAsync(
        RepackerOptions options,
        IProgress<RepackerProgress> progress,
        CancellationToken cancellationToken = default)
    {
        var result = new RepackResult();
        // Each processor already reports its own completion with Success set; nothing used to read
        // it, so a phase that converted nothing still produced a successful run. Observing that
        // stream is enough to propagate it without changing IRepackProcessor's signature.
        var observed = new PhaseFailureObserver(progress);
        progress = observed;

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

            // Unpack the console interface container into the loose menus tree the PC engine
            // reads. Nothing else supplies menus — the 360 Misc.bsa carries none.
            if (options.ProcessMenus)
            {
                var menuProcessor = new MenuProcessor();
                result.MenuFilesProcessed = await menuProcessor.ProcessAsync(options, progress, cancellationToken);
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

            result.Success = observed.Failures.Count == 0;
            result.Error = observed.Failures.Count == 0 ? null : string.Join("; ", observed.Failures);

            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Complete,
                Message = result.Success ? "Repacking complete" : "Repacking finished with failures",
                IsComplete = true,
                Success = result.Success,
                Error = result.Error
            });
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Error = "Operation canceled";

            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Complete,
                Message = "Operation canceled",
                IsComplete = true,
                Success = false,
                Error = "Canceled"
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

    /// <summary>Forwards progress to the caller while noticing any phase that reported failure.</summary>
    /// <remarks>
    ///     Every processor already publishes one completion report carrying its own success, and
    ///     nothing read it: a phase that converted nothing still produced a successful run, which is
    ///     how the retail update.esp went missing from converted builds without any caller being able
    ///     to tell. Reporting is synchronous here on purpose - a <c>Progress&lt;T&gt;</c> would post to
    ///     a synchronization context and could land after the result is read.
    /// </remarks>
    /// <param name="inner">The caller's progress, which still receives every report unchanged.</param>
    private sealed class PhaseFailureObserver(IProgress<RepackerProgress> inner) : IProgress<RepackerProgress>
    {
        /// <summary>Gets one message per phase that completed unsuccessfully.</summary>
        public List<string> Failures { get; } = [];

        /// <summary>Records a failed phase completion and forwards the report untouched.</summary>
        /// <param name="value">One progress report from a processor.</param>
        public void Report(RepackerProgress value)
        {
            if (value is { IsComplete: true, Success: false } && value.Phase != RepackPhase.Complete)
                Failures.Add(value.Message ?? value.Error ?? (value.Phase + " failed"));

            inner.Report(value);
        }
    }
}
