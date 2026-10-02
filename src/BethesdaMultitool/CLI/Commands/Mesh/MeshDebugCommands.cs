using System.CommandLine;
using System.Text.Json;
using BethesdaMultitool.Core.Modeling;
using Slfx77.Multitool.Core.Models.Catalog;
using Slfx77.Multitool.Core.Models.Export;
using Slfx77.Multitool.Core.Models.Inspection;
using Slfx77.Multitool.Media.Blender.Operations;

namespace BethesdaMultitool.CLI.Commands.Mesh;

/// <summary>
///     The <c>mesh</c> debug commands (plan section 5, "Debug commands"; AWE's <c>ModelValidateCommand</c>,
///     <c>ModelFidelityCommand</c>, <c>ModelFormatsCommand</c>, <c>ModelDumpCommand</c> and <c>ModelPackageCommand</c>
///     are the pattern): <c>validate</c> and <c>fidelity</c> go through the Shared inspection operation, <c>formats</c>
///     prints the Shared format catalog, <c>dump</c> streams the Shared exact document JSON
///     (<c>ModelDumpOperation</c>, schema version 1) and <c>package</c> publishes the Blender package through the Shared
///     <c>ModelPackageOperation</c>, whose preflight neither locates nor runs Blender.
/// </summary>
/// <remarks>
///     Exit codes: the Shared result's for <c>validate</c>, <c>fidelity</c>, <c>dump</c> (0 complete, 1 failed or not a
///     model, 2 ambiguous) and <c>package</c> (0 published or skipped because the zip exists without
///     <c>--overwrite</c>, 1 failed or not a model); 0 for a completed <c>formats</c>; 1 with the message on standard
///     error for an IO, access, argument, not-supported or invalid-data failure outside a result (a missing input, a
///     <c>--output</c> dump file that exists without <c>--overwrite</c>, a package name that is not <c>.zip</c>); 130 on
///     cancellation. No command launches Blender.
/// </remarks>
internal static class MeshDebugCommands
{
    /// <summary>Creates <c>mesh validate</c>.</summary>
    public static Command CreateValidate()
    {
        var input = new Argument<string>("input") { Description = "A model file, or a .bsa/.ba2 archive with --entry" };
        var entry = MeshCommand.CreateEntryOption("The model's path inside an archive input");
        var game = MeshCommand.CreateGameOption();
        var platform = MeshCommand.CreatePlatformOption();
        var skeleton = MeshCommand.CreateSkeletonOption();
        var dataRoot = MeshCommand.CreateDataRootOption();
        var json = new Option<bool>("--json") { Description = "Write the result with the shared model information JSON schema" };
        var command = new Command("validate",
            "Read a model and validate its normalized structure without decoding pixels or planning a writer");
        command.Arguments.Add(input);
        command.Options.Add(entry);
        command.Options.Add(game);
        command.Options.Add(platform);
        command.Options.Add(skeleton);
        command.Options.Add(dataRoot);
        command.Options.Add(json);
        command.SetAction((parse, token) => ExecuteValidateAsync(parse.GetValue(input)!, parse.GetValue(entry),
            parse.GetValue(game), parse.GetValue(json), Console.Out, Console.OpenStandardOutput(), Console.Error, token,
            dataRoots: parse.GetValue(dataRoot), platform: parse.GetValue(platform), skeleton: parse.GetValue(skeleton)));
        return command;
    }

    /// <summary>Creates <c>mesh fidelity</c>.</summary>
    public static Command CreateFidelity()
    {
        var input = new Argument<string>("input") { Description = "A model file, or a .bsa/.ba2 archive with --entry" };
        var format = new Option<string>("--format")
        {
            Description = "The writer whose every fidelity row is resolved, without writing a model",
            Required = true,
            Arity = ArgumentArity.ExactlyOne
        };
        format.AcceptOnlyFromAmong("glb", "blend");
        var scale = MeshCommand.CreateScaleOption();
        var entry = MeshCommand.CreateEntryOption("The model's path inside an archive input");
        var game = MeshCommand.CreateGameOption();
        var platform = MeshCommand.CreatePlatformOption();
        var skeleton = MeshCommand.CreateSkeletonOption();
        var dataRoot = MeshCommand.CreateDataRootOption();
        var json = new Option<bool>("--json") { Description = "Write the shared model information JSON with every fidelity row" };
        var command = new Command("fidelity",
            "Resolve one writer's complete fidelity through its own preparation, without writing a model or starting a tool");
        command.Arguments.Add(input);
        command.Options.Add(format);
        command.Options.Add(scale);
        command.Options.Add(entry);
        command.Options.Add(game);
        command.Options.Add(platform);
        command.Options.Add(skeleton);
        command.Options.Add(dataRoot);
        command.Options.Add(json);
        command.SetAction((parse, token) => ExecuteFidelityAsync(parse.GetValue(input)!, parse.GetValue(entry),
            parse.GetValue(game), parse.GetValue(format)!, parse.GetValue(scale), parse.GetValue(json), Console.Out,
            Console.OpenStandardOutput(), Console.Error, token, dataRoots: parse.GetValue(dataRoot),
            platform: parse.GetValue(platform), skeleton: parse.GetValue(skeleton)));
        return command;
    }

    /// <summary>Creates <c>mesh formats</c>.</summary>
    public static Command CreateFormats()
    {
        var json = new Option<bool>("--json") { Description = "Write the registered model format catalog as invariant JSON" };
        var command = new Command("formats",
            "List the registered model readers and writers, their variants, unit policies and admission rules");
        command.Options.Add(json);
        command.SetAction((parse, token) => ExecuteFormatsAsync(parse.GetValue(json), Console.Out,
            Console.OpenStandardOutput(), Console.Error, token));
        return command;
    }

    /// <summary>Creates <c>mesh dump</c> (AWE's <c>--native</c> spelling; <c>--output</c> publishes a staged file).</summary>
    public static Command CreateDump()
    {
        var input = new Argument<string>("input") { Description = "A model file, or a .bsa/.ba2 archive with --entry" };
        var entry = MeshCommand.CreateEntryOption("The model's path inside an archive input");
        var game = MeshCommand.CreateGameOption();
        var platform = MeshCommand.CreatePlatformOption();
        var skeleton = MeshCommand.CreateSkeletonOption();
        var dataRoot = MeshCommand.CreateDataRootOption();
        var native = new Option<bool>("--native")
        {
            Description = "Include full reader-retained native payloads and raw bytes in the JSON document"
        };
        var output = new Option<string?>("--output", "-o")
        {
            Description = "Write the JSON to this file (staged, published only when complete) instead of standard output"
        };
        var overwrite = new Option<bool>("--overwrite") { Description = "Replace an existing --output file" };
        var command = new Command("dump",
            "Write the exact model document as the shared JSON schema without decoding pixels or running writers");
        command.Arguments.Add(input);
        command.Options.Add(entry);
        command.Options.Add(game);
        command.Options.Add(platform);
        command.Options.Add(skeleton);
        command.Options.Add(dataRoot);
        command.Options.Add(native);
        command.Options.Add(output);
        command.Options.Add(overwrite);
        command.SetAction((parse, token) => ExecuteDumpAsync(parse.GetValue(input)!, parse.GetValue(entry),
            parse.GetValue(game), parse.GetValue(native), Console.OpenStandardOutput(), Console.Error, token,
            dataRoots: parse.GetValue(dataRoot), platform: parse.GetValue(platform), outputPath: parse.GetValue(output),
            overwrite: parse.GetValue(overwrite), skeleton: parse.GetValue(skeleton)));
        return command;
    }

    /// <summary>Creates <c>mesh package</c>.</summary>
    public static Command CreatePackage()
    {
        var input = new Argument<string>("input") { Description = "A model file, or a .bsa/.ba2 archive with --entry" };
        var output = new Argument<string>("output_zip") { Description = "The exact .zip filename of the Blender import package" };
        var entry = MeshCommand.CreateEntryOption("The model's path inside an archive input");
        var game = MeshCommand.CreateGameOption();
        var platform = MeshCommand.CreatePlatformOption();
        var skeleton = MeshCommand.CreateSkeletonOption();
        var dataRoot = MeshCommand.CreateDataRootOption();
        var scale = MeshCommand.CreateScaleOption();
        var overwrite = new Option<bool>("--overwrite") { Description = "Replace an existing package; otherwise skip it" };
        var command = new Command("package",
            "Prepare the Blender import package (manifest, streams, images) without requiring or launching Blender");
        command.Arguments.Add(input);
        command.Arguments.Add(output);
        command.Options.Add(entry);
        command.Options.Add(game);
        command.Options.Add(platform);
        command.Options.Add(skeleton);
        command.Options.Add(dataRoot);
        command.Options.Add(scale);
        command.Options.Add(overwrite);
        command.SetAction((parse, token) => ExecutePackageAsync(parse.GetValue(input)!, parse.GetValue(output)!,
            parse.GetValue(entry), parse.GetValue(game), parse.GetValue(scale), parse.GetValue(overwrite), Console.Out,
            Console.Error, token, dataRoots: parse.GetValue(dataRoot), platform: parse.GetValue(platform),
            skeleton: parse.GetValue(skeleton)));
        return command;
    }

    /// <summary>Runs <c>mesh validate</c> and writes the Shared text or JSON result.</summary>
    /// <returns>The Shared exit code (0, or 1 for a read or validation failure), 1 for a failure outside a result, or 130.</returns>
    internal static Task<int> ExecuteValidateAsync(string input, string? entry, string? game, bool json,
        TextWriter textOutput, Stream jsonOutput, TextWriter error, CancellationToken cancellationToken,
        ModelMemoryGate? memory = null, IReadOnlyList<string>? dataRoots = null, string? platform = null,
        string? skeleton = null)
    {
        return GuardAsync(async () =>
        {
            var result = await BethesdaModelWorkflow.ValidateAsync(input, entry, game, platform, dataRoots, memory,
                cancellationToken, skeleton).ConfigureAwait(false);
            await WriteInfoAsync(result, json, false, textOutput, jsonOutput, cancellationToken).ConfigureAwait(false);
            return result.ExitCode;
        }, error, cancellationToken);
    }

    /// <summary>Runs <c>mesh fidelity</c> and writes the verbose Shared text or JSON result.</summary>
    /// <returns>The Shared exit code, 1 for a failure outside a result, or 130 on cancellation.</returns>
    internal static Task<int> ExecuteFidelityAsync(string input, string? entry, string? game, string format,
        double scale, bool json, TextWriter textOutput, Stream jsonOutput, TextWriter error,
        CancellationToken cancellationToken, ModelMemoryGate? memory = null, IReadOnlyList<string>? dataRoots = null,
        string? platform = null, string? skeleton = null)
    {
        return GuardAsync(async () =>
        {
            var result = await BethesdaModelWorkflow.FidelityAsync(input, entry, format, scale, game, platform,
                dataRoots, memory, cancellationToken, skeleton).ConfigureAwait(false);
            await WriteInfoAsync(result, json, true, textOutput, jsonOutput, cancellationToken).ConfigureAwait(false);
            return result.ExitCode;
        }, error, cancellationToken);
    }

    /// <summary>Runs <c>mesh formats</c>: captures the catalog and writes it as text or JSON.</summary>
    /// <returns>0, 1 for a catalog or output failure, or 130 on cancellation.</returns>
    internal static Task<int> ExecuteFormatsAsync(bool json, TextWriter textOutput, Stream jsonOutput,
        TextWriter error, CancellationToken cancellationToken)
    {
        return GuardAsync(async () =>
        {
            var catalog = BethesdaModelWorkflow.CreateFormatCatalog(cancellationToken);
            if (json)
            {
                await using var writer = new Utf8JsonWriter(jsonOutput, new JsonWriterOptions { Indented = true });
                ModelFormatJsonFormatter.Write(writer, catalog, cancellationToken);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ModelFormatTextFormatter.Write(textOutput, catalog, cancellationToken);
            }

            return 0;
        }, error, cancellationToken);
    }

    /// <summary>
    ///     Runs <c>mesh dump</c> (AWE's <c>ModelDumpCommand</c>): streams the Shared exact document JSON to the borrowed
    ///     output, or publishes it as a staged file, and reports the result's reason and cleanup failure on standard error.
    /// </summary>
    /// <param name="input">The model file or archive.</param>
    /// <param name="entry">The archive entry path.</param>
    /// <param name="game">The game whose units apply, or null.</param>
    /// <param name="includeNative">Whether the reader retains full native payloads and raw bytes (<c>--native</c>).</param>
    /// <param name="jsonOutput">The borrowed UTF-8 destination, left open on every outcome; unused with a file output.</param>
    /// <param name="error">The destination of the result's reason, its cleanup failure, or a failure message.</param>
    /// <param name="cancellationToken">Cancels reading, serialization and publication.</param>
    /// <param name="memory">The memory admission gate; null uses the process observer's gate.</param>
    /// <param name="dataRoots">Data folders to resolve textures from; null or empty infers one.</param>
    /// <param name="platform">The console a big-endian input was shipped for (x360 or ps3), or null.</param>
    /// <param name="outputPath">The JSON file to publish instead of writing to <paramref name="jsonOutput" />, or null.</param>
    /// <param name="overwrite">Whether an existing <paramref name="outputPath" /> may be replaced.</param>
    /// <param name="skeleton">The skeleton file a <c>.kf</c> input binds to (<c>--skeleton</c>), or null.</param>
    /// <returns>
    ///     The Shared exit code (0 complete, 1 failed or not a model, 2 ambiguous), 1 with a message for a failure outside
    ///     a result (an existing file output without <paramref name="overwrite" /> included), or 130 on cancellation.
    /// </returns>
    internal static Task<int> ExecuteDumpAsync(string input, string? entry, string? game, bool includeNative,
        Stream jsonOutput, TextWriter error, CancellationToken cancellationToken, ModelMemoryGate? memory = null,
        IReadOnlyList<string>? dataRoots = null, string? platform = null, string? outputPath = null,
        bool overwrite = false, string? skeleton = null)
    {
        return GuardAsync(async () =>
        {
            var result = outputPath is null
                ? await BethesdaModelWorkflow.DumpAsync(input, entry, jsonOutput, includeNative, game, platform,
                    dataRoots, memory, cancellationToken, skeleton).ConfigureAwait(false)
                : await BethesdaModelWorkflow.DumpToFileAsync(input, entry, outputPath, overwrite, includeNative, game,
                    platform, dataRoots, memory, cancellationToken, skeleton).ConfigureAwait(false);
            if (result.Reason is { } reason)
            {
                await error.WriteLineAsync(reason).ConfigureAwait(false);
            }

            if (result.CleanupFailure is { } cleanup)
            {
                await error.WriteLineAsync(cleanup).ConfigureAwait(false);
            }

            return result.ExitCode;
        }, error, cancellationToken);
    }

    /// <summary>
    ///     Runs <c>mesh package</c> (AWE's <c>ModelPackageCommand</c>) and writes the Shared package accounting; the
    ///     terminal report is never cut short by a cancellation that arrives while it is written.
    /// </summary>
    /// <param name="input">The model file or archive.</param>
    /// <param name="output">The exact .zip to publish.</param>
    /// <param name="entry">The archive entry path.</param>
    /// <param name="game">The game whose units apply, or null.</param>
    /// <param name="scale">The finite positive multiplier applied after conversion to meters.</param>
    /// <param name="overwrite">Whether an existing zip may be replaced; otherwise the run is Skipped with exit 0.</param>
    /// <param name="standardOutput">The report's destination.</param>
    /// <param name="error">The destination of a failure message outside a result.</param>
    /// <param name="cancellationToken">Cancels reading, planning and packaging.</param>
    /// <param name="memory">The memory admission gate; null uses the process observer's gate.</param>
    /// <param name="dataRoots">Data folders to resolve textures from; null or empty infers one.</param>
    /// <param name="platform">The console a big-endian input was shipped for (x360 or ps3), or null.</param>
    /// <param name="skeleton">The skeleton file a <c>.kf</c> input binds to (<c>--skeleton</c>), or null.</param>
    /// <returns>The Shared exit code (0 published or skipped, 1 failed), 1 with a message outside a result, or 130.</returns>
    internal static Task<int> ExecutePackageAsync(string input, string output, string? entry, string? game,
        double scale, bool overwrite, TextWriter standardOutput, TextWriter error, CancellationToken cancellationToken,
        ModelMemoryGate? memory = null, IReadOnlyList<string>? dataRoots = null, string? platform = null,
        string? skeleton = null)
    {
        return GuardAsync(async () =>
        {
            var result = await BethesdaModelWorkflow.PackageAsync(input, entry, output, scale, overwrite, game, platform,
                dataRoots, memory, cancellationToken, skeleton).ConfigureAwait(false);
            ModelPackageTextFormatter.Write(standardOutput, result, CancellationToken.None);
            return result.ExitCode;
        }, error, cancellationToken);
    }

    /// <summary>Writes a Shared inspection result as text or JSON; a canceled result is still written in full.</summary>
    private static async Task WriteInfoAsync(ModelInfoResult result, bool json, bool verbose, TextWriter textOutput,
        Stream jsonOutput, CancellationToken cancellationToken)
    {
        var outputToken = result.ExitCode == 130 ? CancellationToken.None : cancellationToken;
        if (json)
        {
            await using var writer = new Utf8JsonWriter(jsonOutput, new JsonWriterOptions { Indented = true });
            ModelInfoJsonFormatter.Write(writer, result, verbose, outputToken);
            await writer.FlushAsync(outputToken).ConfigureAwait(false);
        }
        else
        {
            ModelInfoTextFormatter.Write(textOutput, result, verbose, outputToken);
        }
    }

    /// <summary>Runs one command body with <see cref="MeshCommand" />'s exception mapping.</summary>
    /// <returns>The body's exit code, 130 on caller cancellation, or 1 with the message for an ordinary failure.</returns>
    private static async Task<int> GuardAsync(Func<Task<int>> body, TextWriter error,
        CancellationToken cancellationToken)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException
                                            or NotSupportedException or InvalidDataException)
        {
            await error.WriteLineAsync(failure.Message).ConfigureAwait(false);
            return 1;
        }
    }
}
