using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Semantic;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Shared;

/// <summary>
///     Shared CLI adapter for semantic file loading with consistent progress and error handling.
/// </summary>
internal static class CliSemanticLoader
{
    internal static async Task<UnifiedAnalysisResult?> TryLoadAsync(
        string filePath,
        string description,
        SemanticFileLoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(filePath)}");
            return null;
        }

        options ??= new SemanticFileLoadOptions();

        try
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
                    var analysisProgress = options.AnalysisProgress ?? new Progress<AnalysisProgress>(p =>
                    {
                        task.Description = p.Phase;
                        task.Value = p.PercentComplete * 0.8;
                    });
                    var parseProgress = options.ParseProgress ?? new Progress<(int percent, string phase)>(p =>
                    {
                        task.Description = p.phase;
                        task.Value = 80 + (p.percent * 0.2);
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
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return null;
        }
    }
}
