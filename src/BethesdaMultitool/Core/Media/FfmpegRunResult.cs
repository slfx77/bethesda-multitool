namespace BethesdaMultitool.Core.Media;

/// <summary>
///     Outcome of one ffmpeg invocation that wrote to a FILE rather than to a pipe.
///     <para>
///         Deliberately path-based rather than <c>byte[]</c>-shaped like the XMA converters: a
///         movie is not something to buffer whole. The Oblivion PSP movies are only 272-792 KB,
///         but the same route has to serve <c>Data\Video\*.bik</c> in the Fallout builds.
///     </para>
/// </summary>
internal sealed record FfmpegRunResult
{
    /// <summary>True only when ffmpeg exited 0 AND the output file was left in place.</summary>
    public bool Success { get; init; }

    /// <summary>The file ffmpeg was asked to write. Only meaningful when <see cref="Success" />.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Why the run did not succeed, in a form fit to show a user.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>True when the run ended because the caller cancelled it, not because it failed.</summary>
    public bool Cancelled { get; init; }

    internal static FfmpegRunResult Ok(string outputPath)
    {
        return new FfmpegRunResult { Success = true, OutputPath = outputPath };
    }

    internal static FfmpegRunResult Failed(string message)
    {
        return new FfmpegRunResult { ErrorMessage = message };
    }

    internal static FfmpegRunResult WasCancelled()
    {
        return new FfmpegRunResult { Cancelled = true, ErrorMessage = "Cancelled" };
    }
}
