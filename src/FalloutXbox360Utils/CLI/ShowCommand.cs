using System.CommandLine;
using System.Globalization;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Format-agnostic record inspection. Works on ESM, DMP, and ESP files.
///     Equivalent to clicking a FormID in the GUI's Data Browser.
/// </summary>
public static class ShowCommand
{
    public static Command Create()
    {
        var command = new Command("show", "Inspect a specific record from any supported file");

        var fileArg = new Argument<string>("file") { Description = "ESM, ESP, or DMP file path" };
        var idArg = new Argument<string>("id") { Description = "FormID (hex, e.g., 0x000F0629) or EditorID (text)" };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(idArg);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var filePath = parseResult.GetValue(fileArg)!;
            var id = parseResult.GetValue(idArg)!;

            return await RunShowAsync(filePath, id, cancellationToken);
        });

        return command;
    }

    private static async Task<int> RunShowAsync(string filePath, string id, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {filePath}");
            return 1;
        }

        var fileType = FileTypeDetector.Detect(filePath);
        AnsiConsole.MarkupLine($"[bold]Show:[/] [cyan]{Path.GetFileName(filePath)}[/] ({fileType}) — {id}");

        try
        {
            using var result = await AnsiConsole.Progress()
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("Analyzing...", maxValue: 100);
                    var progress = new Progress<AnalysisProgress>(p =>
                    {
                        task.Description = p.Phase;
                        task.Value = p.PercentComplete;
                    });

                    return await UnifiedAnalyzer.AnalyzeAsync(filePath, progress, cancellationToken);
                });

            // Parse target: FormID or EditorID
            uint? targetFormId = null;
            string? targetEditorId = null;

            if (id.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                targetFormId = Convert.ToUInt32(id, 16);
            }
            else if (uint.TryParse(id, NumberStyles.HexNumber, null, out var parsed))
            {
                targetFormId = parsed;
            }
            else
            {
                targetEditorId = id;
            }

            // Search across all record types
            var records = result.Records;
            var resolver = result.Resolver;
            var found = false;

            found |= TryShowNpc(records, resolver, targetFormId, targetEditorId);
            found |= TryShowRace(records, resolver, targetFormId, targetEditorId);
            found |= TryShowQuest(records, resolver, targetFormId, targetEditorId);
            found |= TryShowFaction(records, resolver, targetFormId, targetEditorId);
            found |= TryShowDialogTopic(records, resolver, targetFormId, targetEditorId);
            found |= TryShowWeapon(records, resolver, targetFormId, targetEditorId);
            found |= TryShowArmor(records, resolver, targetFormId, targetEditorId);
            found |= TryShowScript(records, resolver, targetFormId, targetEditorId);
            found |= TryShowBook(records, resolver, targetFormId, targetEditorId);
            found |= TryShowSound(records, resolver, targetFormId, targetEditorId);
            found |= TryShowExplosion(records, resolver, targetFormId, targetEditorId);
            found |= TryShowMessage(records, resolver, targetFormId, targetEditorId);
            found |= TryShowChallenge(records, resolver, targetFormId, targetEditorId);
            found |= TryShowRecipe(records, resolver, targetFormId, targetEditorId);
            found |= TryShowGeneric(records, resolver, targetFormId, targetEditorId);

            if (!found)
            {
                AnsiConsole.MarkupLine($"[yellow]No record found matching \"{Markup.Escape(id)}\"[/]");

                // Suggest close matches
                var flat = RecordFlattener.Flatten(records);
                var suggestions = flat
                    .Where(r => (r.EditorId?.Contains(id, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                (r.DisplayName?.Contains(id, StringComparison.OrdinalIgnoreCase) ?? false))
                    .Take(5)
                    .ToList();

                if (suggestions.Count > 0)
                {
                    AnsiConsole.MarkupLine("[grey]Did you mean:[/]");
                    foreach (var s in suggestions)
                    {
                        AnsiConsole.MarkupLine(
                            $"  [cyan]0x{s.FormId:X8}[/] {Markup.Escape(s.Type)} {Markup.Escape(s.EditorId ?? "")} {Markup.Escape(s.DisplayName ?? "")}");
                    }
                }

                return 1;
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }

    private static bool Matches<T>(T record, uint? formId, string? editorId,
        Func<T, uint> getFormId, Func<T, string?> getEditorId)
    {
        if (formId.HasValue && getFormId(record) == formId.Value)
        {
            return true;
        }

        if (editorId != null)
        {
            var eid = getEditorId(record);
            return eid != null && eid.Equals(editorId, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool TryShowNpc(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var npc = records.Npcs.FirstOrDefault(r => Matches(r, formId, editorId, n => n.FormId, n => n.EditorId));
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

    private static bool TryShowRace(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var race = records.Races.FirstOrDefault(r => Matches(r, formId, editorId, n => n.FormId, n => n.EditorId));
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

    private static bool TryShowQuest(RecordCollection records, FormIdResolver _,
        uint? formId, string? editorId)
    {
        var quest = records.Quests.FirstOrDefault(r => Matches(r, formId, editorId, q => q.FormId, q => q.EditorId));
        if (quest == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]   0x{quest.FormId:X8}",
            $"[cyan]EditorID:[/] {Markup.Escape(quest.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]     {Markup.Escape(quest.FullName ?? "(none)")}",
            $"[cyan]Priority:[/] {quest.Priority}",
            $"[cyan]Flags:[/]    0x{quest.Flags:X4}"
        };

        if (quest.Objectives is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("[bold]Objectives:[/]");
            foreach (var obj in quest.Objectives.OrderBy(o => o.Index))
            {
                lines.Add($"  [[{obj.Index}]] {Markup.Escape(obj.DisplayText ?? "(no text)")}");
            }
        }

        if (quest.Stages is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("[bold]Stages:[/]");
            foreach (var stage in quest.Stages.OrderBy(s => s.Index))
            {
                lines.Add($"  [[{stage.Index}]] Flags: 0x{stage.Flags:X2}");
            }
        }

        if (quest.Variables is { Count: > 0 })
        {
            lines.Add("");
            lines.Add("[bold]Script Variables:[/]");
            foreach (var variable in quest.Variables)
            {
                lines.Add(
                    $"  {Markup.Escape(variable.Name ?? $"var_{variable.Index}")} ({variable.TypeName}, idx {variable.Index})");
            }
        }

        // Show associated script source/decompiled text inline
        var script = quest.Script is > 0
            ? records.Scripts.FirstOrDefault(s => s.FormId == quest.Script.Value)
            : null;
        if (script != null)
        {
            var scriptText = script.SourceText ?? script.DecompiledText;
            if (!string.IsNullOrEmpty(scriptText))
            {
                var label = script.SourceText != null ? "Source (SCTX)" : "Decompiled";
                lines.Add("");
                lines.Add($"[bold]Script ({Markup.Escape(script.EditorId ?? $"0x{script.FormId:X8}")}) — {label}:[/]");
                if (scriptText.Length > 3000)
                {
                    scriptText = scriptText[..3000] + "\n... (truncated)";
                }

                lines.Add(Markup.Escape(scriptText));
            }
            else
            {
                lines.Add("");
                lines.Add(
                    $"[cyan]Script:[/]  0x{script.FormId:X8} ({Markup.Escape(script.EditorId ?? "")}) — {script.CompiledSize} bytes compiled, no source");
            }
        }
        else if (quest.Script is > 0)
        {
            lines.Add("");
            lines.Add($"[cyan]Script:[/]  0x{quest.Script.Value:X8} (not parsed)");
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]QUST[/] {Markup.Escape(quest.EditorId ?? "")} — {Markup.Escape(quest.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }

    private static bool TryShowFaction(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var faction =
            records.Factions.FirstOrDefault(r => Matches(r, formId, editorId, f => f.FormId, f => f.EditorId));
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

    private static bool TryShowDialogTopic(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var topic = records.DialogTopics.FirstOrDefault(r =>
            Matches(r, formId, editorId, t => t.FormId, t => t.EditorId));
        if (topic == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var questStr = topic.QuestFormId.HasValue
            ? resolver.FormatWithEditorId(topic.QuestFormId.Value)
            : "(none)";
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]     0x{topic.FormId:X8}",
            $"[cyan]EditorID:[/]   {Markup.Escape(topic.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]       {Markup.Escape(topic.FullName ?? "(none)")}",
            $"[cyan]Type:[/]       {topic.TopicTypeName}",
            $"[cyan]Quest:[/]      {questStr}",
            $"[cyan]Responses:[/]  {topic.ResponseCount}",
            $"[cyan]Flags:[/]      0x{topic.Flags:X2}"
        };

        // Find INFO records that reference this topic
        var infos = records.Dialogues
            .Where(d => d.TopicFormId == topic.FormId)
            .Take(20)
            .ToList();

        if (infos.Count > 0)
        {
            lines.Add("");
            lines.Add($"[bold]INFO records ({infos.Count}):[/]");

            foreach (var info in infos)
            {
                var speaker = info.SpeakerFormId.HasValue && info.SpeakerFormId.Value != 0
                    ? resolver.FormatWithEditorId(info.SpeakerFormId.Value)
                    : "(unknown)";
                var firstResponse = info.Responses.FirstOrDefault()?.Text;
                var responseText = !string.IsNullOrEmpty(firstResponse)
                    ? Markup.Escape(firstResponse.Length > 80
                        ? firstResponse[..80] + "..."
                        : firstResponse)
                    : "(no text)";
                lines.Add($"  0x{info.FormId:X8} [[{Markup.Escape(speaker)}]]: {responseText}");
            }
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]DIAL[/] {Markup.Escape(topic.EditorId ?? "")} — {Markup.Escape(topic.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }

    private static bool TryShowWeapon(RecordCollection records, FormIdResolver _,
        uint? formId, string? editorId)
    {
        var weapon = records.Weapons.FirstOrDefault(r => Matches(r, formId, editorId, w => w.FormId, w => w.EditorId));
        if (weapon == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]    0x{weapon.FormId:X8}",
            $"[cyan]EditorID:[/]  {Markup.Escape(weapon.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]      {Markup.Escape(weapon.FullName ?? "(none)")}",
            $"[cyan]Type:[/]      {weapon.WeaponTypeName}",
            $"[cyan]Equip:[/]     {weapon.EquipmentTypeName}",
            $"[cyan]Damage:[/]    {weapon.Damage}",
            $"[cyan]Crit %:[/]    {weapon.CriticalChance:P0}",
            $"[cyan]Crit Dmg:[/]  {weapon.CriticalDamage}",
            $"[cyan]Speed:[/]     {weapon.Speed:F2}",
            $"[cyan]Weight:[/]    {weapon.Weight:F1}",
            $"[cyan]Value:[/]     {weapon.Value}",
            $"[cyan]Health:[/]    {weapon.Health}"
        };

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]WEAP[/] {Markup.Escape(weapon.EditorId ?? "")} — {Markup.Escape(weapon.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }

    private static bool TryShowArmor(RecordCollection records, FormIdResolver _,
        uint? formId, string? editorId)
    {
        var armor = records.Armor.FirstOrDefault(r => Matches(r, formId, editorId, a => a.FormId, a => a.EditorId));
        if (armor == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]    0x{armor.FormId:X8}",
            $"[cyan]EditorID:[/]  {Markup.Escape(armor.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]      {Markup.Escape(armor.FullName ?? "(none)")}",
            $"[cyan]DT:[/]        {armor.DamageThreshold:F1}",
            $"[cyan]Weight:[/]    {armor.Weight:F1}",
            $"[cyan]Value:[/]     {armor.Value}",
            $"[cyan]Health:[/]    {armor.Health}"
        };

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]ARMO[/] {Markup.Escape(armor.EditorId ?? "")} — {Markup.Escape(armor.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }

    private static bool TryShowScript(RecordCollection records, FormIdResolver _,
        uint? formId, string? editorId)
    {
        var script = records.Scripts.FirstOrDefault(r => Matches(r, formId, editorId, s => s.FormId, s => s.EditorId));
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

    private static bool TryShowBook(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var book = records.Books.FirstOrDefault(r => Matches(r, formId, editorId, b => b.FormId, b => b.EditorId));
        if (book == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]    0x{book.FormId:X8}",
            $"[cyan]EditorID:[/]  {Markup.Escape(book.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]      {Markup.Escape(book.FullName ?? "(none)")}",
            $"[cyan]Value:[/]     {book.Value} caps",
            $"[cyan]Weight:[/]    {book.Weight:F1}"
        };

        if (book.Flags != 0)
        {
            lines.Add(
                $"[cyan]Flags:[/]     {FlagRegistry.DecodeFlagNamesWithHex(book.Flags, FlagRegistry.BookFlags)}");
        }

        if (book.TeachesSkill)
        {
            lines.Add(
                $"[cyan]Teaches:[/]   {resolver.GetSkillName(book.SkillTaught) ?? $"Skill#{book.SkillTaught}"}");
        }

        if (book.EnchantmentFormId is > 0)
        {
            lines.Add($"[cyan]Enchantment:[/] {resolver.FormatWithEditorId(book.EnchantmentFormId.Value)}");
            if (book.EnchantmentAmount != 0)
            {
                lines.Add($"[cyan]Enchant Amt:[/] {book.EnchantmentAmount}");
            }
        }

        if (!string.IsNullOrEmpty(book.ModelPath))
        {
            lines.Add($"[cyan]Model:[/]     {Markup.Escape(book.ModelPath)}");
        }

        if (!string.IsNullOrEmpty(book.Text))
        {
            var text = book.Text.Length > 2000 ? book.Text[..2000] + "\n... (truncated)" : book.Text;
            lines.Add("");
            lines.Add("[bold]Text:[/]");
            lines.Add(Markup.Escape(text));
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]BOOK[/] {Markup.Escape(book.EditorId ?? "")} — {Markup.Escape(book.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }

    private static bool TryShowSound(RecordCollection records, FormIdResolver _,
        uint? formId, string? editorId)
    {
        var snd = records.Sounds.FirstOrDefault(r => Matches(r, formId, editorId, s => s.FormId, s => s.EditorId));
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

    private static bool TryShowExplosion(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var expl = records.Explosions.FirstOrDefault(r =>
            Matches(r, formId, editorId, e => e.FormId, e => e.EditorId));
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

    private static bool TryShowMessage(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var msg = records.Messages.FirstOrDefault(r => Matches(r, formId, editorId, m => m.FormId, m => m.EditorId));
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

    private static bool TryShowChallenge(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var chal = records.Challenges.FirstOrDefault(r =>
            Matches(r, formId, editorId, c => c.FormId, c => c.EditorId));
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

    private static bool TryShowRecipe(RecordCollection records, FormIdResolver resolver,
        uint? formId, string? editorId)
    {
        var recipe = records.Recipes.FirstOrDefault(r =>
            Matches(r, formId, editorId, rc => rc.FormId, rc => rc.EditorId));
        if (recipe == null)
        {
            return false;
        }

        AnsiConsole.WriteLine();
        var lines = new List<string>
        {
            $"[cyan]FormID:[/]    0x{recipe.FormId:X8}",
            $"[cyan]EditorID:[/]  {Markup.Escape(recipe.EditorId ?? "(none)")}",
            $"[cyan]Name:[/]      {Markup.Escape(recipe.FullName ?? "(none)")}"
        };

        if (recipe.RequiredSkill != 0 || recipe.RequiredSkillLevel != 0)
        {
            var skillName = resolver.GetSkillName(recipe.RequiredSkill) ?? $"Skill#{recipe.RequiredSkill}";
            lines.Add($"[cyan]Requires:[/]  {skillName} {recipe.RequiredSkillLevel}");
        }

        if (recipe.CategoryFormId != 0)
        {
            lines.Add($"[cyan]Category:[/]  {resolver.FormatWithEditorId(recipe.CategoryFormId)}");
        }

        if (recipe.SubcategoryFormId != 0)
        {
            lines.Add($"[cyan]Subcategory:[/] {resolver.FormatWithEditorId(recipe.SubcategoryFormId)}");
        }

        if (recipe.Ingredients.Count > 0)
        {
            lines.Add("");
            lines.Add($"[bold]Ingredients ({recipe.Ingredients.Count}):[/]");
            foreach (var ing in recipe.Ingredients)
            {
                lines.Add($"  {resolver.FormatWithEditorId(ing.ItemFormId)} x{ing.Count}");
            }
        }

        if (recipe.Outputs.Count > 0)
        {
            lines.Add("");
            lines.Add($"[bold]Outputs ({recipe.Outputs.Count}):[/]");
            foreach (var output in recipe.Outputs)
            {
                lines.Add($"  {resolver.FormatWithEditorId(output.ItemFormId)} x{output.Count}");
            }
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]RCPE[/] {Markup.Escape(recipe.EditorId ?? "")} — {Markup.Escape(recipe.FullName ?? "")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }

    private static bool TryShowGeneric(RecordCollection records, FormIdResolver resolver,
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
            AppendPdbFields(lines, genericRecord.Fields, resolver);
        }

        var panel = new Panel(string.Join("\n", lines))
        {
            Header = new PanelHeader(
                $"[bold]{Markup.Escape(match.Type)}[/] {Markup.Escape(match.EditorId ?? $"0x{match.FormId:X8}")}")
        };
        AnsiConsole.Write(panel);
        return true;
    }

    /// <summary>
    ///     Append PDB-derived struct fields to the display lines, grouped by owner class.
    /// </summary>
    private static void AppendPdbFields(List<string> lines, Dictionary<string, object?> fields,
        FormIdResolver resolver)
    {
        // Group fields by owner class (key format is "OwnerClass.FieldName")
        var grouped = new Dictionary<string, List<(string FieldName, object? Value)>>();
        foreach (var (key, value) in fields)
        {
            var dotIndex = key.IndexOf('.');
            string owner;
            string fieldName;
            if (dotIndex >= 0)
            {
                owner = key[..dotIndex];
                fieldName = key[(dotIndex + 1)..];
            }
            else
            {
                owner = "(unknown)";
                fieldName = key;
            }

            if (!grouped.TryGetValue(owner, out var list))
            {
                list = [];
                grouped[owner] = list;
            }

            list.Add((fieldName, value));
        }

        foreach (var (owner, fieldList) in grouped)
        {
            lines.Add($"[bold]{Markup.Escape(owner)}:[/]");
            foreach (var (fieldName, value) in fieldList)
            {
                var formatted = FormatPdbFieldValue(value, resolver);
                lines.Add($"  [grey]{Markup.Escape(fieldName)}:[/] {formatted}");
            }
        }
    }

    /// <summary>
    ///     Format a PDB field value for display, resolving FormIDs where possible.
    /// </summary>
    private static string FormatPdbFieldValue(object? value, FormIdResolver resolver)
    {
        return value switch
        {
            null => "[grey](null)[/]",
            uint u when u > 0x00010000 && u < 0x10000000 =>
                // Likely a FormID — try to resolve with EditorID
                resolver.FormatWithEditorId(u),
            uint u => $"0x{u:X8}  ({u})",
            int i => i.ToString(),
            float f => f.ToString("F4"),
            ushort us => $"{us}  (0x{us:X4})",
            short s => s.ToString(),
            byte b => $"{b}  (0x{b:X2})",
            sbyte sb => sb.ToString(),
            bool b => b.ToString(),
            string s => Markup.Escape(s),
            _ => Markup.Escape(value.ToString() ?? "")
        };
    }
}
