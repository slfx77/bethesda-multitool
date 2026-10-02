using System.CommandLine;
using System.Text.Json;
using BethesdaMultitool.Core.AssetBrowse;

namespace BethesdaMultitool.CLI.Commands.Assets;

/// <summary>Exposes source browsing and selected export operations independently of the Windows GUI.</summary>
internal static class AssetsCommand
{
    /// <summary>Creates catalog and export commands using the same source and conversion service as Explore.</summary>
    /// <returns>The assets command group.</returns>
    internal static Command Create()
    {
        var root = new Command("assets", "Browse and export folder or archive assets");
        root.Subcommands.Add(CreateList());
        root.Subcommands.Add(CreateExport());
        return root;
    }

    /// <summary>Creates metadata enumeration without decoding payloads.</summary>
    private static Command CreateList()
    {
        var command = new Command("list", "List source-relative asset paths");
        var source = new Argument<string>("source") { Description = "Folder, game installation, or archive" };
        var json = new Option<bool>("--json") { Description = "Write invariant JSON" };
        command.Arguments.Add(source);
        command.Options.Add(json);
        command.SetAction((result, token) =>
        {
            try
            {
                using var session = Open(result.GetValue(source)!);
                if (result.GetValue(json))
                {
                    using var output = Console.OpenStandardOutput();
                    using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
                    writer.WriteStartArray();
                    foreach (var entry in session.FileSystem.EnumerateFiles())
                    {
                        token.ThrowIfCancellationRequested();
                        writer.WriteStartObject();
                        writer.WriteString("path", entry.Path.Replace('\\', '/'));
                        writer.WriteNumber("size", entry.Size);
                        writer.WriteString("source", entry.Source);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                else
                {
                    foreach (var entry in session.FileSystem.EnumerateFiles())
                    {
                        token.ThrowIfCancellationRequested();
                        Console.WriteLine(entry.Path.Replace('\\', '/'));
                    }
                }
                return Task.FromResult(0);
            }
            catch (OperationCanceledException) { return Task.FromResult(130); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Console.Error.WriteLine(exception.Message);
                return Task.FromResult(1);
            }
        });
        return command;
    }

    /// <summary>Creates explicit selected extraction or DDX conversion without silently broadening the input.</summary>
    private static Command CreateExport()
    {
        var command = new Command("export", "Extract selected assets or convert selected DDX textures");
        var source = new Argument<string>("source") { Description = "Folder, game installation, or archive" };
        var paths = new Option<string[]>("--path") { Description = "Exact source-relative path; repeat for multiple files", AllowMultipleArgumentsPerToken = true };
        var directory = new Option<string>("--directory") { Description = "Include files recursively beneath this source-relative folder" };
        var exclude = new Option<string[]>("--exclude") { Description = "Exclude a selected file or folder; repeat as needed", AllowMultipleArgumentsPerToken = true };
        var output = new Option<string>("--output", "-o") { Required = true, Description = "Output directory" };
        var convert = new Option<bool>("--ddx") { Description = "Convert selected DDX textures to DDS" };
        var overwrite = new Option<bool>("--force") { Description = "Replace existing output files" };
        command.Arguments.Add(source);
        command.Options.Add(paths);
        command.Options.Add(directory);
        command.Options.Add(exclude);
        command.Options.Add(output);
        command.Options.Add(convert);
        command.Options.Add(overwrite);
        command.SetAction(async (result, token) =>
        {
            try
            {
                using var session = Open(result.GetValue(source)!);
                var selection = new Slfx77.Multitool.Core.Browsing.AssetSelection(session.SourcePath, StringComparer.OrdinalIgnoreCase);
                var explicitPaths = result.GetValue(paths) ?? [];
                foreach (var path in explicitPaths) { selection.SetSelected(path, true); }
                if (result.GetValue(directory) is { } folder) { selection.SetSelected(folder, true); }
                foreach (var path in result.GetValue(exclude) ?? []) { selection.SetSelected(path, false); }
                var selected = session.FileSystem.EnumerateFiles().Where(entry => selection.IsSelected(entry.Path))
                    .Where(entry => !result.GetValue(convert) || entry.Path.EndsWith(".ddx", StringComparison.OrdinalIgnoreCase))
                    .Select(entry => entry.Path)
                    .Concat(explicitPaths.Where(selection.IsSelected)).ToArray();
                if (selected.Length == 0)
                {
                    Console.Error.WriteLine("Select files with --path or a subtree with --directory (use . for the source root).");
                    return 2;
                }
                var results = await AssetExportService.ExportAsync(session.FileSystem, selected, result.GetValue(output)!,
                    result.GetValue(convert) ? AssetExportMode.DdxToDds : AssetExportMode.Original,
                    result.GetValue(overwrite), cancellationToken: token);
                foreach (var item in results)
                {
                    if (item.Success) { Console.WriteLine(item.OutputPath); }
                    else { Console.Error.WriteLine($"{item.VirtualPath}: {item.Error}"); }
                }
                return results.All(item => item.Success) ? 0 : 1;
            }
            catch (OperationCanceledException) { return 130; }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
        });
        return command;
    }

    /// <summary>Uses Explore's source planning and ownership rules for a CLI request.</summary>
    private static AssetBrowseSession Open(string source)
    {
        // The adapter's only owned resource is the session, which the CLI disposes.
        return BethesdaBrowseSource.Open(ExploreSourcePlanner.Create(source)).Session;
    }
}
