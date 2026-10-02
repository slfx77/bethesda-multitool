using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.Json;
using BethesdaMultitool.Core.Modeling;
using Slfx77.Multitool.Core.Models.Export;
using Slfx77.Multitool.Core.Models.Inspection;

namespace BethesdaMultitool.CLI.Commands.Mesh;

/// <summary>
///     The shared <c>mesh</c> command (design section 3): <c>mesh info</c> inspects one model and <c>mesh convert</c>
///     converts models to GLB or <c>.blend</c> through the Shared operations; the debug commands <c>validate</c>,
///     <c>fidelity</c>, <c>formats</c>, <c>dump</c> and <c>package</c> live in <see cref="MeshDebugCommands" /> (plan
///     section 5). Legacy <c>export nif</c> and <c>classic mesh export</c> are unchanged.
/// </summary>
internal static class MeshCommand
{
    /// <summary>The exit code for a usage error on a <c>mesh</c> command, the shared model shells' convention.</summary>
    public const int UsageExitCode = 2;

    /// <summary>
    ///     Maps a parse error on a <c>mesh</c> command to <see cref="UsageExitCode" />, as AWE's model shell does;
    ///     System.CommandLine returns 1 for every parse error, and other commands keep that.
    /// </summary>
    /// <param name="parseResult">The root parse result.</param>
    /// <param name="exitCode">The exit code the invocation returned.</param>
    /// <returns><see cref="UsageExitCode" /> for a parse error inside <c>mesh</c>, else <paramref name="exitCode" />.</returns>
    internal static int MapUsageExitCode(ParseResult parseResult, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        if (parseResult.Action is not ParseErrorAction)
        {
            return exitCode;
        }

        for (SymbolResult? current = parseResult.CommandResult; current is not null; current = current.Parent)
        {
            if (current is CommandResult { Command.Name: "mesh" })
            {
                return UsageExitCode;
            }
        }

        return exitCode;
    }

    /// <summary>Creates the <c>mesh</c> command group.</summary>
    public static Command Create()
    {
        var command = new Command("mesh", "Inspect and convert models through the shared model workflow");
        command.Subcommands.Add(CreateInfo());
        command.Subcommands.Add(CreateConvert());
        command.Subcommands.Add(MeshDebugCommands.CreateValidate());
        command.Subcommands.Add(MeshDebugCommands.CreateFidelity());
        command.Subcommands.Add(MeshDebugCommands.CreateFormats());
        command.Subcommands.Add(MeshDebugCommands.CreateDump());
        command.Subcommands.Add(MeshDebugCommands.CreatePackage());
        return command;
    }

    /// <summary>Creates <c>mesh info</c>.</summary>
    private static Command CreateInfo()
    {
        var input = new Argument<string>("input") { Description = "A model file, or a .bsa/.ba2 archive with --entry" };
        var entry = CreateEntryOption("The model's path inside an archive input");
        var game = CreateGameOption();
        var platform = CreatePlatformOption();
        var skeleton = CreateSkeletonOption();
        var dataRoot = CreateDataRootOption();
        var json = new Option<bool>("--json") { Description = "Write the invariant model information JSON" };
        var verbose = new Option<bool>("--verbose", "-v") { Description = "Include source metadata and every writer fidelity row" };
        var command = new Command("info", "Inspect a model: structure, units, textures and each writer's fidelity plan");
        command.Arguments.Add(input);
        command.Options.Add(entry);
        command.Options.Add(game);
        command.Options.Add(platform);
        command.Options.Add(skeleton);
        command.Options.Add(dataRoot);
        command.Options.Add(json);
        command.Options.Add(verbose);
        command.SetAction((parse, token) => ExecuteInfoAsync(parse.GetValue(input)!, parse.GetValue(entry),
            parse.GetValue(game), parse.GetValue(json), parse.GetValue(verbose), Console.Out,
            Console.OpenStandardOutput(), Console.Error, token, dataRoots: parse.GetValue(dataRoot),
            platform: parse.GetValue(platform), skeleton: parse.GetValue(skeleton)));
        return command;
    }

    /// <summary>Creates <c>mesh convert</c>.</summary>
    private static Command CreateConvert()
    {
        var input = new Argument<string>("input") { Description = "A model file, a directory of models, or a .bsa/.ba2 archive" };
        var output = new Argument<string>("output_path") { Description = "An output directory, or an exact file name for one input" };
        var entry = CreateEntryOption("For an archive input, one entry to convert; omit to convert every model in it");
        var game = CreateGameOption();
        var platform = CreatePlatformOption();
        var skeleton = CreateSkeletonOption();
        var dataRoot = CreateDataRootOption();
        var format = new Option<string>("--format") { Description = "Output writer", DefaultValueFactory = _ => "glb" };
        format.AcceptOnlyFromAmong("glb", "blend");
        var scale = CreateScaleOption();
        var overwrite = new Option<bool>("--overwrite") { Description = "Replace existing model outputs; reports are never overwritten" };
        var command = new Command("convert", "Convert models through the shared GLB or Blender writer");
        command.Arguments.Add(input);
        command.Arguments.Add(output);
        command.Options.Add(entry);
        command.Options.Add(game);
        command.Options.Add(platform);
        command.Options.Add(skeleton);
        command.Options.Add(dataRoot);
        command.Options.Add(format);
        command.Options.Add(scale);
        command.Options.Add(overwrite);
        command.SetAction((parse, token) => ExecuteConvertAsync(parse.GetValue(input)!, parse.GetValue(output)!,
            parse.GetValue(entry), parse.GetValue(game), parse.GetValue(format)!, parse.GetValue(scale),
            parse.GetValue(overwrite), Console.Out, Console.Error, token, dataRoots: parse.GetValue(dataRoot),
            platform: parse.GetValue(platform), skeleton: parse.GetValue(skeleton)));
        return command;
    }

    /// <summary>The archive entry option shared by the subcommands.</summary>
    internal static Option<string?> CreateEntryOption(string description)
    {
        return new Option<string?>("--entry", "-e") { Description = description };
    }

    /// <summary>The game option: which game's world units apply (default: not established, FNV's value assumed).</summary>
    internal static Option<string?> CreateGameOption()
    {
        return new Option<string?>("--game")
        {
            Description = "The game whose world units apply (fnv, fo3, or auto); omitted means not established"
        };
    }

    /// <summary>
    ///     The platform option: the console a big-endian (X360 or PS3) NIF was shipped for. It selects only the byte
    ///     order of packed vertex colors, the one measured difference between the consoles; omitted, X360 is assumed
    ///     and the reader reports every packed color stream it decodes under that assumption.
    /// </summary>
    internal static Option<string?> CreatePlatformOption()
    {
        var option = new Option<string?>("--platform")
        {
            Description = "The console a big-endian NIF was shipped for (x360 or ps3); it selects the packed " +
                          "vertex-color byte order. Omitted: x360 assumed, with a diagnostic per packed color stream"
        };
        option.AcceptOnlyFromAmong("x360", "ps3");
        return option;
    }

    /// <summary>
    ///     The skeleton option (cut-1b slice 10, plan section 1.7): the <c>skeleton.nif</c> file a <c>.kf</c> animation
    ///     stream binds to, which wins over the nearest ancestor <c>skeleton.nif</c> of the stream's own path. It names a
    ///     file and switches no behavior: a <c>.nif</c> input ignores it. It sets the
    ///     <see cref="BethesdaModelRegistration.SkeletonOption" /> app option.
    /// </summary>
    internal static Option<string?> CreateSkeletonOption()
    {
        return new Option<string?>("--skeleton")
        {
            Description = "The skeleton.nif file a .kf animation stream binds to, instead of the nearest ancestor " +
                          "skeleton.nif of the stream's own path; a .nif input ignores it",
            Arity = ArgumentArity.ExactlyOne
        };
    }

    /// <summary>
    ///     The data-root option: Bethesda Data folders textures resolve from, earlier first (repeatable). Omitted, the
    ///     nearest ancestor of the input holding a <c>textures</c> folder is used (an archive input's own folder).
    /// </summary>
    internal static Option<string[]?> CreateDataRootOption()
    {
        return new Option<string[]?>("--data-root")
        {
            Description = "A Data folder to resolve textures from (loose files over archives); repeat for more. " +
                          "Omitted: the nearest ancestor of the input holding a textures folder",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = false
        };
    }

    /// <summary>The scale option: a finite positive multiplier applied after conversion to meters (default 1).</summary>
    internal static Option<double> CreateScaleOption()
    {
        return new Option<double>("--scale")
        {
            Description = "Finite positive multiplier applied after conversion to meters",
            DefaultValueFactory = _ => 1d,
            Arity = ArgumentArity.ExactlyOne,
            CustomParser = result => ParsePositiveNumber(result, "--scale")
        };
    }

    /// <summary>Parses a finite positive invariant-culture number.</summary>
    internal static double ParsePositiveNumber(ArgumentResult result, string optionName)
    {
        if (result.Tokens.Count == 1 && double.TryParse(result.Tokens[0].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value > 0)
        {
            return value;
        }

        result.AddError($"{optionName} requires a finite positive number using a decimal point.");
        return 0;
    }

    /// <summary>Runs <c>mesh info</c> and writes the Shared text or JSON result.</summary>
    /// <returns>The Shared exit code, 1 for a failure outside a result, or 130 on cancellation.</returns>
    internal static async Task<int> ExecuteInfoAsync(string input, string? entry, string? game, bool json, bool verbose,
        TextWriter textOutput, Stream jsonOutput, TextWriter error, CancellationToken cancellationToken,
        ModelMemoryGate? memory = null, IReadOnlyList<string>? dataRoots = null, string? platform = null,
        string? skeleton = null)
    {
        try
        {
            var result = await BethesdaModelWorkflow.InfoAsync(input, entry, game, platform, dataRoots, memory,
                cancellationToken, skeleton).ConfigureAwait(false);
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

            return result.ExitCode;
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

    /// <summary>Runs <c>mesh convert</c> and writes the Shared per-item accounting.</summary>
    /// <returns>The Shared exit code, 1 for a failure outside a result, or 130 on cancellation.</returns>
    internal static async Task<int> ExecuteConvertAsync(string input, string output, string? entry, string? game,
        string format, double scale, bool overwrite, TextWriter standardOutput, TextWriter error,
        CancellationToken cancellationToken, ModelMemoryGate? memory = null, IReadOnlyList<string>? dataRoots = null,
        string? platform = null, string? skeleton = null)
    {
        try
        {
            var result = await BethesdaModelWorkflow.ConvertAsync(input, entry, output,
                new ModelConvertOptions(format, scale, overwrite), game, platform, dataRoots, memory,
                cancellationToken, skeleton).ConfigureAwait(false);
            ModelConvertTextFormatter.Write(standardOutput, result,
                result.ExitCode == 130 ? CancellationToken.None : cancellationToken);
            return result.ExitCode;
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
