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
                lines.Add($"    Mag: {effect.Magnitude:F1}  Area: {effect.Area}  Dur: {effect.Duration}s  Type: {typeName}");
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

internal sealed class BaseEffectShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var mgef = records.BaseEffects.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, e => e.FormId, e => e.EditorId));
        if (mgef == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
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
            var avName = resolver.GetActorValueName(mgef.ActorValue) ?? $"AV#{mgef.ActorValue}";
            lines.Add($"[cyan]Actor Value:[/] {avName}");
        }

        if (mgef.ResistValue >= 0)
        {
            var resName = resolver.GetActorValueName(mgef.ResistValue) ?? $"AV#{mgef.ResistValue}";
            lines.Add($"[cyan]Resist:[/]      {resName}");
        }

        if (mgef.Flags != 0)
        {
            lines.Add($"[cyan]Flags:[/]       0x{mgef.Flags:X8}");
        }

        if (mgef.Projectile != 0)
        {
            lines.Add($"[cyan]Projectile:[/]  {resolver.FormatWithEditorId(mgef.Projectile)}");
        }

        if (mgef.Explosion != 0)
        {
            lines.Add($"[cyan]Explosion:[/]   {resolver.FormatWithEditorId(mgef.Explosion)}");
        }

        if (mgef.LightFormId is > 0)
        {
            lines.Add($"[cyan]Light:[/]       {resolver.FormatWithEditorId(mgef.LightFormId.Value)}");
        }

        if (mgef.CastingSoundFormId is > 0)
        {
            lines.Add($"[cyan]Cast Sound:[/]  {resolver.FormatWithEditorId(mgef.CastingSoundFormId.Value)}");
        }

        if (mgef.HitSoundFormId is > 0)
        {
            lines.Add($"[cyan]Hit Sound:[/]   {resolver.FormatWithEditorId(mgef.HitSoundFormId.Value)}");
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
                lines.Add($"  {resolver.FormatWithEditorId(counter)}");
            }
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]MGEF[/] {Markup.Escape(mgef.EditorId ?? "")} — {Markup.Escape(mgef.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}

internal sealed class PerkShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var perk = records.Perks.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, p => p.FormId, p => p.EditorId));
        if (perk == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]      0x{perk.FormId:X8}",
            $"[cyan]EditorID:[/]    {Markup.Escape(perk.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]        {Markup.Escape(perk.FullName ?? "(none)")}",
            $"[cyan]Ranks:[/]       {perk.Ranks}",
            $"[cyan]Min Level:[/]   {perk.MinLevel}",
            $"[cyan]Playable:[/]    {perk.IsPlayable}",
            $"[cyan]Trait:[/]       {perk.IsTrait}"
        };

        if (!string.IsNullOrEmpty(perk.Description))
        {
            lines.Add("");
            lines.Add("[bold]Description:[/]");
            lines.Add($"  {Markup.Escape(perk.Description)}");
        }

        if (perk.Entries.Count > 0)
        {
            lines.Add("");
            lines.Add("[bold]Entries:[/]");
            foreach (var entry in perk.Entries)
            {
                var entryLine = $"  [{entry.TypeName}] Rank {entry.Rank}, Priority {entry.Priority}";
                if (entry.AbilityFormId is > 0)
                {
                    entryLine += $" \u2192 {resolver.FormatWithEditorId(entry.AbilityFormId.Value)}";
                }

                lines.Add(entryLine);
            }
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]PERK[/] {Markup.Escape(perk.EditorId ?? "")} — {Markup.Escape(perk.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}

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

        if (proj.ImpactForce != 0)
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
