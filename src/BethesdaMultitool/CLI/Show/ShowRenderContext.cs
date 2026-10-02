using Spectre.Console;

namespace BethesdaMultitool.CLI.Show;

/// <summary>
///     Per-invocation state handed to every <see cref="IRecordDisplayRenderer" /> by <c>show</c>.
///     <para>
///         Renderers write through <c>Console</c> and never through the static
///         <see cref="AnsiConsole" />, so a test can capture one render into its own console without
///         swapping global state that parallel test classes share.
///     </para>
/// </summary>
/// <param name="Console">The console every renderer writes to.</param>
/// <param name="FullText">
///     When true, long text blocks are written in full instead of being truncated for the panel
///     (reserved for <c>show --full</c>).
/// </param>
/// <param name="IsMemoryDumpInput">
///     True when the analyzed input is a memory dump. Plugin SCTX and dump-recovered text can both
///     carry no source-text origin, and this is what tells them apart.
/// </param>
internal sealed record ShowRenderContext(IAnsiConsole Console, bool FullText = false, bool IsMemoryDumpInput = false);
