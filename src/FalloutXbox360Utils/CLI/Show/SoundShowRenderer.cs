using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

internal sealed class SoundShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
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
}
