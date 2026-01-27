namespace Xbox360MemoryCarver.Repack.Processors;

/// <summary>
///     Interface for repack processors that handle specific file types.
/// </summary>
public interface IRepackProcessor
{
    /// <summary>
    ///     Name of this processor for logging/display.
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     Processes files from source to output folder.
    /// </summary>
    /// <param name="options">Repacker options.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of files processed.</returns>
    Task<int> ProcessAsync(
        RepackerOptions options,
        IProgress<RepackerProgress> progress,
        CancellationToken cancellationToken);
}
