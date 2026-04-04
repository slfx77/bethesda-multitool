using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

internal sealed class ProjectileShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var proj = records.Projectiles.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, p => p.FormId, p => p.EditorId));
        if (proj == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]      0x{proj.FormId:X8}",
            $"[cyan]EditorID:[/]    {Markup.Escape(proj.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]        {Markup.Escape(proj.FullName ?? "(none)")}",
            $"[cyan]Type:[/]        {proj.TypeName}",
            $"[cyan]Speed:[/]       {proj.Speed:F1}",
            $"[cyan]Gravity:[/]     {proj.Gravity:F4}",
            $"[cyan]Range:[/]       {proj.Range:F1}"
        };

        if (proj.ImpactForce is not 0f)
        {
            lines.Add($"[cyan]Impact Force:[/] {proj.ImpactForce:F1}");
        }

        if (proj.TracerChance > 0)
        {
            lines.Add($"[cyan]Tracer %:[/]    {proj.TracerChance * 100:F0}%");
        }

        if (proj.Flags != 0)
        {
            lines.Add($"[cyan]Flags:[/]       0x{proj.Flags:X4}");
        }

        if (proj.Explosion != 0)
        {
            lines.Add($"[cyan]Explosion:[/]   {resolver.FormatWithEditorId(proj.Explosion)}");
            if (proj.ExplosionTimer > 0)
            {
                lines.Add($"[cyan]Expl Timer:[/]  {proj.ExplosionTimer:F2}s");
            }

            if (proj.ExplosionProximity > 0)
            {
                lines.Add($"[cyan]Expl Prox:[/]   {proj.ExplosionProximity:F1}");
            }
        }

        if (proj.Light != 0)
        {
            lines.Add($"[cyan]Light:[/]       {resolver.FormatWithEditorId(proj.Light)}");
        }

        if (proj.MuzzleFlashLight != 0)
        {
            lines.Add($"[cyan]Muzzle Flash:[/] {resolver.FormatWithEditorId(proj.MuzzleFlashLight)}");
        }

        if (proj.Sound != 0)
        {
            lines.Add($"[cyan]Sound:[/]       {resolver.FormatWithEditorId(proj.Sound)}");
        }

        if (!string.IsNullOrEmpty(proj.ModelPath))
        {
            lines.Add($"[cyan]Model:[/]       {Markup.Escape(proj.ModelPath)}");
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]PROJ[/] {Markup.Escape(proj.EditorId ?? "")} — {Markup.Escape(proj.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}
