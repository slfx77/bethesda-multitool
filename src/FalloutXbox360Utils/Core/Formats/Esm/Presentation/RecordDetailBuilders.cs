using System.Diagnostics.CodeAnalysis;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Presentation;

/// <summary>
///     Per-record-type detail model builders. Extracted from RecordDetailPresenter.
/// </summary>
internal static class RecordDetailBuilders
{
    internal static RecordDetailModel BuildNpc(NpcRecord npc, FormIdResolver resolver)
    {
        var sections = new List<RecordDetailSection>
        {
            RecordDetailHelpers.Section("Identity",
            [
                RecordDetailHelpers.Scalar("Form ID", $"0x{npc.FormId:X8}"),
                RecordDetailHelpers.Scalar("Editor ID", npc.EditorId ?? "(none)"),
                RecordDetailHelpers.Scalar("Name", npc.FullName ?? "(none)"),
                RecordDetailHelpers.Link("Race", npc.Race, resolver),
                RecordDetailHelpers.Link("Class", npc.Class, resolver),
                RecordDetailHelpers.Scalar("Female", ((npc.Stats?.Flags ?? 0) & 1) != 0 ? "Yes" : "No"),
                RecordDetailHelpers.Scalar("Level", npc.Stats?.Level.ToString() ?? "(unknown)")
            ]),
            RecordDetailHelpers.Section("Appearance",
            [
                RecordDetailHelpers.Link("Hair", npc.HairFormId, resolver),
                RecordDetailHelpers.Scalar("Hair Color", NpcRecord.FormatHairColor(npc.HairColor)),
                RecordDetailHelpers.Link("Eyes", npc.EyesFormId, resolver),
                RecordDetailHelpers.Scalar("Height", npc.Height?.ToString("F2")),
                RecordDetailHelpers.Scalar("Weight", npc.Weight?.ToString("F1")),
                RecordDetailHelpers.Link("Original Race", npc.OriginalRace, resolver),
                RecordDetailHelpers.Link("Face NPC", npc.FaceNpc, resolver),
                RecordDetailHelpers.Scalar("Race Preset", npc.RaceFacePreset?.ToString())
            ]),
            RecordDetailHelpers.Section("AI & Scripts",
            [
                RecordDetailHelpers.Link("Script", npc.Script, resolver),
                RecordDetailHelpers.Link("Death Item", npc.DeathItem, resolver),
                RecordDetailHelpers.Link("Voice Type", npc.VoiceType, resolver),
                RecordDetailHelpers.Link("Template", npc.Template, resolver),
                RecordDetailHelpers.Link("Combat Style", npc.CombatStyleFormId, resolver)
            ]),
            RecordDetailHelpers.ListSection("Head Parts",
                npc.HeadPartFormIds?.Select(id => RecordDetailHelpers.ListLinkItem(id, resolver)).ToList()),
            RecordDetailHelpers.ListSection("SPECIAL", RecordDetailHelpers.BuildStatItems(
                ["Strength", "Perception", "Endurance", "Charisma", "Intelligence", "Agility", "Luck"],
                npc.SpecialStats?.Select(value => value.ToString()).ToArray())),
            RecordDetailHelpers.ListSection("Skills", RecordDetailHelpers.BuildSkillItems(npc.Skills, resolver)),
            RecordDetailHelpers.ListSection("Factions", npc.Factions.Select(faction => new RecordDetailListItem
            {
                Label = resolver.GetBestNameWithRefChain(faction.FactionFormId) ?? $"0x{faction.FactionFormId:X8}",
                Value = $"Rank {faction.Rank}",
                LinkedFormId = faction.FactionFormId
            }).ToList()),
            RecordDetailHelpers.ListSection("Inventory", npc.Inventory.Select(item => new RecordDetailListItem
            {
                Label = resolver.GetBestNameWithRefChain(item.ItemFormId) ?? $"0x{item.ItemFormId:X8}",
                Value = $"x{item.Count}",
                LinkedFormId = item.ItemFormId
            }).ToList()),
            RecordDetailHelpers.ListSection("AI Packages",
                npc.Packages.Select(id => RecordDetailHelpers.ListLinkItem(id, resolver)).ToList())
        };

        return RecordDetailHelpers.Model("NPC_", npc.FormId, npc.EditorId, npc.FullName, sections);
    }

    internal static RecordDetailModel BuildCreature(CreatureRecord creature, FormIdResolver resolver)
    {
        var sections = new List<RecordDetailSection>
        {
            RecordDetailHelpers.Section("Identity",
            [
                RecordDetailHelpers.Scalar("Form ID", $"0x{creature.FormId:X8}"),
                RecordDetailHelpers.Scalar("Editor ID", creature.EditorId ?? "(none)"),
                RecordDetailHelpers.Scalar("Name", creature.FullName ?? "(none)"),
                RecordDetailHelpers.Scalar("Type", creature.CreatureTypeName),
                RecordDetailHelpers.Scalar("Level", creature.Stats?.Level.ToString() ?? "(unknown)")
            ]),
            RecordDetailHelpers.Section("Combat",
            [
                RecordDetailHelpers.Scalar("Attack Damage", creature.AttackDamage.ToString()),
                RecordDetailHelpers.Scalar("Combat Skill", creature.CombatSkill.ToString()),
                RecordDetailHelpers.Scalar("Magic Skill", creature.MagicSkill.ToString()),
                RecordDetailHelpers.Scalar("Stealth Skill", creature.StealthSkill.ToString())
            ]),
            RecordDetailHelpers.Section("AI & Runtime",
            [
                RecordDetailHelpers.Link("Script", creature.Script, resolver),
                RecordDetailHelpers.Link("Death Item", creature.DeathItem, resolver),
                RecordDetailHelpers.Scalar("Aggression", creature.AiData?.AggressionName),
                RecordDetailHelpers.Scalar("Confidence", creature.AiData?.ConfidenceName),
                RecordDetailHelpers.Scalar("Assistance", creature.AiData?.AssistanceName),
                RecordDetailHelpers.Scalar("Mood", creature.AiData?.MoodName),
                RecordDetailHelpers.Scalar("Energy", creature.AiData?.EnergyLevel.ToString()),
                RecordDetailHelpers.Scalar("Model", creature.ModelPath)
            ]),
            RecordDetailHelpers.ListSection("Factions", creature.Factions.Select(faction => new RecordDetailListItem
            {
                Label = resolver.GetBestNameWithRefChain(faction.FactionFormId) ?? $"0x{faction.FactionFormId:X8}",
                Value = $"Rank {faction.Rank}",
                LinkedFormId = faction.FactionFormId
            }).ToList()),
            RecordDetailHelpers.ListSection("Spells & Abilities",
                creature.Spells.Select(id => RecordDetailHelpers.ListLinkItem(id, resolver)).ToList()),
            RecordDetailHelpers.ListSection("AI Packages",
                creature.Packages.Select(id => RecordDetailHelpers.ListLinkItem(id, resolver)).ToList())
        };

        return RecordDetailHelpers.Model("CREA", creature.FormId, creature.EditorId, creature.FullName, sections);
    }

    internal static RecordDetailModel BuildWeapon(WeaponRecord weapon, FormIdResolver resolver)
    {
        var sections = new List<RecordDetailSection>
        {
            RecordDetailHelpers.Section("Identity",
            [
                RecordDetailHelpers.Scalar("Form ID", $"0x{weapon.FormId:X8}"),
                RecordDetailHelpers.Scalar("Editor ID", weapon.EditorId ?? "(none)"),
                RecordDetailHelpers.Scalar("Name", weapon.FullName ?? "(none)"),
                RecordDetailHelpers.Scalar("Type", weapon.WeaponTypeName),
                RecordDetailHelpers.Scalar("Equipment", weapon.EquipmentTypeName),
                RecordDetailHelpers.Scalar("Skill",
                    resolver.GetActorValueName((int)weapon.Skill) ?? $"AV#{weapon.Skill}")
            ]),
            RecordDetailHelpers.Section("Combat",
            [
                RecordDetailHelpers.Scalar("Damage", weapon.Damage.ToString()),
                RecordDetailHelpers.Scalar("Critical Chance", weapon.CriticalChance.ToString("P0")),
                RecordDetailHelpers.Scalar("Critical Damage", weapon.CriticalDamage.ToString()),
                RecordDetailHelpers.Scalar("Attack Speed", weapon.Speed.ToString("F2")),
                RecordDetailHelpers.Scalar("Shots / Sec", weapon.ShotsPerSec.ToString("F2")),
                RecordDetailHelpers.Scalar("Clip Size", weapon.ClipSize.ToString()),
                RecordDetailHelpers.Scalar("DPS", weapon.DamagePerSecond.ToString("F1")),
                RecordDetailHelpers.Scalar("Min Range", weapon.MinRange.ToString("F1")),
                RecordDetailHelpers.Scalar("Max Range", weapon.MaxRange.ToString("F1"))
            ]),
            RecordDetailHelpers.Section("Requirements",
            [
                RecordDetailHelpers.Scalar("Strength Requirement", weapon.StrengthRequirement.ToString()),
                RecordDetailHelpers.Scalar("Skill Requirement", weapon.SkillRequirement.ToString()),
                RecordDetailHelpers.Scalar("Weight", weapon.Weight.ToString("F1")),
                RecordDetailHelpers.Scalar("Value", weapon.Value.ToString()),
                RecordDetailHelpers.Scalar("Health", weapon.Health.ToString())
            ]),
            RecordDetailHelpers.Section("References",
            [
                RecordDetailHelpers.Link("Ammo", weapon.AmmoFormId, resolver),
                RecordDetailHelpers.Link("Projectile", weapon.ProjectileFormId, resolver),
                RecordDetailHelpers.Link("Critical Effect", weapon.CriticalEffectFormId, resolver),
                RecordDetailHelpers.Link("Impact Data Set", weapon.ImpactDataSetFormId, resolver)
            ]),
            RecordDetailHelpers.Section("Presentation",
            [
                RecordDetailHelpers.Scalar("Model", weapon.ModelPath),
                RecordDetailHelpers.Scalar("Shell Casing", weapon.ShellCasingModelPath),
                RecordDetailHelpers.Scalar("Inventory Icon", weapon.InventoryIconPath),
                RecordDetailHelpers.Scalar("Message Icon", weapon.MessageIconPath),
                RecordDetailHelpers.Scalar("Embedded Node", weapon.EmbeddedWeaponNode),
                RecordDetailHelpers.Link("Pickup Sound", weapon.PickupSoundFormId, resolver),
                RecordDetailHelpers.Link("Putdown Sound", weapon.PutdownSoundFormId, resolver),
                RecordDetailHelpers.Link("Fire 3D Sound", weapon.FireSound3DFormId, resolver),
                RecordDetailHelpers.Link("Fire Dist Sound", weapon.FireSoundDistFormId, resolver),
                RecordDetailHelpers.Link("Fire 2D Sound", weapon.FireSound2DFormId, resolver),
                RecordDetailHelpers.Link("Attack Loop Sound", weapon.AttackLoopSoundFormId, resolver),
                RecordDetailHelpers.Link("Dry Fire Sound", weapon.DryFireSoundFormId, resolver),
                RecordDetailHelpers.Link("Melee Block Sound", weapon.MeleeBlockSoundFormId, resolver),
                RecordDetailHelpers.Link("Idle Sound", weapon.IdleSoundFormId, resolver),
                RecordDetailHelpers.Link("Equip Sound", weapon.EquipSoundFormId, resolver),
                RecordDetailHelpers.Link("Unequip Sound", weapon.UnequipSoundFormId, resolver),
                RecordDetailHelpers.Link("Mod Silenced 3D", weapon.ModSilencedSound3DFormId, resolver),
                RecordDetailHelpers.Link("Mod Silenced Dist", weapon.ModSilencedSoundDistFormId, resolver),
                RecordDetailHelpers.Link("Mod Silenced 2D", weapon.ModSilencedSound2DFormId, resolver),
                RecordDetailHelpers.Scalar("Mod Variants",
                    weapon.ModelVariants.Count > 0
                        ? string.Join(", ", weapon.ModelVariants.Select(v => v.CombinationName))
                        : null)
            ])
        };

        return RecordDetailHelpers.Model("WEAP", weapon.FormId, weapon.EditorId, weapon.FullName, sections);
    }

    [SuppressMessage("Major Code Smell", "S1172",
        Justification = "Resolver kept for signature symmetry with sibling Build* methods")]
    internal static RecordDetailModel BuildArmor(ArmorRecord armor, FormIdResolver resolver)
    {
        var sections = new List<RecordDetailSection>
        {
            RecordDetailHelpers.Section("Identity",
            [
                RecordDetailHelpers.Scalar("Form ID", $"0x{armor.FormId:X8}"),
                RecordDetailHelpers.Scalar("Editor ID", armor.EditorId ?? "(none)"),
                RecordDetailHelpers.Scalar("Name", armor.FullName ?? "(none)"),
                RecordDetailHelpers.Scalar("Equipment Type", armor.EquipmentTypeName)
            ]),
            RecordDetailHelpers.Section("Stats",
            [
                RecordDetailHelpers.Scalar("Damage Threshold", armor.DamageThreshold.ToString("F1")),
                RecordDetailHelpers.Scalar("Damage Resistance", armor.DamageResistance.ToString()),
                RecordDetailHelpers.Scalar("Weight", armor.Weight.ToString("F1")),
                RecordDetailHelpers.Scalar("Value", armor.Value.ToString()),
                RecordDetailHelpers.Scalar("Health", armor.Health.ToString()),
                RecordDetailHelpers.Scalar("Biped Flags", $"0x{armor.BipedFlags:X8}"),
                RecordDetailHelpers.Scalar("General Flags", $"0x{armor.GeneralFlags:X2}")
            ]),
            RecordDetailHelpers.Section("Presentation",
            [
                RecordDetailHelpers.Scalar("Model", armor.ModelPath),
                RecordDetailHelpers.Scalar("Bounds", RecordDetailHelpers.FormatBounds(armor.Bounds))
            ])
        };

        return RecordDetailHelpers.Model("ARMO", armor.FormId, armor.EditorId, armor.FullName, sections);
    }

    internal static RecordDetailModel BuildQuest(QuestRecord quest, FormIdResolver resolver)
    {
        var sections = new List<RecordDetailSection>
        {
            RecordDetailHelpers.Section("Identity",
            [
                RecordDetailHelpers.Scalar("Form ID", $"0x{quest.FormId:X8}"),
                RecordDetailHelpers.Scalar("Editor ID", quest.EditorId ?? "(none)"),
                RecordDetailHelpers.Scalar("Name", quest.FullName ?? "(none)"),
                RecordDetailHelpers.Scalar("Priority", quest.Priority.ToString()),
                RecordDetailHelpers.Scalar("Flags", $"0x{quest.Flags:X2}"),
                RecordDetailHelpers.Scalar("Quest Delay", quest.QuestDelay.ToString("F2")),
                RecordDetailHelpers.Link("Script", quest.Script, resolver)
            ]),
            RecordDetailHelpers.ListSection("Objectives", quest.Objectives
                .OrderBy(objective => objective.Index)
                .Select(objective => new RecordDetailListItem
                {
                    Label = $"[{objective.Index}]",
                    Value = objective.DisplayText ?? "(no text)"
                })
                .ToList()),
            RecordDetailHelpers.ListSection("Stages", quest.Stages
                .OrderBy(stage => stage.Index)
                .Select(stage => new RecordDetailListItem
                {
                    Label = $"[{stage.Index}]",
                    Value = $"Flags 0x{stage.Flags:X2}"
                })
                .ToList()),
            RecordDetailHelpers.ListSection("Variables", quest.Variables.Select(variable => new RecordDetailListItem
            {
                Label = variable.Name ?? $"var_{variable.Index}",
                Value = $"{variable.TypeName}, idx {variable.Index}"
            }).ToList()),
            RecordDetailHelpers.ListSection("Related NPCs",
                quest.RelatedNpcFormIds.Select(id => RecordDetailHelpers.ListLinkItem(id, resolver)).ToList())
        };

        return RecordDetailHelpers.Model("QUST", quest.FormId, quest.EditorId, quest.FullName, sections);
    }

    internal static RecordDetailModel BuildPackage(PackageRecord package, FormIdResolver resolver)
    {
        var sections = new List<RecordDetailSection>
        {
            RecordDetailHelpers.Section("Identity",
            [
                RecordDetailHelpers.Scalar("Form ID", $"0x{package.FormId:X8}"),
                RecordDetailHelpers.Scalar("Editor ID", package.EditorId ?? "(none)"),
                RecordDetailHelpers.Scalar("Type", package.TypeName),
                RecordDetailHelpers.Scalar("Repeatable", package.IsRepeatable ? "Yes" : "No"),
                RecordDetailHelpers.Scalar("Linked Start", package.IsStartingLocationLinkedRef ? "Yes" : "No")
            ]),
            RecordDetailHelpers.Section("Schedule",
            [
                RecordDetailHelpers.Scalar("Summary", package.Schedule?.Summary),
                RecordDetailHelpers.Scalar("Month", package.Schedule?.MonthName),
                RecordDetailHelpers.Scalar("Day", package.Schedule?.DayOfWeekName),
                RecordDetailHelpers.Scalar("Date", package.Schedule?.Date.ToString()),
                RecordDetailHelpers.Scalar("Hour", package.Schedule?.Time.ToString()),
                RecordDetailHelpers.Scalar("Duration", package.Schedule?.Duration.ToString())
            ]),
            RecordDetailHelpers.Section("Location",
            [
                RecordDetailHelpers.Scalar("Primary",
                    RecordDetailHelpers.FormatPackageLocation(package.Location, resolver)),
                RecordDetailHelpers.Scalar("Secondary",
                    RecordDetailHelpers.FormatPackageLocation(package.Location2, resolver))
            ]),
            RecordDetailHelpers.Section("Target",
            [
                RecordDetailHelpers.Scalar("Primary",
                    RecordDetailHelpers.FormatPackageTarget(package.Target, resolver)),
                RecordDetailHelpers.Scalar("Secondary",
                    RecordDetailHelpers.FormatPackageTarget(package.Target2, resolver))
            ]),
            RecordDetailHelpers.Section("Use Weapon",
            [
                RecordDetailHelpers.Link("Weapon", package.UseWeaponData?.WeaponFormId, resolver),
                RecordDetailHelpers.Scalar("Always Hit",
                    RecordDetailHelpers.BoolText(package.UseWeaponData?.AlwaysHit)),
                RecordDetailHelpers.Scalar("Do No Damage",
                    RecordDetailHelpers.BoolText(package.UseWeaponData?.DoNoDamage)),
                RecordDetailHelpers.Scalar("Crouch", RecordDetailHelpers.BoolText(package.UseWeaponData?.Crouch)),
                RecordDetailHelpers.Scalar("Hold Fire", RecordDetailHelpers.BoolText(package.UseWeaponData?.HoldFire)),
                RecordDetailHelpers.Scalar("Burst Count", package.UseWeaponData?.BurstCount.ToString()),
                RecordDetailHelpers.Scalar("Volley", RecordDetailHelpers.FormatVolley(package.UseWeaponData))
            ])
        };

        return RecordDetailHelpers.Model("PACK", package.FormId, package.EditorId, package.TypeName, sections);
    }

    internal static RecordDetailModel BuildDialogTopic(
        DialogTopicRecord topic,
        RecordCollection? records,
        FormIdResolver resolver)
    {
        var sections = new List<RecordDetailSection>
        {
            RecordDetailHelpers.Section("Identity",
            [
                RecordDetailHelpers.Scalar("Form ID", $"0x{topic.FormId:X8}"),
                RecordDetailHelpers.Scalar("Editor ID", topic.EditorId ?? "(none)"),
                RecordDetailHelpers.Scalar("Name", topic.FullName ?? "(none)"),
                RecordDetailHelpers.Scalar("Type", topic.TopicTypeName),
                RecordDetailHelpers.Link("Quest", topic.QuestFormId, resolver),
                RecordDetailHelpers.Link("Speaker", topic.SpeakerFormId, resolver),
                RecordDetailHelpers.Scalar("Responses", topic.ResponseCount.ToString()),
                RecordDetailHelpers.Scalar("Flags", $"0x{topic.Flags:X2}"),
                RecordDetailHelpers.Scalar("Priority", topic.Priority.ToString("F2")),
                RecordDetailHelpers.Scalar("Journal Index", topic.JournalIndex.ToString()),
                RecordDetailHelpers.Scalar("Dummy Prompt", topic.DummyPrompt)
            ])
        };

        if (records != null)
        {
            var infos = records.Dialogues
                .Where(dialogue => dialogue.TopicFormId == topic.FormId)
                .Take(20)
                .Select(dialogue => new RecordDetailListItem
                {
                    Label = $"0x{dialogue.FormId:X8}",
                    Value = dialogue.Responses.FirstOrDefault()?.Text
                            ?? dialogue.PromptText
                            ?? "(no text)",
                    LinkedFormId = dialogue.FormId
                })
                .ToList();
            sections.Add(RecordDetailHelpers.ListSection("INFO Records", infos));
        }

        return RecordDetailHelpers.Model("DIAL", topic.FormId, topic.EditorId, topic.FullName, sections);
    }

    internal static RecordDetailModel BuildCell(CellRecord cell, FormIdResolver resolver)
    {
        var sections = new List<RecordDetailSection>
        {
            RecordDetailHelpers.Section("Identity",
            [
                RecordDetailHelpers.Scalar("Form ID", $"0x{cell.FormId:X8}"),
                RecordDetailHelpers.Scalar("Editor ID", cell.EditorId ?? "(none)"),
                RecordDetailHelpers.Scalar("Name", cell.FullName ?? "(none)"),
                RecordDetailHelpers.Scalar("Interior", cell.IsInterior ? "Yes" : "No"),
                RecordDetailHelpers.Scalar("Grid",
                    cell.GridX.HasValue && cell.GridY.HasValue ? $"({cell.GridX}, {cell.GridY})" : null),
                RecordDetailHelpers.Link("Worldspace", cell.WorldspaceFormId, resolver)
            ]),
            RecordDetailHelpers.Section("Environment",
            [
                RecordDetailHelpers.Scalar("Flags", $"0x{cell.Flags:X2}"),
                RecordDetailHelpers.Scalar("Has Water", cell.HasWater ? "Yes" : "No"),
                RecordDetailHelpers.Scalar("Water Height", cell.WaterHeight?.ToString("F2")),
                RecordDetailHelpers.Link("Encounter Zone", cell.EncounterZoneFormId, resolver),
                RecordDetailHelpers.Link("Music Type", cell.MusicTypeFormId, resolver),
                RecordDetailHelpers.Link("Acoustic Space", cell.AcousticSpaceFormId, resolver),
                RecordDetailHelpers.Link("Image Space", cell.ImageSpaceFormId, resolver),
                RecordDetailHelpers.Link("Lighting Template", cell.LightingTemplateFormId, resolver)
            ]),
            RecordDetailHelpers.Section("Content",
            [
                RecordDetailHelpers.Scalar("Placed Objects", cell.PlacedObjects.Count.ToString()),
                RecordDetailHelpers.Scalar("Linked Cells", cell.LinkedCellFormIds.Count.ToString()),
                RecordDetailHelpers.Scalar("Persistent Objects", cell.HasPersistentObjects ? "Yes" : "No"),
                RecordDetailHelpers.Scalar("Persistent Cell Container", cell.IsPersistentCell ? "Yes" : "No"),
                RecordDetailHelpers.Scalar("Virtual Cell", cell.IsVirtual ? "Yes" : "No"),
                RecordDetailHelpers.Scalar("Unresolved Bucket", cell.IsUnresolvedBucket ? "Yes" : "No"),
                RecordDetailHelpers.Scalar("Heightmap", cell.Heightmap != null ? "Present" : "Absent"),
                RecordDetailHelpers.Scalar("Runtime Terrain Mesh",
                    cell.RuntimeTerrainMesh != null ? "Present" : "Absent")
            ]),
            RecordDetailHelpers.ListSection("Linked Cells",
                cell.LinkedCellFormIds.Select(id => RecordDetailHelpers.ListLinkItem(id, resolver)).ToList()),
            RecordDetailHelpers.ListSection("Door Links", BuildDoorLinkItems(cell, resolver))
        };

        return RecordDetailHelpers.Model("CELL", cell.FormId, cell.EditorId, cell.FullName, sections);
    }

    private static List<RecordDetailListItem> BuildDoorLinkItems(CellRecord cell, FormIdResolver resolver)
    {
        return cell.PlacedObjects
            .Where(obj => obj.DestinationCellFormId is > 0)
            .OrderBy(obj => obj.FormId)
            .Select(obj =>
            {
                var destinationCellFormId = obj.DestinationCellFormId.GetValueOrDefault();
                var referenceName = !string.IsNullOrEmpty(obj.EditorId)
                    ? obj.EditorId
                    : resolver.GetEditorId(obj.FormId)
                      ?? obj.BaseEditorId
                      ?? resolver.GetBestName(obj.BaseFormId)
                      ?? $"0x{obj.FormId:X8}";
                var destinationCell = resolver.FormatFull(destinationCellFormId);

                return new RecordDetailListItem
                {
                    Label = $"{referenceName} ({obj.RecordType} 0x{obj.FormId:X8})",
                    Value = $"Links to: {destinationCell}"
                };
            })
            .ToList();
    }

    internal static RecordDetailModel BuildWorldspace(WorldspaceRecord worldspace, FormIdResolver resolver)
    {
        var sections = new List<RecordDetailSection>
        {
            RecordDetailHelpers.Section("Identity",
            [
                RecordDetailHelpers.Scalar("Form ID", $"0x{worldspace.FormId:X8}"),
                RecordDetailHelpers.Scalar("Editor ID", worldspace.EditorId ?? "(none)"),
                RecordDetailHelpers.Scalar("Name", worldspace.FullName ?? "(none)"),
                RecordDetailHelpers.Link("Parent", worldspace.ParentWorldspaceFormId, resolver),
                RecordDetailHelpers.Link("Climate", worldspace.ClimateFormId, resolver),
                RecordDetailHelpers.Link("Water", worldspace.WaterFormId, resolver),
                RecordDetailHelpers.Link("Encounter Zone", worldspace.EncounterZoneFormId, resolver),
                RecordDetailHelpers.Link("Image Space", worldspace.ImageSpaceFormId, resolver),
                RecordDetailHelpers.Link("Music Type", worldspace.MusicTypeFormId, resolver)
            ]),
            RecordDetailHelpers.Section("Map Data",
            [
                RecordDetailHelpers.Scalar("Usable Width", worldspace.MapUsableWidth?.ToString()),
                RecordDetailHelpers.Scalar("Usable Height", worldspace.MapUsableHeight?.ToString()),
                RecordDetailHelpers.Scalar("NW Cell",
                    RecordDetailHelpers.FormatCellCorner(worldspace.MapNWCellX, worldspace.MapNWCellY)),
                RecordDetailHelpers.Scalar("SE Cell",
                    RecordDetailHelpers.FormatCellCorner(worldspace.MapSECellX, worldspace.MapSECellY)),
                RecordDetailHelpers.Scalar("Default Land Height", worldspace.DefaultLandHeight?.ToString("F2")),
                RecordDetailHelpers.Scalar("Default Water Height", worldspace.DefaultWaterHeight?.ToString("F2")),
                RecordDetailHelpers.Scalar("Map Offset", RecordDetailHelpers.FormatMapOffset(worldspace))
            ]),
            RecordDetailHelpers.Section("Bounds & Stats",
            [
                RecordDetailHelpers.Scalar("Bounds", RecordDetailHelpers.FormatWorldBounds(worldspace)),
                RecordDetailHelpers.Scalar("Flags",
                    worldspace.Flags.HasValue ? $"0x{worldspace.Flags.Value:X2}" : null),
                RecordDetailHelpers.Scalar("Parent Use Flags", worldspace.ParentUseFlags.HasValue
                    ? $"0x{worldspace.ParentUseFlags.Value:X4}"
                    : null),
                RecordDetailHelpers.Scalar("Cells", worldspace.Cells.Count.ToString())
            ])
        };

        return RecordDetailHelpers.Model("WRLD", worldspace.FormId, worldspace.EditorId, worldspace.FullName, sections);
    }
}
