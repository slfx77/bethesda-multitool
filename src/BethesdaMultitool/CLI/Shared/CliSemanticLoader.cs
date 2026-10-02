using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Semantic;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Shared;

/// <summary>
///     Shared CLI adapter for semantic file loading with consistent progress and error handling.
/// </summary>
internal static class CliSemanticLoader
{
    /// <summary>
    ///     Loads <paramref name="filePath" /> behind a progress bar, returning null after printing the error
    ///     when the file is missing or fails to load.
    /// </summary>
    /// <param name="filePath">ESM/ESP or DMP path.</param>
    /// <param name="description">Initial progress-task label.</param>
    /// <param name="options">Load options; the progress callbacks are replaced unless the caller supplied them.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <param name="console">
    ///     Where the progress bar and error lines go. Null means the shared <see cref="AnsiConsole.Console" />
    ///     (stdout); a command whose stdout carries a machine-readable document passes
    ///     <see cref="CliConsoles.Stderr" /> so nothing but the document reaches stdout.
    /// </param>
    internal static async Task<UnifiedAnalysisResult?> TryLoadAsync(
        string filePath,
        string description,
        SemanticFileLoadOptions? options = null,
        CancellationToken cancellationToken = default,
        IAnsiConsole? console = null)
    {
        console ??= AnsiConsole.Console;

        if (!File.Exists(filePath))
        {
            console.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(filePath)}");
            return null;
        }

        options ??= new SemanticFileLoadOptions();

        try
        {
            return await console.Progress()
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask(description, maxValue: 100);
                    var analysisProgress = options.AnalysisProgress ?? new Progress<AnalysisProgress>(p =>
                    {
                        task.Description = p.Phase;
                        task.Value = p.PercentComplete * 0.8;
                    });
                    var parseProgress = options.ParseProgress ?? new Progress<(int percent, string phase)>(p =>
                    {
                        task.Description = p.phase;
                        task.Value = 80 + p.percent * 0.2;
                    });

                    var result = await SemanticFileLoader.LoadAsync(
                        filePath,
                        options with
                        {
                            AnalysisProgress = analysisProgress,
                            ParseProgress = parseProgress
                        },
                        cancellationToken);
                    task.Value = 100;
                    return result;
                });
        }
        catch (Exception ex)
        {
            console.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return null;
        }
    }
}
