using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Export.Scripts;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Semantic;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Export;

/// <summary>
///     <c>export scripts &lt;input&gt; -o &lt;dir&gt;</c>: writes every SCPT script of an ESM/ESP plugin or a DMP
///     memory dump as its own file, plus one <c>scripts.manifest.json</c> (see <see cref="ScriptExportWriter" />).
///     <list type="bullet">
///         <item>
///             <description>
///                 Author-written SCTX (stored in the plugin, or recovered from the dump) goes to
///                 <c>&lt;EditorID&gt;.gek</c> VERBATIM: the Windows-1252 bytes the record holds, no BOM, line
///                 endings as stored, no header and nothing appended.
///             </description>
///         </item>
///         <item>
///             <description>
///                 A reconstruction decompiled from SCDA goes to a SEPARATE <c>&lt;EditorID&gt;.decompiled.gek</c>
///                 that opens with <c>;</c> comment lines saying what it is (plugin or dump, FormID, build label,
///                 bytecode byte order). By default (<c>--decompiled missing</c>) only for a script that has no
///                 source text; <c>all</c> writes one beside every source file, <c>none</c> never.
///             </description>
///         </item>
///         <item>
///             <description>
///                 The extension defaults to <c>.gek</c> for Fallout: New Vegas and Fallout 3 (one GECK script
///                 language; the community convention and the VS Code grammar key on it) and <c>.txt</c> for
///                 every other game; <c>--ext</c> overrides it.
///             </description>
///         </item>
///     </list>
///     <para>
///         Selection: each <c>--id</c> is a FormID when it starts with <c>0x</c>, otherwise an EditorID matched
///         exactly, ignoring case; every script matching any <c>--id</c> is exported (a memory dump can hold the
///         same FormID twice), and <c>-f</c> narrows the result to EditorIDs containing its text. Without either,
///         every script is exported.
///     </para>
///     <para>
///         Exit codes: 0 when the export was written; 1 for a missing or unsupported input, an <c>--id</c> that
///         matches no script, a <c>-f</c> that matches nothing, a target that already exists without
///         <c>--overwrite</c> (nothing is written then), an invalid <c>--ext</c>/<c>--decompiled</c>/<c>--id</c>
///         value that reached the runner, or an I/O failure; 130 when cancelled. An invalid option value on the
///         command line is a parse error. <see cref="Logger" /> output and errors go to stderr; the summary goes
///         to stdout with every data-derived value escaped.
///     </para>
/// </summary>
internal static class ExportScriptsCommand
{
    /// <summary>The <c>--decompiled</c> default.</summary>
    internal const string DefaultDecompiledPolicy = "missing";

    /// <summary>Creates the <c>scripts</c> subcommand of <c>export</c>.</summary>
    internal static Command Create()
    {
        var command = new Command(
            "scripts",
            "Export scripts as individual files: plugin SCTX verbatim as <EditorID>.gek, SCDA reconstructions " +
            "as <EditorID>.decompiled.gek, provenance in scripts.manifest.json");

        var inputArgument = new Argument<string>("input")
        {
            Description = "ESM/ESP plugin or DMP memory dump"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Directory for the script files and scripts.manifest.json (created when missing)",
            Required = true
        };
        var extensionOption = new Option<string?>("--ext")
        {
            Description = "Script file extension, with or without the dot (default: .gek for Fallout: New Vegas and " +
                          "Fallout 3, .txt for other games)"
        };
        extensionOption.Validators.Add(ValidateExtension);
        var decompiledOption = new Option<string>("--decompiled")
        {
            Description = "When to write a <stem>.decompiled<ext> reconstruction from SCDA: missing (default; only " +
                          "for scripts with no source text), all (also beside every source file) or none",
            DefaultValueFactory = _ => DefaultDecompiledPolicy,
            HelpName = "missing|all|none"
        };
        decompiledOption.Validators.Add(ValidateDecompiledPolicy);
        var idOption = new Option<string[]>("--id")
        {
            Description = "Repeatable: export only this script, as a FormID with 0x prefix (0x00168CFC) or an " +
                          "exact EditorID (case-insensitive). An id that matches no script is an error",
            HelpName = "0xFormID|EditorID"
        };
        idOption.Validators.Add(ValidateIds);
        var filterOption = new Option<string?>("-f", "--filter")
        {
            Description = "Export only scripts whose EditorID contains this text (case-insensitive)"
        };
        var overwriteOption = new Option<bool>("--overwrite")
        {
            Description = "Replace existing files; without it the export fails before writing anything when any " +
                          "target already exists"
        };
        var buildLabelOption = new Option<string?>("--build-label")
        {
            Description = "Build label recorded in the manifest and in each decompiled file's banner (default: the " +
                          "Sample/Builds/<build> directory the input sits in, if any)"
        };

        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(extensionOption);
        command.Options.Add(decompiledOption);
        command.Options.Add(idOption);
        command.Options.Add(filterOption);
        command.Options.Add(overwriteOption);
        command.Options.Add(buildLabelOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var request = new Request
            {
                InputPath = parseResult.GetValue(inputArgument)!,
                OutputDirectory = parseResult.GetValue(outputOption)!,
                Extension = parseResult.GetValue(extensionOption),
                Decompiled = parseResult.GetValue(decompiledOption) ?? DefaultDecompiledPolicy,
                Ids = parseResult.GetValue(idOption) ?? [],
                Filter = parseResult.GetValue(filterOption),
                Overwrite = parseResult.GetValue(overwriteOption),
                BuildLabel = parseResult.GetValue(buildLabelOption)
            };
            var log = parseResult.InvocationConfiguration?.Error ?? Console.Error;
            return await RunAsync(request, AnsiConsole.Console, CliConsoles.Stderr, log, cancellationToken);
        });

        return command;
    }

    /// <summary>
    ///     Validates the request, loads the input through <see cref="SemanticFileLoader" /> and runs
    ///     <see cref="ExportLoaded" />. <see cref="Logger" /> output of the whole run goes to <paramref name="log" />.
    /// </summary>
    /// <param name="request">The parsed command line.</param>
    /// <param name="output">Where the summary goes (stdout).</param>
    /// <param name="error">Where status and error lines go (stderr).</param>
    /// <param name="log">The <see cref="Logger" /> sink for this run (stderr).</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The process exit code.</returns>
    internal static async Task<int> RunAsync(
        Request request,
        IAnsiConsole output,
        IAnsiConsole error,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(log);

        // Scoped to this call's async context: every log line of the run, the parse's included, is stderr.
        Logger.SetOutput(log);

        // Options first, so a malformed request fails before a multi-GB input is read.
        if (!TryValidate(request, out _, out var problem))
        {
            WriteError(error, problem);
            return 1;
        }

        if (Directory.Exists(request.InputPath))
        {
            WriteError(error, $"{request.InputPath} is a directory; pass an ESM/ESP plugin or a DMP memory dump.");
            return 1;
        }

        var inputPath = Path.GetFullPath(request.InputPath);
        if (!File.Exists(inputPath))
        {
            WriteError(error, $"Input file not found: {inputPath}");
            return 1;
        }

        AnalysisFileType fileType;
        try
        {
            fileType = SemanticFileLoader.ResolveSemanticFileType(inputPath);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException
                                              or IOException or UnauthorizedAccessException)
        {
            WriteError(error, exception.Message);
            return 1;
        }

        if (!IsSupportedInput(fileType))
        {
            WriteError(
                error,
                $"{Path.GetFileName(inputPath)} is not an ESM/ESP plugin or a DMP memory dump (detected {fileType}); " +
                "scripts can only be exported from those.");
            return 1;
        }

        // A second run into the same directory is the usual conflict. Catch it before the load, which for a
        // memory dump takes minutes; the writer still checks every target before it writes anything.
        if (FindManifestConflict(request.OutputDirectory, request.Overwrite) is { } conflict)
        {
            WriteConflict(error, conflict);
            return 1;
        }

        error.MarkupLine($"Loading {Markup.Escape(Path.GetFileName(inputPath))}...");
        UnifiedAnalysisResult loaded;
        try
        {
            loaded = await SemanticFileLoader.LoadAsync(
                inputPath,
                new SemanticFileLoadOptions { FileType = fileType },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception exception)
        {
            WriteError(error, $"Could not load {Path.GetFileName(inputPath)}: {exception.Message}");
            return 1;
        }

        using (loaded)
        {
            return ExportLoaded(loaded, request, output, error, DateTimeOffset.UtcNow).ExitCode;
        }
    }

    /// <summary>
    ///     Selects the scripts of an already-loaded input and writes the export. Separate from
    ///     <see cref="RunAsync" /> so a caller holding a parse (a test sharing a cached retail master) runs the
    ///     same selection, source description and writer as the command. Does not dispose
    ///     <paramref name="loaded" />.
    /// </summary>
    /// <param name="loaded">The loaded plugin or memory dump.</param>
    /// <param name="request">The command line; <see cref="Request.InputPath" /> is used only when the result
    ///     carries no path of its own.</param>
    /// <param name="output">Where the summary goes.</param>
    /// <param name="error">Where error lines go.</param>
    /// <param name="createdUtc">Recorded as the manifest's <c>createdUtc</c>.</param>
    internal static Outcome ExportLoaded(
        UnifiedAnalysisResult loaded,
        Request request,
        IAnsiConsole output,
        IAnsiConsole error,
        DateTimeOffset createdUtc)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!TryValidate(request, out var validated, out var problem))
        {
            WriteError(error, problem);
            return Outcome.Failed;
        }

        var inputPath = string.IsNullOrWhiteSpace(loaded.FilePath) ? request.InputPath : loaded.FilePath;
        var inputName = Path.GetFileName(inputPath);
        if (!IsSupportedInput(loaded.FileType))
        {
            WriteError(error, $"{inputName} is not an ESM/ESP plugin or a DMP memory dump (detected {loaded.FileType}).");
            return Outcome.Failed;
        }

        var selection = SelectScripts(loaded.Records.Scripts, validated.Selectors, request.Filter);
        if (selection.UnmatchedIds.Count > 0)
        {
            foreach (var unmatched in selection.UnmatchedIds)
            {
                WriteError(error, unmatched.FormId is { } formId
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"--id {unmatched.Text}: no script with FormID 0x{formId:X8} in {inputName}.")
                    : $"--id {unmatched.Text}: no script with that EditorID in {inputName} (an EditorID must match " +
                      "exactly, ignoring case; -f matches a substring).");
            }

            error.MarkupLine("Nothing was written.");
            return Outcome.Failed;
        }

        if (selection.Scripts.Count == 0 && !string.IsNullOrEmpty(request.Filter))
        {
            WriteError(
                error,
                $"No script EditorID in {inputName} contains '{request.Filter}'" +
                (validated.Selectors.Count > 0 ? " among the scripts --id selected" : "") +
                "; nothing was written.");
            return Outcome.Failed;
        }

        var isMemoryDump = loaded.FileType == AnalysisFileType.Minidump;
        var game = loaded.Records.Game;
        ScriptExportSource source;
        try
        {
            source = ScriptExportSource.Describe(inputPath, loaded.RawResult, game, isMemoryDump, request.BuildLabel);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            WriteError(error, $"Could not read {inputName} to describe it: {exception.Message}");
            return Outcome.Failed;
        }

        var options = new ScriptExportOptions
        {
            Extension = validated.Extension ?? ScriptExportFileNamer.DefaultExtension(game),
            Decompiled = validated.Decompiled,
            Overwrite = request.Overwrite,
            ToolVersion = CliConsoles.ToolVersion
        };

        ScriptExportSummary summary;
        try
        {
            summary = ScriptExportWriter.Write(
                selection.Scripts,
                source,
                options,
                request.OutputDirectory,
                loaded.Resolver.GetEditorId,
                createdUtc);
        }
        catch (ScriptExportConflictException conflict)
        {
            WriteConflict(error, conflict);
            return Outcome.Failed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or ArgumentException or InvalidOperationException)
        {
            WriteError(error, $"Script export failed: {exception.Message}");
            return Outcome.Failed;
        }

        WriteSummary(output, summary, source, options);
        return new Outcome(0, summary);
    }

    /// <summary>
    ///     Parses one <c>--id</c> value: <c>0x</c> followed by 1 to 8 hex digits is a FormID, anything else an
    ///     EditorID (surrounding spaces ignored).
    /// </summary>
    internal static bool TryParseId(
        string? text,
        [NotNullWhen(true)] out IdSelector? selector,
        [NotNullWhen(false)] out string? problem)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            selector = null;
            problem = "--id needs a value: a FormID with 0x prefix (0x00168CFC) or an EditorID.";
            return false;
        }

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var digits = trimmed.AsSpan(2);
            if (digits.Length is < 1 or > 8
                || !uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var formId))
            {
                selector = null;
                problem = $"--id '{text}' is not a FormID: after 0x give 1 to 8 hex digits (e.g. 0x00168CFC).";
                return false;
            }

            selector = new IdSelector(trimmed, formId, null);
            problem = null;
            return true;
        }

        selector = new IdSelector(trimmed, null, trimmed);
        problem = null;
        return true;
    }

    /// <summary>
    ///     The scripts to export, in input order: those matching any selector (all scripts when there is none),
    ///     narrowed to EditorIDs containing <paramref name="filter" /> (case-insensitive) when it is set. A
    ///     selector is unmatched when no script of the whole input matches it, whatever the filter keeps.
    /// </summary>
    internal static Selection SelectScripts(
        IReadOnlyList<ScriptRecord> scripts,
        IReadOnlyList<IdSelector> selectors,
        string? filter)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(selectors);

        var matched = new bool[selectors.Count];
        var selected = new List<ScriptRecord>();
        foreach (var script in scripts)
        {
            var include = selectors.Count == 0;
            for (var i = 0; i < selectors.Count; i++)
            {
                if (selectors[i].Matches(script))
                {
                    matched[i] = true;
                    include = true;
                }
            }

            if (include && !string.IsNullOrEmpty(filter))
            {
                include = script.EditorId?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;
            }

            if (include)
            {
                selected.Add(script);
            }
        }

        var unmatched = new List<IdSelector>();
        for (var i = 0; i < selectors.Count; i++)
        {
            if (!matched[i])
            {
                unmatched.Add(selectors[i]);
            }
        }

        return new Selection(selected, unmatched);
    }

    private static bool IsSupportedInput(AnalysisFileType fileType)
    {
        return fileType is AnalysisFileType.EsmFile or AnalysisFileType.Minidump;
    }

    private static bool TryValidate(
        Request request,
        [NotNullWhen(true)] out ValidatedRequest? validated,
        [NotNullWhen(false)] out string? problem)
    {
        validated = null;
        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            problem = "--output needs a directory.";
            return false;
        }

        string? extension = null;
        if (request.Extension is not null)
        {
            if (!TryNormalizeExtension(request.Extension, out var normalized, out problem))
            {
                return false;
            }

            extension = normalized;
        }

        if (!ScriptExportOptions.TryParseDecompiledPolicy(request.Decompiled, out var policy))
        {
            problem = DecompiledPolicyProblem(request.Decompiled);
            return false;
        }

        var selectors = new List<IdSelector>(request.Ids.Count);
        foreach (var id in request.Ids)
        {
            if (!TryParseId(id, out var selector, out problem))
            {
                return false;
            }

            selectors.Add(selector);
        }

        validated = new ValidatedRequest(extension, policy, selectors);
        problem = null;
        return true;
    }

    private static bool TryNormalizeExtension(
        string extension,
        [NotNullWhen(true)] out string? normalized,
        [NotNullWhen(false)] out string? problem)
    {
        try
        {
            normalized = ScriptExportFileNamer.NormalizeExtension(extension);
            problem = null;
            return true;
        }
        catch (ArgumentException)
        {
            normalized = null;
            problem = $"--ext '{extension}' is not a usable extension: give 1 to 16 ASCII letters, digits, '_' or '-' " +
                      "after one optional leading dot (e.g. .gek or txt).";
            return false;
        }
    }

    private static string DecompiledPolicyProblem(string? value)
    {
        return $"--decompiled '{value}' is not one of: missing, all, none.";
    }

    private static void ValidateExtension(OptionResult result)
    {
        foreach (var token in result.Tokens)
        {
            if (!TryNormalizeExtension(token.Value, out _, out var problem))
            {
                result.AddError(problem);
            }
        }
    }

    private static void ValidateDecompiledPolicy(OptionResult result)
    {
        foreach (var token in result.Tokens)
        {
            if (!ScriptExportOptions.TryParseDecompiledPolicy(token.Value, out _))
            {
                result.AddError(DecompiledPolicyProblem(token.Value));
            }
        }
    }

    private static void ValidateIds(OptionResult result)
    {
        foreach (var token in result.Tokens)
        {
            if (!TryParseId(token.Value, out _, out var problem))
            {
                result.AddError(problem);
            }
        }
    }

    /// <summary>The manifest target when it is in the way, as the writer would report it; otherwise null.</summary>
    private static ScriptExportConflictException? FindManifestConflict(string outputDirectory, bool overwrite)
    {
        var manifestPath = Path.Combine(Path.GetFullPath(outputDirectory), ScriptExportWriter.ManifestFileName);
        return Directory.Exists(manifestPath) || (!overwrite && File.Exists(manifestPath))
            ? new ScriptExportConflictException([manifestPath])
            : null;
    }

    private static void WriteConflict(IAnsiConsole error, ScriptExportConflictException conflict)
    {
        WriteError(error, conflict.Message);
        const int listed = 10;
        foreach (var path in conflict.ConflictingPaths.Take(listed))
        {
            error.MarkupLine($"  {Markup.Escape(path)}");
        }

        if (conflict.ConflictingPaths.Count > listed)
        {
            error.MarkupLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  ... and {conflict.ConflictingPaths.Count - listed:N0} more"));
        }

        error.MarkupLine("Pass --overwrite to replace them, or choose another --output directory.");
    }

    private static void WriteError(IAnsiConsole error, string message)
    {
        error.MarkupLine($"[red]Error:[/] {Markup.Escape(message)}");
    }

    private static void WriteSummary(
        IAnsiConsole output,
        ScriptExportSummary summary,
        ScriptExportSource source,
        ScriptExportOptions options)
    {
        var extension = summary.Extension;
        var decompiledExtension = ScriptExportFileNamer.DecompiledInfix + extension;
        var renamed = summary.Entries.Count(entry => entry.NameAdjustments.Count > 0);

        output.MarkupLine(
            $"Exported [bold]{Count(summary.ScriptCount)}[/] script(s) from {Markup.Escape(source.FileName)} " +
            $"to {Markup.Escape(summary.OutputDirectory)}");
        output.MarkupLine($"  Source: {Markup.Escape(DescribeSource(source))}");
        output.MarkupLine(
            $"  Source stored as plugin SCTX (verbatim {Markup.Escape(extension)}): {Count(summary.StoredSourceFiles)}");
        output.MarkupLine(
            $"  Captured source, recovered from the dump (verbatim {Markup.Escape(extension)}): " +
            Count(summary.CapturedSourceFiles));
        output.MarkupLine(
            $"  Decompiled reconstructions ({Markup.Escape(decompiledExtension)}, --decompiled " +
            $"{ScriptExportOptions.FormatDecompiledPolicy(options.Decompiled)}): {Count(summary.DecompiledFiles)}");
        output.MarkupLine($"  Skipped (no file written): {Count(summary.SkippedScripts)}");
        foreach (var reason in summary.Entries
                     .Where(entry => entry.SkipReason is not null)
                     .GroupBy(entry => entry.SkipReason!, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            output.MarkupLine($"    {Markup.Escape(reason.Key)}: {Count(reason.Count())}");
        }

        output.MarkupLine($"  Not named after their EditorID: {Count(renamed)} (see nameAdjustments in the manifest)");
        output.MarkupLine($"  Files written: {Count(summary.FilesWritten)} plus {ScriptExportWriter.ManifestFileName}");
        output.MarkupLine($"  Manifest: {Markup.Escape(summary.ManifestPath)}");
        if (source.IsMemoryDump)
        {
            output.MarkupLine($"  [yellow]Note:[/] {Markup.Escape(ScriptExportSource.PartialCaptureNote)}");
        }
    }

    private static string DescribeSource(ScriptExportSource source)
    {
        var kind = source.IsMemoryDump ? "memory dump (partial capture)" : "plugin";
        var byteOrder = source.ContainerEndianness is { } endianness
            ? endianness + "-endian container"
            : "container byte order unknown";
        var build = source.BuildLabel is { } label
            ? $"build '{label}' ({source.BuildLabelSource})"
            : "no build label";
        var dumpBuild = source.IsMemoryDump && source.DumpBuildType is { } buildType ? $", {buildType}" : "";
        return $"{kind}, {source.Game}, {byteOrder}{dumpBuild}, {build}";
    }

    private static string Count(int value)
    {
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>The parsed command line.</summary>
    internal sealed record Request
    {
        /// <summary>The ESM/ESP or DMP to read.</summary>
        public required string InputPath { get; init; }

        /// <summary>The directory to write into.</summary>
        public required string OutputDirectory { get; init; }

        /// <summary>The <c>--ext</c> value, or null for the game's default.</summary>
        public string? Extension { get; init; }

        /// <summary>The <c>--decompiled</c> value: missing, all or none.</summary>
        public string Decompiled { get; init; } = DefaultDecompiledPolicy;

        /// <summary>The <c>--id</c> values.</summary>
        public IReadOnlyList<string> Ids { get; init; } = [];

        /// <summary>The <c>-f</c> EditorID substring, or null.</summary>
        public string? Filter { get; init; }

        /// <summary>Whether existing files may be replaced.</summary>
        public bool Overwrite { get; init; }

        /// <summary>The <c>--build-label</c> value, or null.</summary>
        public string? BuildLabel { get; init; }
    }

    /// <summary>One parsed <c>--id</c>: a FormID or an EditorID.</summary>
    /// <param name="Text">The value as given (trimmed).</param>
    /// <param name="FormId">The FormID for a <c>0x</c> value; null for an EditorID.</param>
    /// <param name="EditorId">The EditorID to match exactly, ignoring case; null for a FormID.</param>
    internal sealed record IdSelector(string Text, uint? FormId, string? EditorId)
    {
        /// <summary>Whether <paramref name="script" /> is the one this selector names.</summary>
        public bool Matches(ScriptRecord script)
        {
            return FormId is { } formId
                ? script.FormId == formId
                : string.Equals(script.EditorId, EditorId, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The scripts selected for export and the selectors that matched none.</summary>
    internal sealed record Selection(IReadOnlyList<ScriptRecord> Scripts, IReadOnlyList<IdSelector> UnmatchedIds);

    /// <summary>What one export run returned.</summary>
    /// <param name="ExitCode">The process exit code.</param>
    /// <param name="Summary">What was written; null when the export failed.</param>
    internal sealed record Outcome(int ExitCode, ScriptExportSummary? Summary)
    {
        /// <summary>A failed run: exit code 1 and no summary.</summary>
        public static Outcome Failed { get; } = new(1, null);
    }

    private sealed record ValidatedRequest(
        string? Extension,
        ScriptDecompiledPolicy Decompiled,
        IReadOnlyList<IdSelector> Selectors);
}
