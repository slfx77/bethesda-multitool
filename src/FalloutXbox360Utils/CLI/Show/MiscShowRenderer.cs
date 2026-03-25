using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

/// <summary>
///     Show renderers for miscellaneous record types: SOUN, EXPL, MESG, CHAL, and generic fallback.
/// </summary>
internal static class MiscShowRenderer
{
    internal static bool TryShowSound(RecordCollection records, FormIdResolver _,
        uint? formId, string? editorId)
    {
        var snd = records.Sounds.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, s => s.FormId, s => s.EditorId));
        if (snd == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]      0x{snd.FormId:X8}",
            $"[cyan]EditorID:[/]    {Markup.Escape(snd.EditorId ?? "(none)")}",
            $"[cyan]File:[/]        {Markup.Escape(snd.FileName ?? "(none)")}",
            $"[cyan]Min Atten:[/]   {snd.MinAttenuationDistance * 5}",
            $"[cyan]Max Atten:[/]   {snd.MaxAttenuationDistance * 5}"
        };

        if (snd.StaticAttenuation != 0)
        {
            lines.Add($"[cyan]Static Atten:[/] {snd.StaticAttenuation / 100.0:F2} dB");
        }

        if (snd.Flags != 0)
        {
            lines.Add(
                $"[cyan]Flags:[/]       {FlagRegistry.DecodeFlagNamesWithHex(snd.Flags, FlagRegistry.SoundFlags)}");
        }

        if (snd.StartTime != 0 || snd.EndTime != 0)
        {
            lines.Add($"[cyan]Play Hours:[/]  {snd.StartTime}:00 \u2013 {snd.EndTime}:00");
        }

        if (snd.RandomPercentChance != 0)
        {
            lines.Add($"[cyan]Random %:[/]    {snd.RandomPercentChance}%");
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader($"[bold]SOUN[/] {Markup.Escape(snd.EditorId ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }

    internal static bool TryShowExplosion(RecordCollection records, FormIdResolver resolver,
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

        // FormID references
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

    internal static bool TryShowMessage(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var msg = records.Messages.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, m => m.FormId, m => m.EditorId));
        if (msg == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]    0x{msg.FormId:X8}",
            $"[cyan]EditorID:[/]  {Markup.Escape(msg.EditorId ?? "(none)")}",
            $"[cyan]Title:[/]     {Markup.Escape(msg.FullName ?? "(none)")}"
        };

        var flags = new List<string>();
        if (msg.IsMessageBox)
        {
            flags.Add("Message Box");
        }

        if (msg.IsAutoDisplay)
        {
            flags.Add("Auto Display");
        }

        if (flags.Count > 0)
        {
            lines.Add($"[cyan]Flags:[/]     {string.Join(", ", flags)}");
        }

        if (msg.DisplayTime != 0)
        {
            lines.Add($"[cyan]Display:[/]   {msg.DisplayTime} seconds");
        }

        if (msg.QuestFormId != 0)
        {
            lines.Add($"[cyan]Quest:[/]     {resolver.FormatWithEditorId(msg.QuestFormId)}");
        }

        if (!string.IsNullOrEmpty(msg.Description))
        {
            var text = msg.Description.Length > 2000
                ? msg.Description[..2000] + "\n... (truncated)"
                : msg.Description;
            lines.Add("");
            lines.Add("[bold]Text:[/]");
            lines.Add(Markup.Escape(text));
        }

        if (msg.Buttons.Count > 0)
        {
            lines.Add("");
            lines.Add($"[bold]Buttons ({msg.Buttons.Count}):[/]");
            for (var i = 0; i < msg.Buttons.Count; i++)
            {
                lines.Add($"  [{i + 1}] {Markup.Escape(msg.Buttons[i])}");
            }
        }

        if (!string.IsNullOrEmpty(msg.Icon))
        {
            lines.Add($"[cyan]Icon:[/]      {Markup.Escape(msg.Icon)}");
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]MESG[/] {Markup.Escape(msg.EditorId ?? "")} — {Markup.Escape(msg.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }

    internal static bool TryShowChallenge(RecordCollection records, FormIdResolver resolver,
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

    internal static bool TryShowGeneric(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        // Try all remaining types via flat list
        var flat = RecordFlattener.Flatten(records);
        var match = flat.FirstOrDefault(r =>
            (formId.HasValue && r.FormId == formId.Value) ||
            (editorId != null && (r.EditorId?.Equals(editorId, StringComparison.OrdinalIgnoreCase) ?? false)));

        if (match == null)
        {
            return false;
        }

        // Already shown by a specialized TryShow* method above?
        if (match.Type is "NPC_" or "RACE" or "QUST" or "FACT" or "DIAL" or "WEAP" or "ARMO" or "SCPT"
            or "BOOK" or "SOUN" or "EXPL" or "MESG" or "CHAL" or "RCPE")
        {
            return false;
        }

        // Try to find the full GenericEsmRecord for PDB field data
        var genericRecord = records.GenericRecords
            .FirstOrDefault(r => r.FormId == match.FormId);

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]    0x{match.FormId:X8}",
            $"[cyan]Type:[/]      {Markup.Escape(match.Type)}",
            $"[cyan]EditorID:[/]  {Markup.Escape(match.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]      {Markup.Escape(match.DisplayName ?? "(none)")}"
        };

        if (genericRecord?.ModelPath != null)
        {
            lines.Add($"[cyan]Model:[/]     {Markup.Escape(genericRecord.ModelPath)}");
        }

        // Display PDB-derived runtime fields grouped by owner class
        if (genericRecord?.Fields is { Count: > 0 })
        {
            lines.Add("");
            ShowHelpers.AppendPdbFields(lines, genericRecord.Fields, resolver);
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]{Markup.Escape(match.Type)}[/] {Markup.Escape(match.EditorId ?? $"0x{match.FormId:X8}")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}
