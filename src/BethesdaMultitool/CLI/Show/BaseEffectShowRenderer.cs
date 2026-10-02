using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Show;

internal sealed class BaseEffectShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId, ShowRenderContext context)
    {
        var mgef = records.BaseEffects.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, e => e.FormId, e => e.EditorId));
        if (mgef == null)
        {
            return false;
        }

        context.Console.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]      0x{mgef.FormId:X8}",
            $"[cyan]EditorID:[/]    {Markup.Escape(mgef.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]        {Markup.Escape(mgef.FullName ?? "(none)")}",
            $"[cyan]Archetype:[/]   {mgef.ArchetypeName}",
            $"[cyan]Base Cost:[/]   {mgef.BaseCost:F2}"
        };

        if (!string.IsNullOrEmpty(mgef.EffectCode))
        {
            lines.Add($"[cyan]Effect Code:[/] {Markup.Escape(mgef.EffectCode)}");
        }

        if (mgef.ActorValue >= 0)
        {
            var avName = ShowHelpers.Plain(resolver.GetActorValueName(mgef.ActorValue), $"AV#{mgef.ActorValue}");
            lines.Add($"[cyan]Actor Value:[/] {avName}");
        }

        if (mgef.ResistValue >= 0)
        {
            var resName = ShowHelpers.Plain(resolver.GetActorValueName(mgef.ResistValue), $"AV#{mgef.ResistValue}");
            lines.Add($"[cyan]Resist:[/]      {resName}");
        }

        if (mgef.Flags != 0)
        {
            lines.Add($"[cyan]Flags:[/]       0x{mgef.Flags:X8}");
        }

        if (mgef.Projectile != 0)
        {
            lines.Add($"[cyan]Projectile:[/]  {ShowHelpers.Ref(resolver, mgef.Projectile)}");
        }

        if (mgef.Explosion != 0)
        {
            lines.Add($"[cyan]Explosion:[/]   {ShowHelpers.Ref(resolver, mgef.Explosion)}");
        }

        if (mgef.LightFormId is > 0)
        {
            lines.Add($"[cyan]Light:[/]       {ShowHelpers.Ref(resolver, mgef.LightFormId.Value)}");
        }

        if (mgef.CastingSoundFormId is > 0)
        {
            lines.Add($"[cyan]Cast Sound:[/]  {ShowHelpers.Ref(resolver, mgef.CastingSoundFormId.Value)}");
        }

        if (mgef.HitSoundFormId is > 0)
        {
            lines.Add($"[cyan]Hit Sound:[/]   {ShowHelpers.Ref(resolver, mgef.HitSoundFormId.Value)}");
        }

        if (!string.IsNullOrEmpty(mgef.Description))
        {
            lines.Add("");
            lines.Add("[bold]Description:[/]");
            lines.Add($"  {Markup.Escape(mgef.Description)}");
        }

        if (mgef.CounterEffectFormIds is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("[bold]Counter Effects:[/]");
            foreach (var counter in mgef.CounterEffectFormIds)
            {
                lines.Add($"  {ShowHelpers.Ref(resolver, counter)}");
            }
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]MGEF[/] {Markup.Escape(mgef.EditorId ?? "")} — {Markup.Escape(mgef.FullName ?? "")}")
        };
        context.Console.Write(panel);
        return true;
    }
}
