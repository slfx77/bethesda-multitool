using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Show;

internal sealed class ProjectileShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId, ShowRenderContext context)
    {
        var proj = records.Projectiles.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, p => p.FormId, p => p.EditorId));
        if (proj == null)
        {
            return false;
        }

        context.Console.WriteLine();
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
            lines.Add($"[cyan]Explosion:[/]   {ShowHelpers.Ref(resolver, proj.Explosion)}");
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
            lines.Add($"[cyan]Light:[/]       {ShowHelpers.Ref(resolver, proj.Light)}");
        }

        if (proj.MuzzleFlashLight != 0)
        {
            lines.Add($"[cyan]Muzzle Flash:[/] {ShowHelpers.Ref(resolver, proj.MuzzleFlashLight)}");
        }

        if (proj.Sound != 0)
        {
            lines.Add($"[cyan]Sound:[/]       {ShowHelpers.Ref(resolver, proj.Sound)}");
        }

        if (!string.IsNullOrEmpty(proj.ModelPath))
        {
            lines.Add($"[cyan]Model:[/]       {Markup.Escape(proj.ModelPath)}");
        }

        ShowHelpers.AppendNestedPayloads(lines, records, proj.FormId, resolver);

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]PROJ[/] {Markup.Escape(proj.EditorId ?? "")} — {Markup.Escape(proj.FullName ?? "")}")
        };
        context.Console.Write(panel);
        return true;
    }
}
