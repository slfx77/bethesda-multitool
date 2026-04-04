using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

internal sealed class EnchantmentShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var ench = records.Enchantments.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, e => e.FormId, e => e.EditorId));
        if (ench == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]      0x{ench.FormId:X8}",
            $"[cyan]EditorID:[/]    {Markup.Escape(ench.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]        {Markup.Escape(ench.FullName ?? "(none)")}",
            $"[cyan]Type:[/]        {ench.TypeName}",
            $"[cyan]Charge:[/]      {ench.ChargeAmount}",
            $"[cyan]Cost:[/]        {ench.EnchantCost}"
        };

        if (ench.Flags != 0)
        {
            lines.Add($"[cyan]Flags:[/]       0x{ench.Flags:X2}");
        }

        if (ench.Effects.Count > 0)
        {
            lines.Add("");
            lines.Add("[bold]Effects:[/]");
            foreach (var effect in ench.Effects)
            {
                var effectName = effect.EffectFormId != 0
                    ? resolver.FormatWithEditorId(effect.EffectFormId)
                    : "(none)";
                var typeName = effect.Type switch { 0 => "Self", 1 => "Touch", 2 => "Target", _ => $"#{effect.Type}" };
                lines.Add($"  {Markup.Escape(effectName)}");
                lines.Add(
                    $"    Mag: {effect.Magnitude:F1}  Area: {effect.Area}  Dur: {effect.Duration}s  Type: {typeName}");
            }
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]ENCH[/] {Markup.Escape(ench.EditorId ?? "")} — {Markup.Escape(ench.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}
