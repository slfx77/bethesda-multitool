using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

internal sealed class ChallengeShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var chal = records.Challenges.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, c => c.FormId, c => c.EditorId));
        if (chal == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]    0x{chal.FormId:X8}",
            $"[cyan]EditorID:[/]  {Markup.Escape(chal.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]      {Markup.Escape(chal.FullName ?? "(none)")}",
            $"[cyan]Type:[/]      {chal.TypeName}",
            $"[cyan]Threshold:[/] {chal.Threshold}"
        };

        if (chal.Interval != 0)
        {
            lines.Add($"[cyan]Interval:[/]  {chal.Interval}");
        }

        if (chal.Flags != 0)
        {
            lines.Add(
                $"[cyan]Flags:[/]     {FlagRegistry.DecodeFlagNamesWithHex(chal.Flags, FlagRegistry.ChallengeFlags)}");
        }

        if (chal.Value1 != 0)
        {
            lines.Add($"[cyan]Value 1:[/]   {resolver.FormatWithEditorId(chal.Value1)}");
        }

        if (chal.Value2 != 0)
        {
            lines.Add($"[cyan]Value 2:[/]   {chal.Value2}");
        }

        if (chal.Value3 != 0)
        {
            lines.Add($"[cyan]Value 3:[/]   {chal.Value3}");
        }

        if (chal.Script != 0)
        {
            lines.Add($"[cyan]Script:[/]    {resolver.FormatWithEditorId(chal.Script)}");
        }

        if (!string.IsNullOrEmpty(chal.Description))
        {
            lines.Add("");
            lines.Add("[bold]Description:[/]");
            lines.Add(Markup.Escape(chal.Description));
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]CHAL[/] {Markup.Escape(chal.EditorId ?? "")} — {Markup.Escape(chal.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}
