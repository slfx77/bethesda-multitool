using System.Diagnostics;

namespace BethesdaMultitool.Core.Utils;

internal enum AtomicFileWritePhase
{
    CompanionStagingMove,
    TargetPublishMove
}

/// <summary>
///     Best-effort diagnostic timing for one actual filesystem move performed by
///     <see cref="AtomicFileWriter" />. A failed move is reported before its original exception is
///     rethrown; companion restoration and temporary-file cleanup are deliberately outside the sample.
/// </summary>
internal readonly record struct AtomicFileWritePhaseTiming(
    AtomicFileWritePhase Phase,
    double WallMilliseconds,
    bool Succeeded);

/// <summary>
///     Publishes one file through a same-directory temporary path. The previous target remains intact
///     until the writer finishes and cancellation is checked, then one atomic rename replaces it.
/// </summary>
internal static class AtomicFileWriter
{
    internal static async Task WriteAsync(
        string targetPath,
        Func<string, CancellationToken, Task> writeTemporaryAsync,
        string? companionPathToInvalidate = null,
        Action<AtomicFileWritePhaseTiming>? phaseObserver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(writeTemporaryAsync);

        var fullTargetPath = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullTargetPath);
        var fileName = Path.GetFileName(fullTargetPath);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException(
                "Atomic output requires a named file in a concrete directory.",
                nameof(targetPath));
        }

        cancellationToken.ThrowIfCancellationRequested();
        string? temporaryPath = Path.Combine(
            directory,
            $".{fileName}.{Guid.NewGuid():N}.tmp");
        string? companionBackupPath = null;
        string? fullCompanionPath = null;
        try
        {
            await writeTemporaryAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(temporaryPath))
            {
                throw new FileNotFoundException(
                    "The atomic-output writer did not create its temporary file.",
                    temporaryPath);
            }

            if (!string.IsNullOrWhiteSpace(companionPathToInvalidate))
            {
                fullCompanionPath = Path.GetFullPath(companionPathToInvalidate);
                if (!string.Equals(
                        Path.GetDirectoryName(fullCompanionPath),
                        directory,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        "An atomically invalidated companion must share the target directory.",
                        nameof(companionPathToInvalidate));
                }
                if (string.Equals(fullCompanionPath, fullTargetPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        "The companion path must differ from the target path.",
                        nameof(companionPathToInvalidate));
                }

                if (File.Exists(fullCompanionPath))
                {
                    companionBackupPath = Path.Combine(
                        directory,
                        $".{Path.GetFileName(fullCompanionPath)}.{Guid.NewGuid():N}.tmp");
                    var stagingStarted = StartMoveTiming(phaseObserver);
                    try
                    {
                        File.Move(fullCompanionPath, companionBackupPath);
                    }
                    catch
                    {
                        ObserveMove(
                            phaseObserver,
                            AtomicFileWritePhase.CompanionStagingMove,
                            stagingStarted,
                            succeeded: false);
                        throw;
                    }
                    ObserveMove(
                        phaseObserver,
                        AtomicFileWritePhase.CompanionStagingMove,
                        stagingStarted,
                        succeeded: true);
                }
            }

            var publicationStarted = StartMoveTiming(phaseObserver);
            try
            {
                File.Move(temporaryPath, fullTargetPath, overwrite: true);
                ObserveMove(
                    phaseObserver,
                    AtomicFileWritePhase.TargetPublishMove,
                    publicationStarted,
                    succeeded: true);
            }
            catch
            {
                ObserveMove(
                    phaseObserver,
                    AtomicFileWritePhase.TargetPublishMove,
                    publicationStarted,
                    succeeded: false);
                if (companionBackupPath is not null && fullCompanionPath is not null)
                {
                    File.Move(companionBackupPath, fullCompanionPath, overwrite: false);
                }
                throw;
            }
            temporaryPath = null;
            if (companionBackupPath is not null)
            {
                TryDelete(companionBackupPath);
            }
        }
        finally
        {
            if (temporaryPath is not null)
            {
                TryDelete(temporaryPath);
            }
            // If restoring an invalidated companion itself failed, deliberately retain its hidden
            // backup for recovery instead of deleting the last intact copy in this finally block.
        }
    }

    private static long StartMoveTiming(Action<AtomicFileWritePhaseTiming>? phaseObserver) =>
        phaseObserver is null ? 0 : Stopwatch.GetTimestamp();

    private static void ObserveMove(
        Action<AtomicFileWritePhaseTiming>? phaseObserver,
        AtomicFileWritePhase phase,
        long started,
        bool succeeded)
    {
        if (phaseObserver is null)
        {
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        try
        {
            phaseObserver(new AtomicFileWritePhaseTiming(phase, elapsed, succeeded));
        }
        catch
        {
            // Diagnostics must never alter the target/companion recovery contract, especially in
            // the interval after a companion has moved but before target publication completes.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: never replace the writer/cancellation exception with temp-file cleanup.
        }
    }
}
