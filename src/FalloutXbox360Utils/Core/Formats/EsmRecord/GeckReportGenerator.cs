using System.Text;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord;

/// <summary>
///     Generates human-readable GECK-style reports from reconstructed ESM data.
///     Output format is designed to be similar to how data appears in the GECK editor.
/// </summary>
public static class GeckReportGenerator
{
    private const int SeparatorWidth = 80;
    private const char SeparatorChar = '=';

    /// <summary>
    ///     Generate a complete report from semantic reconstruction results.
    /// </summary>
    public static string Generate(SemanticReconstructionResult result, Dictionary<uint, string>? formIdToEditorId = null)
    {
        var sb = new StringBuilder();
        var lookup = formIdToEditorId ?? result.FormIdToEditorId;

        // Header
        AppendHeader(sb, "ESM Memory Dump Semantic Reconstruction Report");
        sb.AppendLine();
        AppendSummary(sb, result);
        sb.AppendLine();

        // Characters
        if (result.Npcs.Count > 0)
        {
            AppendNpcsSection(sb, result.Npcs, lookup);
        }

        if (result.Races.Count > 0)
        {
            AppendRacesSection(sb, result.Races, lookup);
        }

        // Quests and Dialogue
        if (result.Quests.Count > 0)
        {
            AppendQuestsSection(sb, result.Quests, lookup);
        }

        if (result.Notes.Count > 0)
        {
            AppendNotesSection(sb, result.Notes, lookup);
        }

        if (result.Dialogues.Count > 0)
        {
            AppendDialogueSection(sb, result.Dialogues, lookup);
        }

        // Items
        if (result.Weapons.Count > 0)
        {
            AppendWeaponsSection(sb, result.Weapons, lookup);
        }

        if (result.Armor.Count > 0)
        {
            AppendArmorSection(sb, result.Armor, lookup);
        }

        if (result.Ammo.Count > 0)
        {
            AppendAmmoSection(sb, result.Ammo, lookup);
        }

        if (result.Consumables.Count > 0)
        {
            AppendConsumablesSection(sb, result.Consumables, lookup);
        }

        if (result.MiscItems.Count > 0)
        {
            AppendMiscItemsSection(sb, result.MiscItems, lookup);
        }

        // Abilities
        if (result.Perks.Count > 0)
        {
            AppendPerksSection(sb, result.Perks, lookup);
        }

        if (result.Spells.Count > 0)
        {
            AppendSpellsSection(sb, result.Spells, lookup);
        }

        // World
        if (result.Cells.Count > 0)
        {
            AppendCellsSection(sb, result.Cells, lookup);
        }

        if (result.Worldspaces.Count > 0)
        {
            AppendWorldspacesSection(sb, result.Worldspaces, lookup);
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for NPCs only.
    /// </summary>
    public static string GenerateNpcsReport(List<ReconstructedNpc> npcs, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendNpcsSection(sb, npcs, lookup ?? []);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Quests only.
    /// </summary>
    public static string GenerateQuestsReport(List<ReconstructedQuest> quests, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendQuestsSection(sb, quests, lookup ?? []);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Notes only.
    /// </summary>
    public static string GenerateNotesReport(List<ReconstructedNote> notes, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendNotesSection(sb, notes, lookup ?? []);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Dialogue only.
    /// </summary>
    public static string GenerateDialogueReport(List<ReconstructedDialogue> dialogues, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendDialogueSection(sb, dialogues, lookup ?? []);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a report for Cells only.
    /// </summary>
    public static string GenerateCellsReport(List<ReconstructedCell> cells, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendCellsSection(sb, cells, lookup ?? []);
        return sb.ToString();
    }

    #region Section Generators

    private static void AppendHeader(StringBuilder sb, string title)
    {
        sb.AppendLine(new string(SeparatorChar, SeparatorWidth));
        var padding = (SeparatorWidth - title.Length) / 2;
        sb.AppendLine(new string(' ', Math.Max(0, padding)) + title);
        sb.AppendLine(new string(SeparatorChar, SeparatorWidth));
    }

    private static void AppendSectionHeader(StringBuilder sb, string title)
    {
        sb.AppendLine();
        sb.AppendLine(new string('-', SeparatorWidth));
        sb.AppendLine($"  {title}");
        sb.AppendLine(new string('-', SeparatorWidth));
    }

    private static void AppendRecordHeader(StringBuilder sb, string recordType, string? editorId)
    {
        sb.AppendLine();
        sb.AppendLine(new string(SeparatorChar, SeparatorWidth));
        var title = string.IsNullOrEmpty(editorId)
            ? $"{recordType}"
            : $"{recordType}: {editorId}";
        var padding = (SeparatorWidth - title.Length) / 2;
        sb.AppendLine(new string(' ', Math.Max(0, padding)) + title);
        sb.AppendLine(new string(SeparatorChar, SeparatorWidth));
    }

    private static void AppendSummary(StringBuilder sb, SemanticReconstructionResult result)
    {
        sb.AppendLine("Summary:");
        sb.AppendLine($"  Total Records Processed:     {result.TotalRecordsProcessed:N0}");
        sb.AppendLine($"  Total Records Reconstructed: {result.TotalRecordsReconstructed:N0}");
        sb.AppendLine();
        sb.AppendLine("  Characters:");
        sb.AppendLine($"    NPCs:         {result.Npcs.Count,6:N0}");
        sb.AppendLine($"    Races:        {result.Races.Count,6:N0}");
        sb.AppendLine();
        sb.AppendLine("  Quests & Dialogue:");
        sb.AppendLine($"    Quests:       {result.Quests.Count,6:N0}");
        sb.AppendLine($"    Notes:        {result.Notes.Count,6:N0}");
        sb.AppendLine($"    Dialogue:     {result.Dialogues.Count,6:N0}");
        sb.AppendLine();
        sb.AppendLine("  Items:");
        sb.AppendLine($"    Weapons:      {result.Weapons.Count,6:N0}");
        sb.AppendLine($"    Armor:        {result.Armor.Count,6:N0}");
        sb.AppendLine($"    Ammo:         {result.Ammo.Count,6:N0}");
        sb.AppendLine($"    Consumables:  {result.Consumables.Count,6:N0}");
        sb.AppendLine($"    Misc Items:   {result.MiscItems.Count,6:N0}");
        sb.AppendLine();
        sb.AppendLine("  Abilities:");
        sb.AppendLine($"    Perks:        {result.Perks.Count,6:N0}");
        sb.AppendLine($"    Spells:       {result.Spells.Count,6:N0}");
        sb.AppendLine();
        sb.AppendLine("  World:");
        sb.AppendLine($"    Cells:        {result.Cells.Count,6:N0}");
        sb.AppendLine($"    Worldspaces:  {result.Worldspaces.Count,6:N0}");
    }

    private static void AppendNpcsSection(StringBuilder sb, List<ReconstructedNpc> npcs, Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"NPCs ({npcs.Count})");

        foreach (var npc in npcs.OrderBy(n => n.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "NPC", npc.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(npc.FormId)}");
            sb.AppendLine($"Editor ID:      {npc.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {npc.FullName ?? "(none)"}");
            sb.AppendLine($"Endianness:     {(npc.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{npc.Offset:X8}");

            if (npc.Stats != null)
            {
                sb.AppendLine();
                sb.AppendLine("Stats (ACBS):");
                sb.AppendLine($"  Level:          {npc.Stats.Level}");
                sb.AppendLine($"  Fatigue Base:   {npc.Stats.FatigueBase}");
                sb.AppendLine($"  Barter Gold:    {npc.Stats.BarterGold}");
                sb.AppendLine($"  Speed Mult:     {npc.Stats.SpeedMultiplier}");
                sb.AppendLine($"  Karma:          {npc.Stats.KarmaAlignment:F2}");
                sb.AppendLine($"  Disposition:    {npc.Stats.DispositionBase}");
                sb.AppendLine($"  Calc Range:     {npc.Stats.CalcMin} - {npc.Stats.CalcMax}");
                sb.AppendLine($"  Flags:          0x{npc.Stats.Flags:X8}");
            }

            if (npc.Race.HasValue)
            {
                sb.AppendLine();
                sb.AppendLine($"Race:           {FormatFormIdWithName(npc.Race.Value, lookup)}");
            }

            if (npc.Class.HasValue)
            {
                sb.AppendLine($"Class:          {FormatFormIdWithName(npc.Class.Value, lookup)}");
            }

            if (npc.Script.HasValue)
            {
                sb.AppendLine($"Script:         {FormatFormIdWithName(npc.Script.Value, lookup)}");
            }

            if (npc.VoiceType.HasValue)
            {
                sb.AppendLine($"Voice Type:     {FormatFormIdWithName(npc.VoiceType.Value, lookup)}");
            }

            if (npc.Template.HasValue)
            {
                sb.AppendLine($"Template:       {FormatFormIdWithName(npc.Template.Value, lookup)}");
            }

            if (npc.Factions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Factions:");
                foreach (var faction in npc.Factions)
                {
                    sb.AppendLine($"  - {FormatFormIdWithName(faction.FactionFormId, lookup)} (Rank: {faction.Rank})");
                }
            }

            if (npc.Spells.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Spells/Abilities:");
                foreach (var spell in npc.Spells)
                {
                    sb.AppendLine($"  - {FormatFormIdWithName(spell, lookup)}");
                }
            }

            if (npc.Inventory.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Inventory:");
                foreach (var item in npc.Inventory)
                {
                    sb.AppendLine($"  - {FormatFormIdWithName(item.ItemFormId, lookup)} x{item.Count}");
                }
            }

            if (npc.Packages.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("AI Packages:");
                foreach (var package in npc.Packages)
                {
                    sb.AppendLine($"  - {FormatFormIdWithName(package, lookup)}");
                }
            }
        }
    }

    private static void AppendQuestsSection(StringBuilder sb, List<ReconstructedQuest> quests, Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Quests ({quests.Count})");

        foreach (var quest in quests.OrderBy(q => q.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "QUEST", quest.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(quest.FormId)}");
            sb.AppendLine($"Editor ID:      {quest.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {quest.FullName ?? "(none)"}");
            sb.AppendLine($"Flags:          0x{quest.Flags:X2}");
            sb.AppendLine($"Priority:       {quest.Priority}");
            sb.AppendLine($"Endianness:     {(quest.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{quest.Offset:X8}");

            if (quest.Script.HasValue)
            {
                sb.AppendLine($"Script:         {FormatFormIdWithName(quest.Script.Value, lookup)}");
            }

            if (quest.Stages.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Stages:");
                foreach (var stage in quest.Stages)
                {
                    var flagsStr = stage.Flags != 0 ? $" [Flags: 0x{stage.Flags:X2}]" : "";
                    var logStr = !string.IsNullOrEmpty(stage.LogEntry)
                        ? $" {stage.LogEntry}"
                        : "";
                    sb.AppendLine($"  [{stage.Index,3}]{flagsStr}{logStr}");
                }
            }

            if (quest.Objectives.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Objectives:");
                foreach (var obj in quest.Objectives)
                {
                    var text = !string.IsNullOrEmpty(obj.DisplayText)
                        ? obj.DisplayText
                        : "(no text)";
                    sb.AppendLine($"  [{obj.Index,3}] {text}");
                }
            }
        }
    }

    private static void AppendNotesSection(StringBuilder sb, List<ReconstructedNote> notes, Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Notes ({notes.Count})");

        foreach (var note in notes.OrderBy(n => n.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "NOTE", note.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(note.FormId)}");
            sb.AppendLine($"Editor ID:      {note.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {note.FullName ?? "(none)"}");
            sb.AppendLine($"Type:           {note.NoteTypeName}");
            sb.AppendLine($"Endianness:     {(note.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{note.Offset:X8}");

            if (!string.IsNullOrEmpty(note.Text))
            {
                sb.AppendLine();
                sb.AppendLine("Text:");
                // Indent each line of the note text
                foreach (var line in note.Text.Split('\n'))
                {
                    sb.AppendLine($"  {line.TrimEnd('\r')}");
                }
            }
        }
    }

    private static void AppendDialogueSection(StringBuilder sb, List<ReconstructedDialogue> dialogues, Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Dialogue Responses ({dialogues.Count})");

        // Group by quest if possible
        var grouped = dialogues
            .GroupBy(d => d.QuestFormId ?? 0)
            .OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            if (group.Key != 0)
            {
                sb.AppendLine();
                sb.AppendLine($"--- Quest: {FormatFormIdWithName(group.Key, lookup)} ---");
            }

            foreach (var dialogue in group.OrderBy(d => d.EditorId ?? ""))
            {
                AppendRecordHeader(sb, "INFO", dialogue.EditorId);

                sb.AppendLine($"FormID:         {FormatFormId(dialogue.FormId)}");
                sb.AppendLine($"Editor ID:      {dialogue.EditorId ?? "(none)"}");

                if (dialogue.TopicFormId.HasValue)
                {
                    sb.AppendLine($"Topic:          {FormatFormIdWithName(dialogue.TopicFormId.Value, lookup)}");
                }

                if (dialogue.QuestFormId.HasValue)
                {
                    sb.AppendLine($"Quest:          {FormatFormIdWithName(dialogue.QuestFormId.Value, lookup)}");
                }

                if (dialogue.SpeakerFormId.HasValue)
                {
                    sb.AppendLine($"Speaker:        {FormatFormIdWithName(dialogue.SpeakerFormId.Value, lookup)}");
                }

                if (dialogue.PreviousInfo.HasValue)
                {
                    sb.AppendLine($"Previous INFO:  {FormatFormIdWithName(dialogue.PreviousInfo.Value, lookup)}");
                }

                sb.AppendLine($"Endianness:     {(dialogue.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
                sb.AppendLine($"Offset:         0x{dialogue.Offset:X8}");

                if (dialogue.Responses.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Responses:");
                    foreach (var response in dialogue.Responses.OrderBy(r => r.ResponseNumber))
                    {
                        var emotionStr = response.EmotionType != 0 || response.EmotionValue != 0
                            ? $" [{response.EmotionName}: {response.EmotionValue}]"
                            : "";
                        sb.AppendLine($"  [{response.ResponseNumber}]{emotionStr}");
                        if (!string.IsNullOrEmpty(response.Text))
                        {
                            sb.AppendLine($"    \"{response.Text}\"");
                        }
                    }
                }
            }
        }
    }

    private static void AppendCellsSection(StringBuilder sb, List<ReconstructedCell> cells, Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Cells ({cells.Count})");

        // Separate interior and exterior cells
        var exteriorCells = cells.Where(c => !c.IsInterior && c.GridX.HasValue).ToList();
        var interiorCells = cells.Where(c => c.IsInterior || !c.GridX.HasValue).ToList();

        if (exteriorCells.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  Exterior Cells ({exteriorCells.Count}):");

            foreach (var cell in exteriorCells.OrderBy(c => c.GridX).ThenBy(c => c.GridY))
            {
                var gridStr = $"({cell.GridX}, {cell.GridY})";
                AppendRecordHeader(sb, $"CELL {gridStr}", cell.EditorId);

                sb.AppendLine($"FormID:         {FormatFormId(cell.FormId)}");
                sb.AppendLine($"Editor ID:      {cell.EditorId ?? "(none)"}");
                sb.AppendLine($"Display Name:   {cell.FullName ?? "(none)"}");
                sb.AppendLine($"Grid:           {cell.GridX}, {cell.GridY}");
                sb.AppendLine($"Flags:          0x{cell.Flags:X2}");
                sb.AppendLine($"Has Water:      {cell.HasWater}");
                sb.AppendLine($"Endianness:     {(cell.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
                sb.AppendLine($"Offset:         0x{cell.Offset:X8}");

                if (cell.Heightmap != null)
                {
                    sb.AppendLine();
                    sb.AppendLine($"Heightmap:      Found (offset: {cell.Heightmap.HeightOffset:F1})");
                }

                AppendPlacedObjects(sb, cell.PlacedObjects, lookup);
            }
        }

        if (interiorCells.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  Interior Cells ({interiorCells.Count}):");

            foreach (var cell in interiorCells.OrderBy(c => c.EditorId ?? ""))
            {
                AppendRecordHeader(sb, "CELL (Interior)", cell.EditorId);

                sb.AppendLine($"FormID:         {FormatFormId(cell.FormId)}");
                sb.AppendLine($"Editor ID:      {cell.EditorId ?? "(none)"}");
                sb.AppendLine($"Display Name:   {cell.FullName ?? "(none)"}");
                sb.AppendLine($"Flags:          0x{cell.Flags:X2}");
                sb.AppendLine($"Endianness:     {(cell.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
                sb.AppendLine($"Offset:         0x{cell.Offset:X8}");

                AppendPlacedObjects(sb, cell.PlacedObjects, lookup);
            }
        }
    }

    private static void AppendPlacedObjects(StringBuilder sb, List<PlacedReference> placedObjects, Dictionary<uint, string> lookup)
    {
        if (placedObjects.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine($"Placed Objects ({placedObjects.Count}):");

        // Limit output for large cells
        var toShow = placedObjects.Take(50).ToList();
        foreach (var obj in toShow)
        {
            var baseStr = !string.IsNullOrEmpty(obj.BaseEditorId)
                ? obj.BaseEditorId
                : FormatFormId(obj.BaseFormId);
            var scaleStr = Math.Abs(obj.Scale - 1.0f) > 0.01f ? $" scale={obj.Scale:F2}" : "";
            sb.AppendLine($"  - {baseStr} ({obj.RecordType})");
            sb.AppendLine($"      at ({obj.X:F1}, {obj.Y:F1}, {obj.Z:F1}){scaleStr}");
        }

        if (placedObjects.Count > 50)
        {
            sb.AppendLine($"  ... and {placedObjects.Count - 50} more");
        }
    }

    private static void AppendWorldspacesSection(StringBuilder sb, List<ReconstructedWorldspace> worldspaces, Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Worldspaces ({worldspaces.Count})");

        foreach (var wrld in worldspaces.OrderBy(w => w.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "WRLD", wrld.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(wrld.FormId)}");
            sb.AppendLine($"Editor ID:      {wrld.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {wrld.FullName ?? "(none)"}");
            sb.AppendLine($"Endianness:     {(wrld.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{wrld.Offset:X8}");

            if (wrld.ParentWorldspaceFormId.HasValue)
            {
                sb.AppendLine($"Parent:         {FormatFormIdWithName(wrld.ParentWorldspaceFormId.Value, lookup)}");
            }

            if (wrld.ClimateFormId.HasValue)
            {
                sb.AppendLine($"Climate:        {FormatFormIdWithName(wrld.ClimateFormId.Value, lookup)}");
            }

            if (wrld.WaterFormId.HasValue)
            {
                sb.AppendLine($"Water:          {FormatFormIdWithName(wrld.WaterFormId.Value, lookup)}");
            }

            if (wrld.Cells.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Cells:          {wrld.Cells.Count}");
            }
        }
    }

    private static void AppendWeaponsSection(StringBuilder sb, List<ReconstructedWeapon> weapons, Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Weapons ({weapons.Count})");

        foreach (var weapon in weapons.OrderBy(w => w.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "WEAP", weapon.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(weapon.FormId)}");
            sb.AppendLine($"Editor ID:      {weapon.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {weapon.FullName ?? "(none)"}");
            sb.AppendLine($"Type:           {weapon.WeaponTypeName}");
            sb.AppendLine($"Endianness:     {(weapon.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{weapon.Offset:X8}");

            sb.AppendLine();
            sb.AppendLine("Combat Stats:");
            sb.AppendLine($"  Damage:         {weapon.Damage}");
            sb.AppendLine($"  DPS:            {weapon.DamagePerSecond:F1}");
            sb.AppendLine($"  Fire Rate:      {weapon.ShotsPerSec:F1}/sec");
            sb.AppendLine($"  Clip Size:      {weapon.ClipSize}");
            sb.AppendLine($"  Range:          {weapon.MinRange:F0} - {weapon.MaxRange:F0}");

            sb.AppendLine();
            sb.AppendLine("Accuracy:");
            sb.AppendLine($"  Spread:         {weapon.Spread:F2}");
            sb.AppendLine($"  Min Spread:     {weapon.MinSpread:F2}");
            sb.AppendLine($"  Drift:          {weapon.Drift:F2}");

            if (weapon.StrengthRequirement > 0 || weapon.SkillRequirement > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Requirements:");
                if (weapon.StrengthRequirement > 0)
                {
                    sb.AppendLine($"  Strength:       {weapon.StrengthRequirement}");
                }
                if (weapon.SkillRequirement > 0)
                {
                    sb.AppendLine($"  Skill:          {weapon.SkillRequirement}");
                }
            }

            if (weapon.CriticalDamage != 0 || Math.Abs(weapon.CriticalChance - 1.0f) > 0.01f)
            {
                sb.AppendLine();
                sb.AppendLine("Critical:");
                sb.AppendLine($"  Damage:         {weapon.CriticalDamage}");
                sb.AppendLine($"  Chance:         x{weapon.CriticalChance:F1}");
                if (weapon.CriticalEffectFormId.HasValue)
                {
                    sb.AppendLine($"  Effect:         {FormatFormIdWithName(weapon.CriticalEffectFormId.Value, lookup)}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("Value/Weight:");
            sb.AppendLine($"  Value:          {weapon.Value} caps");
            sb.AppendLine($"  Weight:         {weapon.Weight:F1}");
            sb.AppendLine($"  Health:         {weapon.Health}");

            if (weapon.AmmoFormId.HasValue)
            {
                sb.AppendLine($"  Ammo:           {FormatFormIdWithName(weapon.AmmoFormId.Value, lookup)}");
            }

            sb.AppendLine($"  AP Cost:        {weapon.ActionPoints:F0}");

            if (!string.IsNullOrEmpty(weapon.ModelPath))
            {
                sb.AppendLine($"  Model:          {weapon.ModelPath}");
            }
        }
    }

    private static void AppendArmorSection(StringBuilder sb, List<ReconstructedArmor> armor, Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Armor ({armor.Count})");

        foreach (var item in armor.OrderBy(a => a.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "ARMO", item.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(item.FormId)}");
            sb.AppendLine($"Editor ID:      {item.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {item.FullName ?? "(none)"}");
            sb.AppendLine($"Endianness:     {(item.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{item.Offset:X8}");

            sb.AppendLine();
            sb.AppendLine("Stats:");
            sb.AppendLine($"  Armor Rating:   {item.ArmorRating}");
            sb.AppendLine($"  Value:          {item.Value} caps");
            sb.AppendLine($"  Weight:         {item.Weight:F1}");
            sb.AppendLine($"  Health:         {item.Health}");

            if (!string.IsNullOrEmpty(item.ModelPath))
            {
                sb.AppendLine($"  Model:          {item.ModelPath}");
            }
        }
    }

    private static void AppendAmmoSection(StringBuilder sb, List<ReconstructedAmmo> ammo, Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Ammunition ({ammo.Count})");

        foreach (var item in ammo.OrderBy(a => a.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "AMMO", item.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(item.FormId)}");
            sb.AppendLine($"Editor ID:      {item.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {item.FullName ?? "(none)"}");
            sb.AppendLine($"Endianness:     {(item.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{item.Offset:X8}");

            sb.AppendLine();
            sb.AppendLine("Stats:");
            sb.AppendLine($"  Speed:          {item.Speed:F1}");
            sb.AppendLine($"  Value:          {item.Value} caps");
            sb.AppendLine($"  Clip Rounds:    {item.ClipRounds}");
            sb.AppendLine($"  Flags:          0x{item.Flags:X2}");

            if (item.ProjectileFormId.HasValue)
            {
                sb.AppendLine($"  Projectile:     {FormatFormIdWithName(item.ProjectileFormId.Value, lookup)}");
            }

            if (!string.IsNullOrEmpty(item.ModelPath))
            {
                sb.AppendLine($"  Model:          {item.ModelPath}");
            }
        }
    }

    private static void AppendConsumablesSection(StringBuilder sb, List<ReconstructedConsumable> consumables, Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Consumables ({consumables.Count})");

        foreach (var item in consumables.OrderBy(c => c.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "ALCH", item.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(item.FormId)}");
            sb.AppendLine($"Editor ID:      {item.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {item.FullName ?? "(none)"}");
            sb.AppendLine($"Endianness:     {(item.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{item.Offset:X8}");

            sb.AppendLine();
            sb.AppendLine("Stats:");
            sb.AppendLine($"  Value:          {item.Value} caps");
            sb.AppendLine($"  Weight:         {item.Weight:F1}");

            if (item.AddictionFormId.HasValue)
            {
                sb.AppendLine($"  Addiction:      {FormatFormIdWithName(item.AddictionFormId.Value, lookup)}");
                sb.AppendLine($"  Addict. Chance: {item.AddictionChance * 100:F0}%");
            }

            if (item.EffectFormIds.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Effects:");
                foreach (var effect in item.EffectFormIds)
                {
                    sb.AppendLine($"  - {FormatFormIdWithName(effect, lookup)}");
                }
            }

            if (!string.IsNullOrEmpty(item.ModelPath))
            {
                sb.AppendLine($"  Model:          {item.ModelPath}");
            }
        }
    }

    private static void AppendMiscItemsSection(StringBuilder sb, List<ReconstructedMiscItem> miscItems, Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Miscellaneous Items ({miscItems.Count})");

        foreach (var item in miscItems.OrderBy(m => m.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "MISC", item.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(item.FormId)}");
            sb.AppendLine($"Editor ID:      {item.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {item.FullName ?? "(none)"}");
            sb.AppendLine($"Endianness:     {(item.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{item.Offset:X8}");

            sb.AppendLine();
            sb.AppendLine("Stats:");
            sb.AppendLine($"  Value:          {item.Value} caps");
            sb.AppendLine($"  Weight:         {item.Weight:F1}");

            if (!string.IsNullOrEmpty(item.ModelPath))
            {
                sb.AppendLine($"  Model:          {item.ModelPath}");
            }
        }
    }

    private static void AppendPerksSection(StringBuilder sb, List<ReconstructedPerk> perks, Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Perks ({perks.Count})");

        foreach (var perk in perks.OrderBy(p => p.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "PERK", perk.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(perk.FormId)}");
            sb.AppendLine($"Editor ID:      {perk.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {perk.FullName ?? "(none)"}");
            sb.AppendLine($"Endianness:     {(perk.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{perk.Offset:X8}");

            if (!string.IsNullOrEmpty(perk.Description))
            {
                sb.AppendLine();
                sb.AppendLine("Description:");
                foreach (var line in perk.Description.Split('\n'))
                {
                    sb.AppendLine($"  {line.TrimEnd('\r')}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("Requirements:");
            sb.AppendLine($"  Ranks:          {perk.Ranks}");
            sb.AppendLine($"  Min Level:      {perk.MinLevel}");
            sb.AppendLine($"  Playable:       {(perk.IsPlayable ? "Yes" : "No")}");
            sb.AppendLine($"  Is Trait:       {(perk.IsTrait ? "Yes" : "No")}");

            if (!string.IsNullOrEmpty(perk.IconPath))
            {
                sb.AppendLine($"  Icon:           {perk.IconPath}");
            }

            if (perk.Entries.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Entries:");
                foreach (var entry in perk.Entries)
                {
                    var abilityStr = entry.AbilityFormId.HasValue
                        ? $" Ability: {FormatFormIdWithName(entry.AbilityFormId.Value, lookup)}"
                        : "";
                    sb.AppendLine($"  [Rank {entry.Rank}] {entry.TypeName}{abilityStr}");
                }
            }
        }
    }

    private static void AppendSpellsSection(StringBuilder sb, List<ReconstructedSpell> spells, Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Spells/Abilities ({spells.Count})");

        foreach (var spell in spells.OrderBy(s => s.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "SPEL", spell.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(spell.FormId)}");
            sb.AppendLine($"Editor ID:      {spell.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {spell.FullName ?? "(none)"}");
            sb.AppendLine($"Type:           {spell.TypeName}");
            sb.AppendLine($"Endianness:     {(spell.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{spell.Offset:X8}");

            sb.AppendLine();
            sb.AppendLine("Stats:");
            sb.AppendLine($"  Cost:           {spell.Cost}");
            sb.AppendLine($"  Level:          {spell.Level}");
            sb.AppendLine($"  Flags:          0x{spell.Flags:X2}");

            if (spell.EffectFormIds.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Effects:");
                foreach (var effect in spell.EffectFormIds)
                {
                    sb.AppendLine($"  - {FormatFormIdWithName(effect, lookup)}");
                }
            }
        }
    }

    private static void AppendRacesSection(StringBuilder sb, List<ReconstructedRace> races, Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Races ({races.Count})");

        foreach (var race in races.OrderBy(r => r.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "RACE", race.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(race.FormId)}");
            sb.AppendLine($"Editor ID:      {race.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {race.FullName ?? "(none)"}");
            sb.AppendLine($"Endianness:     {(race.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{race.Offset:X8}");

            if (!string.IsNullOrEmpty(race.Description))
            {
                sb.AppendLine();
                sb.AppendLine("Description:");
                foreach (var line in race.Description.Split('\n'))
                {
                    sb.AppendLine($"  {line.TrimEnd('\r')}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("S.P.E.C.I.A.L. Modifiers:");
            sb.AppendLine($"  Strength:     {FormatModifier(race.Strength)}");
            sb.AppendLine($"  Perception:   {FormatModifier(race.Perception)}");
            sb.AppendLine($"  Endurance:    {FormatModifier(race.Endurance)}");
            sb.AppendLine($"  Charisma:     {FormatModifier(race.Charisma)}");
            sb.AppendLine($"  Intelligence: {FormatModifier(race.Intelligence)}");
            sb.AppendLine($"  Agility:      {FormatModifier(race.Agility)}");
            sb.AppendLine($"  Luck:         {FormatModifier(race.Luck)}");

            sb.AppendLine();
            sb.AppendLine("Height:");
            sb.AppendLine($"  Male:         {race.MaleHeight:F2}");
            sb.AppendLine($"  Female:       {race.FemaleHeight:F2}");

            if (race.MaleVoiceFormId.HasValue || race.FemaleVoiceFormId.HasValue)
            {
                sb.AppendLine();
                sb.AppendLine("Voice Types:");
                if (race.MaleVoiceFormId.HasValue)
                {
                    sb.AppendLine($"  Male:         {FormatFormIdWithName(race.MaleVoiceFormId.Value, lookup)}");
                }
                if (race.FemaleVoiceFormId.HasValue)
                {
                    sb.AppendLine($"  Female:       {FormatFormIdWithName(race.FemaleVoiceFormId.Value, lookup)}");
                }
            }

            if (race.OlderRaceFormId.HasValue || race.YoungerRaceFormId.HasValue)
            {
                sb.AppendLine();
                sb.AppendLine("Related Races:");
                if (race.OlderRaceFormId.HasValue)
                {
                    sb.AppendLine($"  Older:        {FormatFormIdWithName(race.OlderRaceFormId.Value, lookup)}");
                }
                if (race.YoungerRaceFormId.HasValue)
                {
                    sb.AppendLine($"  Younger:      {FormatFormIdWithName(race.YoungerRaceFormId.Value, lookup)}");
                }
            }

            if (race.AbilityFormIds.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Racial Abilities:");
                foreach (var ability in race.AbilityFormIds)
                {
                    sb.AppendLine($"  - {FormatFormIdWithName(ability, lookup)}");
                }
            }
        }
    }

    #endregion

    #region Formatting Helpers

    private static string FormatFormId(uint formId)
    {
        return $"0x{formId:X8}";
    }

    private static string FormatFormIdWithName(uint formId, Dictionary<uint, string> lookup)
    {
        if (lookup.TryGetValue(formId, out var name))
        {
            return $"{name} ({FormatFormId(formId)})";
        }

        return FormatFormId(formId);
    }

    private static string FormatModifier(sbyte value)
    {
        return value switch
        {
            > 0 => $"+{value}",
            < 0 => value.ToString(),
            _ => "+0"
        };
    }

    #endregion
}
