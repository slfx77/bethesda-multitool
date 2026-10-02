using BethesdaMultitool.Core.Formats.Esm.Export.Geck;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Show;

/// <summary>
///     <c>show</c> for a standalone SCPT record.
///     <para>
///         Source text is labelled by <see cref="ScriptSourceProvenance" />: authored plugin SCTX keeps
///         "SCTX" in its label, text recovered from a memory dump says so (with its correspondence
///         verdict), and a BethesdaMultitool decompilation is never presented as original source. When
///         there is no source text, the panel says so, and for a memory dump it adds that absence from a
///         partial capture is not evidence of absence from the build.
///     </para>
///     <para>
///         By default each body is cut at <see cref="ShowHelpers.DefaultTextCap" /> characters with a
///         marker naming the total and <c>--full</c>. Under <c>--full</c> the panel lists the variables
///         and referenced objects, and each body is written after the panel as a verbatim block
///         (<see cref="ShowHelpers.WriteVerbatimBlocks" />), unwrapped, with its TABs and CRLF intact.
///     </para>
/// </summary>
internal sealed class ScriptShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId, ShowRenderContext context)
    {
        var script = records.Scripts.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, s => s.FormId, s => s.EditorId));
        if (script == null)
        {
            return false;
        }

        context.Console.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]     0x{script.FormId:X8}",
            $"[cyan]EditorID:[/]   {Markup.Escape(script.EditorId ?? "(none)")}",
            $"[cyan]Type:[/]       {script.ScriptType}",
            $"[cyan]Variables:[/]  {script.VariableCount}",
            $"[cyan]RefCount:[/]   {script.RefObjectCount}",
            $"[cyan]Compiled:[/]   {script.CompiledSize} bytes",
            $"[cyan]Compiled flag:[/] {script.IsCompiled}",
            $"[cyan]Bytecode:[/]   {Markup.Escape(FormatBytecodeOrder(script, context.IsMemoryDumpInput))}"
        };

        if (context.FullText)
        {
            AppendVariables(lines, script);
            AppendReferencedObjects(lines, script, resolver);
        }

        var blocks = new List<VerbatimBlock>();
        var source = ScriptSourceProvenance.Classify(script, context.IsMemoryDumpInput);
        if (source.HasSourceText)
        {
            AppendBody(lines, blocks, source.Label, script.SourceText!, context.FullText);
        }
        else
        {
            lines.Add("");
            lines.Add($"[grey]{Markup.Escape(source.Label)}[/]");
        }

        if (!string.IsNullOrEmpty(script.DecompiledText))
        {
            AppendBody(lines, blocks, ScriptSourceProvenance.DecompiledTextLabel, script.DecompiledText,
                context.FullText);
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader($"[bold]SCPT[/] {Markup.Escape(script.EditorId ?? "")}")
        };
        context.Console.Write(panel);
        ShowHelpers.WriteVerbatimBlocks(context.Console, blocks);
        return true;
    }

    /// <summary>
    ///     One body under its label: in the panel (cut at the cap) by default, or, under
    ///     <c>--full</c>, a size note in the panel and the whole text as a verbatim block.
    /// </summary>
    private static void AppendBody(List<string> lines, List<VerbatimBlock> blocks, string label, string text,
        bool fullText)
    {
        lines.Add("");
        if (fullText)
        {
            lines.Add($"[bold]{Markup.Escape(label)}:[/] {ShowHelpers.VerbatimPlaceholder(text)}");
            blocks.Add(new VerbatimBlock(label, text));
            return;
        }

        lines.Add($"[bold]{Markup.Escape(label)}:[/]");
        lines.Add(Markup.Escape(ShowHelpers.TruncateForPanel(text)));
    }

    /// <summary>
    ///     The SLSD/SCVR variables as parsed. The SCHR count is printed beside the listed count because the
    ///     two are different quantities and routinely disagree (a header count can exceed the variables
    ///     that carry a declaration).
    /// </summary>
    private static void AppendVariables(List<string> lines, ScriptRecord script)
    {
        lines.Add("");
        lines.Add(
            $"[bold]Variables ({script.Variables.Count} listed; SCHR VariableCount {script.VariableCount}):[/]");
        if (script.Variables.Count == 0)
        {
            lines.Add("  [grey](none)[/]");
            return;
        }

        foreach (var variable in script.Variables.OrderBy(v => v.Index))
        {
            // Escaped as a whole: "[  3]" would otherwise open a Spectre style.
            var declaration = ScriptVariableTypeResolver.FormatDeclaration(variable, script.ReferencedObjects, alignType: true);
            lines.Add("  " + Markup.Escape($"[{variable.Index,3}] {declaration}"));
        }
    }

    /// <summary>
    ///     The SCRO/SCRV reference list in bytecode order, formatted as the GECK script report formats it
    ///     (SCRV entries name the local variable). Resolver output is data and is escaped.
    /// </summary>
    private static void AppendReferencedObjects(List<string> lines, ScriptRecord script, FormIdResolver resolver)
    {
        lines.Add("");
        lines.Add($"[bold]Referenced Objects ({script.ReferencedObjects.Count}):[/]");
        if (script.ReferencedObjects.Count == 0)
        {
            lines.Add("  [grey](none)[/]");
            return;
        }

        for (var i = 0; i < script.ReferencedObjects.Count; i++)
        {
            var reference = GeckScriptWriter.FormatScriptReference(
                script.ReferencedObjects[i], script.Variables, resolver);
            // The 1-based slot is what the bytecode's reference operands index.
            lines.Add($"  {Markup.Escape($"[{i + 1,3}]")} {Markup.Escape(reference)}");
        }
    }

    /// <summary>
    ///     The byte order the SCDA was decoded in and the rule that chose it, worded as the GECK script
    ///     report's "Bytecode Order:" line. This is not the record container's byte order: an Xbox 360
    ///     ESM is a big-endian container whose serialized bytecode is little-endian.
    /// </summary>
    private static string FormatBytecodeOrder(ScriptRecord script, bool isMemoryDumpInput)
    {
        if (script.CompiledData is not { Length: > 0 })
        {
            return isMemoryDumpInput || script.FromRuntime
                ? $"no SCDA: {ScriptSourceProvenance.PartialDumpAbsenceWording}"
                : "(no SCDA)";
        }

        var order = script.IsBigEndianBytecode ? "Big-Endian" : "Little-Endian";
        var evidence = script.BytecodeByteOrderEvidence switch
        {
            ScriptBytecodeByteOrderEvidence.ScriptNameAnchor => "ScriptName anchor",
            ScriptBytecodeByteOrderEvidence.SingleCleanWalk => "only clean walk",
            ScriptBytecodeByteOrderEvidence.FewerUnknownOpcodes => "fewer unknown opcodes",
            ScriptBytecodeByteOrderEvidence.WalkedToEndInOneOrder => "only walk to reach the end",
            ScriptBytecodeByteOrderEvidence.AmbiguousSerializedDefault => "ambiguous, serialized default",
            ScriptBytecodeByteOrderEvidence.AmbiguousContainerFallback => "ambiguous, container order",
            ScriptBytecodeByteOrderEvidence.RuntimeScriptObject => "runtime Script object",
            _ => "not determined"
        };
        return $"{order} ({evidence})";
    }
}
