using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

internal sealed class ExplosionShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var expl = records.Explosions.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, e => e.FormId, e => e.EditorId));
        if (expl == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]     0x{expl.FormId:X8}",
            $"[cyan]EditorID:[/]   {Markup.Escape(expl.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]       {Markup.Escape(expl.FullName ?? "(none)")}",
            "",
            "[bold]Stats:[/]",
            $"  [cyan]Force:[/]     {expl.Force:F1}",
            $"  [cyan]Damage:[/]    {expl.Damage:F1}",
            $"  [cyan]Radius:[/]    {expl.Radius:F1}",
            $"  [cyan]IS Radius:[/] {expl.ISRadius:F1}"
        };

        if (expl.Flags != 0)
        {
            lines.Add(
                $"  [cyan]Flags:[/]     {FlagRegistry.DecodeFlagNamesWithHex(expl.Flags, FlagRegistry.ExplosionFlags)}");
        }

        void AddRef(string label, uint fid)
        {
            if (fid != 0)
            {
                lines.Add($"  [cyan]{label}:[/] {resolver.FormatWithEditorId(fid)}");
            }
        }

        lines.Add("");
        lines.Add("[bold]References:[/]");
        AddRef("Light      ", expl.Light);
        AddRef("Sound 1    ", expl.Sound1);
        AddRef("Sound 2    ", expl.Sound2);
        AddRef("Impact Data", expl.ImpactDataSet);
        AddRef("Enchantment", expl.Enchantment);

        if (!string.IsNullOrEmpty(expl.ModelPath))
        {
            lines.Add($"  [cyan]Model:[/]      {Markup.Escape(expl.ModelPath)}");
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]EXPL[/] {Markup.Escape(expl.EditorId ?? "")} — {Markup.Escape(expl.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}
