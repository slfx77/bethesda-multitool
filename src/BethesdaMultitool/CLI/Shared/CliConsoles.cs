using System.Reflection;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Shared;

/// <summary>
///     Console routing and stamping shared by commands that emit machine-readable output.
///     <para>
///         A command in a machine format (<c>-f json</c>) owns stdout for exactly one document, so its
///         progress bars, status lines, errors and <c>Logger</c> output must go
///         to stderr instead. <see cref="Stderr" /> is the Spectre console for that; pair it with
///         <c>Logger.SetOutput(Console.Error)</c> and write the document to
///         <see cref="Console.OpenStandardOutput()" />.
///     </para>
/// </summary>
internal static class CliConsoles
{
    /// <summary>
    ///     The version written as <c>toolVersion</c> into machine-readable CLI documents: the assembly's
    ///     informational version (which carries the source revision when the build stamps one), else its
    ///     assembly version.
    /// </summary>
    internal static string ToolVersion { get; } = ResolveToolVersion();

    /// <summary>
    ///     A new plain-text Spectre console bound to the CURRENT <see cref="Console.Error" /> writer: no
    ///     ANSI, no colour and not interactive, so a progress bar degrades to the line-by-line fallback
    ///     renderer. Created per access, so a caller that redirected <see cref="Console.Error" /> (a test)
    ///     is honoured rather than the writer captured by an earlier call.
    /// </summary>
    internal static IAnsiConsole Stderr => AnsiConsole.Create(new AnsiConsoleSettings
    {
        Out = new AnsiConsoleOutput(Console.Error),
        Ansi = AnsiSupport.No,
        ColorSystem = ColorSystemSupport.NoColors,
        Interactive = InteractionSupport.No
    });

    /// <summary>
    ///     The console a command's status and progress output belongs on: <see cref="Stderr" /> when stdout
    ///     carries a machine-readable document, otherwise the shared <see cref="AnsiConsole.Console" />.
    /// </summary>
    internal static IAnsiConsole ForStatus(bool stdoutCarriesDocument)
    {
        return stdoutCarriesDocument ? Stderr : AnsiConsole.Console;
    }

    private static string ResolveToolVersion()
    {
        var assembly = typeof(CliConsoles).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
