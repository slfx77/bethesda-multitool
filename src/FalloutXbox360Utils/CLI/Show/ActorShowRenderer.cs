using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Show;

/// <summary>
///     Show renderers for actor-related record types: NPC_, RACE, FACT, SCPT.
/// </summary>
internal static class ActorShowRenderer
{
    internal static bool TryShowNpc(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var npc = records.Npcs.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, n => n.FormId, n => n.EditorId));
        if (npc == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var panel = new Panel(BuildNpcContent(npc, resolver))
        {
            Header = new PanelHeader(
                $"[bold]NPC_[/] {Markup.Escape(npc.EditorId ?? "")} — {Markup.Escape(npc.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }

    private static string BuildNpcContent(NpcRecord npc, FormIdResolver resolver)
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
            for (var i = 0; i < npc.Skills.Length && i < 14; i++)
            {
                if (i == 1)
                {
                    continue; // Skip Big Guns (unused in FNV)
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

    internal static bool TryShowRace(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var race = records.Races.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, n => n.FormId, n => n.EditorId));
        if (race == null)
            return false;

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]      0x{race.FormId:X8}",
            $"[cyan]EditorID:[/]    {Markup.Escape(race.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]        {Markup.Escape(race.FullName ?? "(none)")}",
            $"[cyan]Playable:[/]    {race.IsPlayable}",
            $"[cyan]Height:[/]      M={race.MaleHeight:F2}  F={race.FemaleHeight:F2}",
            $"[cyan]Weight:[/]      M={race.MaleWeight:F2}  F={race.FemaleWeight:F2}",
            $"[cyan]Flags:[/]       0x{race.DataFlags:X8}"
        };

        if (race.OlderRaceFormId.HasValue)
            lines.Add($"[cyan]Older Race:[/]  {resolver.FormatWithEditorId(race.OlderRaceFormId.Value)}");
        if (race.YoungerRaceFormId.HasValue)
            lines.Add($"[cyan]Younger Race:[/] {resolver.FormatWithEditorId(race.YoungerRaceFormId.Value)}");

        // Head meshes
        lines.Add("");
        lines.Add("[bold]Head Parts (NAM0):[/]");
        lines.Add($"  [cyan]Male Head Mesh:[/]    {Markup.Escape(race.MaleHeadModelPath ?? "(none)")}");
        lines.Add($"  [cyan]Female Head Mesh:[/]  {Markup.Escape(race.FemaleHeadModelPath ?? "(none)")}");
        lines.Add($"  [cyan]Male Head Tex:[/]     {Markup.Escape(race.MaleHeadTexturePath ?? "(none)")}");
        lines.Add($"  [cyan]Female Head Tex:[/]   {Markup.Escape(race.FemaleHeadTexturePath ?? "(none)")}");
        lines.Add($"  [cyan]Male Mouth Mesh:[/]   {Markup.Escape(race.MaleMouthModelPath ?? "(none)")}");
        lines.Add($"  [cyan]Female Mouth Mesh:[/] {Markup.Escape(race.FemaleMouthModelPath ?? "(none)")}");
        lines.Add($"  [cyan]Male Lower Teeth:[/]  {Markup.Escape(race.MaleLowerTeethModelPath ?? "(none)")}");
        lines.Add($"  [cyan]Female Lower Teeth:[/] {Markup.Escape(race.FemaleLowerTeethModelPath ?? "(none)")}");
        lines.Add($"  [cyan]Male Upper Teeth:[/]  {Markup.Escape(race.MaleUpperTeethModelPath ?? "(none)")}");
        lines.Add($"  [cyan]Female Upper Teeth:[/] {Markup.Escape(race.FemaleUpperTeethModelPath ?? "(none)")}");
        lines.Add($"  [cyan]Male Tongue:[/]       {Markup.Escape(race.MaleTongueModelPath ?? "(none)")}");
        lines.Add($"  [cyan]Female Tongue:[/]     {Markup.Escape(race.FemaleTongueModelPath ?? "(none)")}");

        // Body meshes
        lines.Add("");
        lines.Add("[bold]Body Parts (NAM1):[/]");
        lines.Add($"  [cyan]Male Upper Body:[/]   {Markup.Escape(race.MaleUpperBodyPath ?? "(none)")}");
        lines.Add($"  [cyan]Female Upper Body:[/] {Markup.Escape(race.FemaleUpperBodyPath ?? "(none)")}");
        lines.Add($"  [cyan]Male Left Hand:[/]    {Markup.Escape(race.MaleLeftHandPath ?? "(none)")}");
        lines.Add($"  [cyan]Female Left Hand:[/]  {Markup.Escape(race.FemaleLeftHandPath ?? "(none)")}");
        lines.Add($"  [cyan]Male Right Hand:[/]   {Markup.Escape(race.MaleRightHandPath ?? "(none)")}");
        lines.Add($"  [cyan]Female Right Hand:[/] {Markup.Escape(race.FemaleRightHandPath ?? "(none)")}");
        lines.Add($"  [cyan]Male Body Tex:[/]     {Markup.Escape(race.MaleBodyTexturePath ?? "(none)")}");
        lines.Add($"  [cyan]Female Body Tex:[/]   {Markup.Escape(race.FemaleBodyTexturePath ?? "(none)")}");

        // Abilities
        if (race.AbilityFormIds.Count > 0)
        {
            lines.Add("");
            lines.Add("[bold]Abilities:[/]");
            foreach (var abilId in race.AbilityFormIds)
                lines.Add($"  {resolver.FormatWithEditorId(abilId)}");
        }

        // Skill Boosts
        if (race.SkillBoosts.Count > 0)
        {
            lines.Add("");
            lines.Add("[bold]Skill Boosts:[/]");
            foreach (var (skillIndex, boost) in race.SkillBoosts)
                lines.Add($"  Skill {skillIndex}: {(boost > 0 ? "+" : "")}{boost}");
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]RACE[/] {Markup.Escape(race.EditorId ?? "")} — {Markup.Escape(race.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }

    internal static bool TryShowFaction(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var faction =
            records.Factions.FirstOrDefault(r =>
                ShowHelpers.Matches(r, formId, editorId, f => f.FormId, f => f.EditorId));
        if (faction == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]   0x{faction.FormId:X8}",
            $"[cyan]EditorID:[/] {Markup.Escape(faction.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]     {Markup.Escape(faction.FullName ?? "(none)")}",
            $"[cyan]Flags:[/]    0x{faction.Flags:X4}"
        };

        if (faction.Relations is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("[bold]Relations:[/]");
            foreach (var rel in faction.Relations)
            {
                lines.Add(
                    $"  {resolver.FormatWithEditorId(rel.FactionFormId)}: {rel.Modifier} (combat: 0x{rel.CombatFlags:X})");
            }
        }

        if (faction.Ranks is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("[bold]Ranks:[/]");
            foreach (var rank in faction.Ranks)
            {
                lines.Add(
                    $"  [[{rank.RankNumber}]] {Markup.Escape(rank.MaleTitle ?? rank.FemaleTitle ?? "(unnamed)")}");
            }
        }

        // Reverse lookup: find all NPCs and Creatures that belong to this faction
        var members = new List<(string type, string label, sbyte rank)>();
        foreach (var npc in records.Npcs)
        {
            var membership = npc.Factions.FirstOrDefault(f => f.FactionFormId == faction.FormId);
            if (membership != null)
            {
                var label = resolver.FormatWithEditorId(npc.FormId);
                members.Add(("NPC_", label, membership.Rank));
            }
        }

        foreach (var creature in records.Creatures)
        {
            var membership = creature.Factions.FirstOrDefault(f => f.FactionFormId == faction.FormId);
            if (membership != null)
            {
                var label = resolver.FormatWithEditorId(creature.FormId);
                members.Add(("CREA", label, membership.Rank));
            }
        }

        if (members.Count > 0)
        {
            lines.Add("");
            lines.Add($"[bold]Members ({members.Count}):[/]");
            foreach (var (type, label, rank) in members.OrderBy(m => m.type).ThenBy(m => m.label))
            {
                lines.Add($"  [grey]{type}[/] {label} (rank {rank})");
            }
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]FACT[/] {Markup.Escape(faction.EditorId ?? "")} — {Markup.Escape(faction.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }

    internal static bool TryShowScript(RecordCollection records, FormIdResolver _,
        uint? formId, string? editorId)
    {
        var script = records.Scripts.FirstOrDefault(r =>
            ShowHelpers.Matches(r, formId, editorId, s => s.FormId, s => s.EditorId));
        if (script == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]     0x{script.FormId:X8}",
            $"[cyan]EditorID:[/]   {Markup.Escape(script.EditorId ?? "(none)")}",
            $"[cyan]Type:[/]       {script.ScriptType}",
            $"[cyan]Variables:[/]  {script.VariableCount}",
            $"[cyan]RefCount:[/]   {script.RefObjectCount}",
            $"[cyan]Compiled:[/]   {script.CompiledSize} bytes"
        };

        if (!string.IsNullOrEmpty(script.SourceText))
        {
            lines.Add("");
            lines.Add("[bold]Source (SCTX):[/]");
            // Truncate long scripts
            var source = script.SourceText;
            if (source.Length > 2000)
            {
                source = source[..2000] + "\n... (truncated)";
            }

            lines.Add(Markup.Escape(source));
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader($"[bold]SCPT[/] {Markup.Escape(script.EditorId ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }
}
