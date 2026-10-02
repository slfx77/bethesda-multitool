using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Export.Report;
using BethesdaMultitool.Core.Semantic.LoadOrder;

namespace BethesdaMultitool.CLI.Shared;

internal static class SelectedViewCli
{
    internal static async Task<LoadOrderSelectionView?> LoadAsync(string input, string[]? specifications,
        bool allowMissing, CancellationToken cancellationToken)
    {
        if (specifications is not { Length: > 0 })
        {
            if (allowMissing) { throw new ArgumentException("--allow-missing-masters requires --load-order."); }
            return null;
        }
        Logger.SetOutput(Console.Error);
        var order = PluginLoadOrder.Open(LoadOrderOptions.ResolvePaths(input, specifications), allowMissing);
        var view = await LoadOrderSelectionView.LoadAsync(order, cancellationToken);
        Console.Error.WriteLine("Explicit load order: " + string.Join(" -> ", order.Entries.Select(e => e.Name)));
        Console.Error.WriteLine("FormIDs: load-order slots. View: selected static records. Runtime state: Unavailable.");
        if (order.MissingMasters.Count > 0)
        {
            Console.Error.WriteLine("Unresolved masters: " + string.Join(", ", order.MissingMasters));
        }
        return view;
    }

    internal static async Task WriteProvenanceAsync(LoadOrderSelectionView? view, string? output,
        CancellationToken cancellationToken)
    {
        if (view == null || string.IsNullOrEmpty(output)) { return; }
        var directory = Path.GetFullPath(output) + ".sources";
        if (Directory.Exists(directory)) { throw new IOException($"Provenance output already exists: {directory}"); }
        Directory.CreateDirectory(directory);
        foreach (var (name, content) in LoadOrderReportWriter.GenerateProvenance(view,
                     typeof(SelectedViewCli).Assembly.GetName().Version?.ToString() ?? "unknown"))
        {
            await using var file = new FileStream(Path.Combine(directory, name), FileMode.CreateNew);
            await using var writer = new StreamWriter(file);
            await writer.WriteAsync(content.AsMemory(), cancellationToken);
        }
        Console.Error.WriteLine("Source evidence: " + directory);
    }
}
