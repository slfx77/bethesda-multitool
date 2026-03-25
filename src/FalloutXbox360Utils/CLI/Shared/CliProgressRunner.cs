using FalloutXbox360Utils.Core;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Shared;

/// <summary>
///     Reduces boilerplate for the standard Spectre.Console progress bar pattern
///     used across CLI commands. Wraps the 4-column progress display with
///     <see cref="AnalysisProgress" /> phase/percent tracking.
/// </summary>
internal static class CliProgressRunner
{
    /// <summary>
    ///     Runs an async operation with a standard progress bar that tracks
    ///     <see cref="AnalysisProgress.Phase" /> and <see cref="AnalysisProgress.PercentComplete" />.
    /// </summary>
    internal static async Task<T> RunWithProgressAsync<T>(
        string description,
        Func<IProgress<AnalysisProgress>, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        return await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask(description, maxValue: 100);
                var progress = new Progress<AnalysisProgress>(p =>
                {
                    task.Description = p.Phase;
                    task.Value = p.PercentComplete;
                });
                return await work(progress, cancellationToken);
            });
    }

    /// <summary>
    ///     Runs an async operation with no return value.
    /// </summary>
    internal static async Task RunWithProgressAsync(
        string description,
        Func<IProgress<AnalysisProgress>, CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask(description, maxValue: 100);
                var progress = new Progress<AnalysisProgress>(p =>
                {
                    task.Description = p.Phase;
                    task.Value = p.PercentComplete;
                });
                await work(progress, cancellationToken);
            });
    }
}
