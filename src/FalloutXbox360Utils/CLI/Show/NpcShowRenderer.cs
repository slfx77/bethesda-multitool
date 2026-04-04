using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

internal sealed class NpcShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var npc = records.Npcs.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, n => n.FormId, n => n.EditorId));
        if (npc == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var panel = new Panel(BuildContent(npc, resolver))
        {
            Header = new PanelHeader(
                $"[bold]NPC_[/] {Markup.Escape(npc.EditorId ?? "")} — {Markup.Escape(npc.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }

    private static string BuildContent(NpcRecord npc, FormIdResolver resolver)
    {
        var isFemale = (npc.Stats?.Flags & 1) != 0;
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]   0x{npc.FormId:X8}",
            $"[cyan]EditorID:[/] {Markup.Escape(npc.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]     {Markup.Escape(npc.FullName ?? "(none)")}",
            $"[cyan]Race:[/]     {(npc.Race.HasValue ? resolver.FormatWithEditorId(npc.Race.Value) : "(none)")}",
            $"[cyan]Class:[/]    {(npc.Class.HasValue ? resolver.FormatWithEditorId(npc.Class.Value) : "(none)")}",
            $"[cyan]Female:[/]   {isFemale}",
            $"[cyan]Level:[/]    {npc.Stats?.Level.ToString() ?? "(unknown)"}"
        };

        if (npc.HairFormId.HasValue)
        {
            lines.Add($"[cyan]Hair:[/]       {resolver.FormatWithEditorId(npc.HairFormId.Value)}");
        }

        var hairColorStr = NpcRecord.FormatHairColor(npc.HairColor);
        if (hairColorStr != null)
        {
            lines.Add($"[cyan]Hair Color:[/] {Markup.Escape(hairColorStr)}");
        }

        if (npc.EyesFormId.HasValue)
        {
            lines.Add($"[cyan]Eyes:[/]       {resolver.FormatWithEditorId(npc.EyesFormId.Value)}");
        }

        if (npc.HeadPartFormIds is { Count: > 0 })
        {
            lines.Add("[cyan]Head Parts:[/]");
            foreach (var hdptId in npc.HeadPartFormIds)
                lines.Add($"  {resolver.FormatWithEditorId(hdptId)}");
        }

        if (npc.Height.HasValue)
        {
            lines.Add($"[cyan]Height:[/]     {npc.Height.Value:F2}");
        }

        if (npc.Weight.HasValue)
        {
            lines.Add($"[cyan]Weight:[/]     {npc.Weight.Value:F1}");
        }

        if (npc.OriginalRace.HasValue)
        {
            lines.Add($"[cyan]Original Race:[/] {resolver.FormatWithEditorId(npc.OriginalRace.Value)}");
        }

        if (npc.FaceNpc.HasValue)
        {
            lines.Add($"[cyan]Face NPC:[/]   {resolver.FormatWithEditorId(npc.FaceNpc.Value)}");
        }

        if (npc.RaceFacePreset.HasValue)
        {
            lines.Add($"[cyan]Race Preset:[/] {npc.RaceFacePreset.Value}");
        }

        if (npc.SpecialStats is { Length: >= 7 })
        {
            var names = new[] { "ST", "PE", "EN", "CH", "IN", "AG", "LK" };
            lines.Add("");
            lines.Add("[bold]S.P.E.C.I.A.L.:[/]");
            for (var i = 0; i < 7; i++)
            {
                lines.Add($"  {names[i]}: {npc.SpecialStats[i]}");
            }
        }

        if (npc.Skills is { Length: >= 13 })
        {
            lines.Add("");
            lines.Add("[bold]Skills:[/]");
            var hasBigGuns = resolver.SkillEra?.BigGunsActive ?? false;
            for (var i = 0; i < npc.Skills.Length && i < 14; i++)
            {
                if (i == 1 && !hasBigGuns)
                {
                    continue; // Skip Big Guns slot when merged into Guns
                }

                var skillName = resolver.GetSkillName(i) ?? $"Skill#{i}";
                lines.Add($"  {skillName}: {npc.Skills[i]}");
            }
        }

        if (npc.Factions is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("[bold]Factions:[/]");
            foreach (var faction in npc.Factions)
            {
                lines.Add($"  {resolver.FormatWithEditorId(faction.FactionFormId)} (rank {faction.Rank})");
            }
        }

        if (npc.Inventory is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("[bold]Inventory:[/]");
            foreach (var item in npc.Inventory)
            {
                lines.Add($"  {resolver.FormatWithEditorId(item.ItemFormId)} x{item.Count}");
            }
        }

        // FaceGen morph data
        foreach (var (label, arr) in new[]
                 {
                     ("FGGS", npc.FaceGenGeometrySymmetric),
                     ("FGGA", npc.FaceGenGeometryAsymmetric),
                     ("FGTS", npc.FaceGenTextureSymmetric)
                 })
        {
            if (arr is { Length: > 0 })
            {
                lines.Add("");
                lines.Add($"[bold]{label} ({arr.Length} floats):[/]");
                for (var row = 0; row < arr.Length; row += 10)
                {
                    var end = Math.Min(row + 10, arr.Length);
                    var vals = string.Join(" ", Enumerable.Range(row, end - row).Select(i => $"{arr[i]:F4}"));
                    lines.Add($"  [[{row,2}]] {vals}");
                }
            }
        }

        return string.Join("\n", lines);
    }
}
