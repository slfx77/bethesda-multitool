using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Presentation;

internal static class RecordDetailPresenter
{
    internal static bool TryBuildForLookup(
        RecordCollection records,
        FormIdResolver resolver,
        uint? formId,
        string? editorId,
        out RecordDetailModel? model)
    {
        if (TryFind(records.Npcs, formId, editorId, r => r.FormId, r => r.EditorId, out var npc))
        {
            model = BuildNpc(npc!, resolver);
            return true;
        }

        if (TryFind(records.Creatures, formId, editorId, r => r.FormId, r => r.EditorId, out var creature))
        {
            model = BuildCreature(creature!, resolver);
            return true;
        }

        if (TryFind(records.Weapons, formId, editorId, r => r.FormId, r => r.EditorId, out var weapon))
        {
            model = BuildWeapon(weapon!, resolver);
            return true;
        }

        if (TryFind(records.Armor, formId, editorId, r => r.FormId, r => r.EditorId, out var armor))
        {
            model = BuildArmor(armor!, resolver);
            return true;
        }

        if (TryFind(records.Quests, formId, editorId, r => r.FormId, r => r.EditorId, out var quest))
        {
            model = BuildQuest(quest!, resolver);
            return true;
        }

        if (TryFind(records.Packages, formId, editorId, r => r.FormId, r => r.EditorId, out var package))
        {
            model = BuildPackage(package!, resolver);
            return true;
        }

        if (TryFind(records.DialogTopics, formId, editorId, r => r.FormId, r => r.EditorId, out var topic))
        {
            model = BuildDialogTopic(topic!, records, resolver);
            return true;
        }

        if (TryFind(records.Cells, formId, editorId, r => r.FormId, r => r.EditorId, out var cell))
        {
            model = BuildCell(cell!, resolver);
            return true;
        }

        if (TryFind(records.Worldspaces, formId, editorId, r => r.FormId, r => r.EditorId,
                out var worldspace))
        {
            model = BuildWorldspace(worldspace!, resolver);
            return true;
        }

        model = null;
        return false;
    }

    internal static bool TryBuildForRecord(
        object record,
        RecordCollection? records,
        FormIdResolver resolver,
        out RecordDetailModel? model)
    {
        switch (record)
        {
            case NpcRecord npc:
                model = BuildNpc(npc, resolver);
                return true;
            case CreatureRecord creature:
                model = BuildCreature(creature, resolver);
                return true;
            case WeaponRecord weapon:
                model = BuildWeapon(weapon, resolver);
                return true;
            case ArmorRecord armor:
                model = BuildArmor(armor, resolver);
                return true;
            case QuestRecord quest:
                model = BuildQuest(quest, resolver);
                return true;
            case PackageRecord package:
                model = BuildPackage(package, resolver);
                return true;
            case DialogTopicRecord topic:
                model = BuildDialogTopic(topic, records, resolver);
                return true;
            case CellRecord cell:
                model = BuildCell(cell, resolver);
                return true;
            case WorldspaceRecord worldspace:
                model = BuildWorldspace(worldspace, resolver);
                return true;
            default:
                model = null;
                return false;
        }
    }

    private static RecordDetailModel BuildNpc(NpcRecord npc, FormIdResolver resolver)
    {
        var sections = new List<RecordDetailSection>
        {
            Section("Identity",
            [
                Scalar("Form ID", $"0x{npc.FormId:X8}"),
                Scalar("Editor ID", npc.EditorId ?? "(none)"),
                Scalar("Name", npc.FullName ?? "(none)"),
                Link("Race", npc.Race, resolver),
                Link("Class", npc.Class, resolver),
                Scalar("Female", ((npc.Stats?.Flags ?? 0) & 1) != 0 ? "Yes" : "No"),
                Scalar("Level", npc.Stats?.Level.ToString() ?? "(unknown)")
            ]),
            Section("Appearance",
            [
                Link("Hair", npc.HairFormId, resolver),
                Scalar("Hair Color", NpcRecord.FormatHairColor(npc.HairColor)),
                Link("Eyes", npc.EyesFormId, resolver),
                Scalar("Height", npc.Height?.ToString("F2")),
                Scalar("Weight", npc.Weight?.ToString("F1")),
                Link("Original Race", npc.OriginalRace, resolver),
                Link("Face NPC", npc.FaceNpc, resolver),
                Scalar("Race Preset", npc.RaceFacePreset?.ToString())
            ]),
            Section("AI & Scripts",
            [
                Link("Script", npc.Script, resolver),
                Link("Death Item", npc.DeathItem, resolver),
                Link("Voice Type", npc.VoiceType, resolver),
                Link("Template", npc.Template, resolver),
                Link("Combat Style", npc.CombatStyleFormId, resolver)
            ]),
            ListSection("Head Parts", npc.HeadPartFormIds?.Select(id => ListLinkItem(id, resolver)).ToList()),
            ListSection("SPECIAL", BuildStatItems(
                ["Strength", "Perception", "Endurance", "Charisma", "Intelligence", "Agility", "Luck"],
                npc.SpecialStats?.Select(value => value.ToString()).ToArray())),
            ListSection("Skills", BuildSkillItems(npc.Skills, resolver)),
            ListSection("Factions", npc.Factions.Select(faction => new RecordDetailListItem
            {
                Label = resolver.GetBestNameWithRefChain(faction.FactionFormId) ?? $"0x{faction.FactionFormId:X8}",
                Value = $"Rank {faction.Rank}",
                LinkedFormId = faction.FactionFormId
            }).ToList()),
            ListSection("Inventory", npc.Inventory.Select(item => new RecordDetailListItem
            {
                Label = resolver.GetBestNameWithRefChain(item.ItemFormId) ?? $"0x{item.ItemFormId:X8}",
                Value = $"x{item.Count}",
                LinkedFormId = item.ItemFormId
            }).ToList()),
            ListSection("AI Packages", npc.Packages.Select(id => ListLinkItem(id, resolver)).ToList())
        };

        return Model("NPC_", npc.FormId, npc.EditorId, npc.FullName, sections);
    }

    private static RecordDetailModel BuildCreature(CreatureRecord creature, FormIdResolver resolver)
    {
        var sections = new List<RecordDetailSection>
        {
            Section("Identity",
            [
                Scalar("Form ID", $"0x{creature.FormId:X8}"),
                Scalar("Editor ID", creature.EditorId ?? "(none)"),
                Scalar("Name", creature.FullName ?? "(none)"),
                Scalar("Type", creature.CreatureTypeName),
                Scalar("Level", creature.Stats?.Level.ToString() ?? "(unknown)")
            ]),
            Section("Combat",
            [
                Scalar("Attack Damage", creature.AttackDamage.ToString()),
                Scalar("Combat Skill", creature.CombatSkill.ToString()),
                Scalar("Magic Skill", creature.MagicSkill.ToString()),
                Scalar("Stealth Skill", creature.StealthSkill.ToString())
            ]),
            Section("AI & Runtime",
            [
                Link("Script", creature.Script, resolver),
                Link("Death Item", creature.DeathItem, resolver),
                Scalar("Aggression", creature.AiData?.AggressionName),
                Scalar("Confidence", creature.AiData?.ConfidenceName),
                Scalar("Assistance", creature.AiData?.AssistanceName),
                Scalar("Mood", creature.AiData?.MoodName),
                Scalar("Energy", creature.AiData?.EnergyLevel.ToString()),
                Scalar("Model", creature.ModelPath)
            ]),
            ListSection("Factions", creature.Factions.Select(faction => new RecordDetailListItem
            {
                Label = resolver.GetBestNameWithRefChain(faction.FactionFormId) ?? $"0x{faction.FactionFormId:X8}",
                Value = $"Rank {faction.Rank}",
                LinkedFormId = faction.FactionFormId
            }).ToList()),
            ListSection("Spells & Abilities", creature.Spells.Select(id => ListLinkItem(id, resolver)).ToList()),
            ListSection("AI Packages", creature.Packages.Select(id => ListLinkItem(id, resolver)).ToList())
        };

        return Model("CREA", creature.FormId, creature.EditorId, creature.FullName, sections);
    }

    private static RecordDetailModel BuildWeapon(WeaponRecord weapon, FormIdResolver resolver)
    {
        var sections = new List<RecordDetailSection>
        {
            Section("Identity",
            [
                Scalar("Form ID", $"0x{weapon.FormId:X8}"),
                Scalar("Editor ID", weapon.EditorId ?? "(none)"),
                Scalar("Name", weapon.FullName ?? "(none)"),
                Scalar("Type", weapon.WeaponTypeName),
                Scalar("Equipment", weapon.EquipmentTypeName),
                Scalar("Skill", resolver.GetActorValueName((int)weapon.Skill) ?? $"AV#{weapon.Skill}")
            ]),
            Section("Combat",
            [
                Scalar("Damage", weapon.Damage.ToString()),
                Scalar("Critical Chance", weapon.CriticalChance.ToString("P0")),
                Scalar("Critical Damage", weapon.CriticalDamage.ToString()),
                Scalar("Attack Speed", weapon.Speed.ToString("F2")),
                Scalar("Shots / Sec", weapon.ShotsPerSec.ToString("F2")),
                Scalar("Clip Size", weapon.ClipSize.ToString()),
                Scalar("DPS", weapon.DamagePerSecond.ToString("F1")),
                Scalar("Min Range", weapon.MinRange.ToString("F1")),
                Scalar("Max Range", weapon.MaxRange.ToString("F1"))
            ]),
            Section("Requirements",
            [
                Scalar("Strength Requirement", weapon.StrengthRequirement.ToString()),
                Scalar("Skill Requirement", weapon.SkillRequirement.ToString()),
                Scalar("Weight", weapon.Weight.ToString("F1")),
                Scalar("Value", weapon.Value.ToString()),
                Scalar("Health", weapon.Health.ToString())
            ]),
            Section("References",
            [
                Link("Ammo", weapon.AmmoFormId, resolver),
                Link("Projectile", weapon.ProjectileFormId, resolver),
                Link("Critical Effect", weapon.CriticalEffectFormId, resolver),
                Link("Impact Data Set", weapon.ImpactDataSetFormId, resolver)
            ]),
            Section("Presentation",
            [
                Scalar("Model", weapon.ModelPath),
                Scalar("Shell Casing", weapon.ShellCasingModelPath),
                Scalar("Inventory Icon", weapon.InventoryIconPath),
                Scalar("Message Icon", weapon.MessageIconPath),
                Scalar("Embedded Node", weapon.EmbeddedWeaponNode),
                Link("Pickup Sound", weapon.PickupSoundFormId, resolver),
                Link("Putdown Sound", weapon.PutdownSoundFormId, resolver),
                Link("Fire 3D Sound", weapon.FireSound3DFormId, resolver),
                Link("Fire Dist Sound", weapon.FireSoundDistFormId, resolver),
                Link("Fire 2D Sound", weapon.FireSound2DFormId, resolver),
                Link("Attack Loop Sound", weapon.AttackLoopSoundFormId, resolver),
                Link("Dry Fire Sound", weapon.DryFireSoundFormId, resolver),
                Link("Melee Block Sound", weapon.MeleeBlockSoundFormId, resolver),
                Link("Idle Sound", weapon.IdleSoundFormId, resolver),
                Link("Equip Sound", weapon.EquipSoundFormId, resolver),
                Link("Unequip Sound", weapon.UnequipSoundFormId, resolver),
                Link("Mod Silenced 3D", weapon.ModSilencedSound3DFormId, resolver),
                Link("Mod Silenced Dist", weapon.ModSilencedSoundDistFormId, resolver),
                Link("Mod Silenced 2D", weapon.ModSilencedSound2DFormId, resolver),
                Scalar("Mod Variants",
                    weapon.ModelVariants.Count > 0
                        ? string.Join(", ", weapon.ModelVariants.Select(v => v.CombinationName))
                        : null)
            ])
        };

        return Model("WEAP", weapon.FormId, weapon.EditorId, weapon.FullName, sections);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1172",
        Justification = "Resolver kept for signature symmetry with sibling Build* methods")]
    private static RecordDetailModel BuildArmor(ArmorRecord armor, FormIdResolver resolver)
    {
        var sections = new List<RecordDetailSection>
        {
            Section("Identity",
            [
                Scalar("Form ID", $"0x{armor.FormId:X8}"),
                Scalar("Editor ID", armor.EditorId ?? "(none)"),
                Scalar("Name", armor.FullName ?? "(none)"),
                Scalar("Equipment Type", armor.EquipmentTypeName)
            ]),
            Section("Stats",
            [
                Scalar("Damage Threshold", armor.DamageThreshold.ToString("F1")),
                Scalar("Damage Resistance", armor.DamageResistance.ToString()),
                Scalar("Weight", armor.Weight.ToString("F1")),
                Scalar("Value", armor.Value.ToString()),
                Scalar("Health", armor.Health.ToString()),
                Scalar("Biped Flags", $"0x{armor.BipedFlags:X8}"),
                Scalar("General Flags", $"0x{armor.GeneralFlags:X2}")
            ]),
            Section("Presentation",
            [
                Scalar("Model", armor.ModelPath),
                Scalar("Bounds", FormatBounds(armor.Bounds))
            ])
        };

        return Model("ARMO", armor.FormId, armor.EditorId, armor.FullName, sections);
    }

    private static RecordDetailModel BuildQuest(QuestRecord quest, FormIdResolver resolver)
    {
        var sections = new List<RecordDetailSection>
        {
            Section("Identity",
            [
                Scalar("Form ID", $"0x{quest.FormId:X8}"),
                Scalar("Editor ID", quest.EditorId ?? "(none)"),
                Scalar("Name", quest.FullName ?? "(none)"),
                Scalar("Priority", quest.Priority.ToString()),
                Scalar("Flags", $"0x{quest.Flags:X2}"),
                Scalar("Quest Delay", quest.QuestDelay.ToString("F2")),
                Link("Script", quest.Script, resolver)
            ]),
            ListSection("Objectives", quest.Objectives
                .OrderBy(objective => objective.Index)
                .Select(objective => new RecordDetailListItem
                {
                    Label = $"[{objective.Index}]",
                    Value = objective.DisplayText ?? "(no text)"
                })
                .ToList()),
            ListSection("Stages", quest.Stages
                .OrderBy(stage => stage.Index)
                .Select(stage => new RecordDetailListItem
                {
                    Label = $"[{stage.Index}]",
                    Value = $"Flags 0x{stage.Flags:X2}"
                })
                .ToList()),
            ListSection("Variables", quest.Variables.Select(variable => new RecordDetailListItem
            {
                Label = variable.Name ?? $"var_{variable.Index}",
                Value = $"{variable.TypeName}, idx {variable.Index}"
            }).ToList()),
            ListSection("Related NPCs", quest.RelatedNpcFormIds.Select(id => ListLinkItem(id, resolver)).ToList())
        };

        return Model("QUST", quest.FormId, quest.EditorId, quest.FullName, sections);
    }

    private static RecordDetailModel BuildPackage(PackageRecord package, FormIdResolver resolver)
    {
        var sections = new List<RecordDetailSection>
        {
            Section("Identity",
            [
                Scalar("Form ID", $"0x{package.FormId:X8}"),
                Scalar("Editor ID", package.EditorId ?? "(none)"),
                Scalar("Type", package.TypeName),
                Scalar("Repeatable", package.IsRepeatable ? "Yes" : "No"),
                Scalar("Linked Start", package.IsStartingLocationLinkedRef ? "Yes" : "No")
            ]),
            Section("Schedule",
            [
                Scalar("Summary", package.Schedule?.Summary),
                Scalar("Month", package.Schedule?.MonthName),
                Scalar("Day", package.Schedule?.DayOfWeekName),
                Scalar("Date", package.Schedule?.Date.ToString()),
                Scalar("Hour", package.Schedule?.Time.ToString()),
                Scalar("Duration", package.Schedule?.Duration.ToString())
            ]),
            Section("Location",
            [
                Scalar("Primary", FormatPackageLocation(package.Location, resolver)),
                Scalar("Secondary", FormatPackageLocation(package.Location2, resolver))
            ]),
            Section("Target",
            [
                Scalar("Primary", FormatPackageTarget(package.Target, resolver)),
                Scalar("Secondary", FormatPackageTarget(package.Target2, resolver))
            ]),
            Section("Use Weapon",
            [
                Link("Weapon", package.UseWeaponData?.WeaponFormId, resolver),
                Scalar("Always Hit", BoolText(package.UseWeaponData?.AlwaysHit)),
                Scalar("Do No Damage", BoolText(package.UseWeaponData?.DoNoDamage)),
                Scalar("Crouch", BoolText(package.UseWeaponData?.Crouch)),
                Scalar("Hold Fire", BoolText(package.UseWeaponData?.HoldFire)),
                Scalar("Burst Count", package.UseWeaponData?.BurstCount.ToString()),
                Scalar("Volley", FormatVolley(package.UseWeaponData))
            ])
        };

        return Model("PACK", package.FormId, package.EditorId, package.TypeName, sections);
    }

    private static RecordDetailModel BuildDialogTopic(
        DialogTopicRecord topic,
        RecordCollection? records,
        FormIdResolver resolver)
    {
        var sections = new List<RecordDetailSection>
        {
            Section("Identity",
            [
                Scalar("Form ID", $"0x{topic.FormId:X8}"),
                Scalar("Editor ID", topic.EditorId ?? "(none)"),
                Scalar("Name", topic.FullName ?? "(none)"),
                Scalar("Type", topic.TopicTypeName),
                Link("Quest", topic.QuestFormId, resolver),
                Link("Speaker", topic.SpeakerFormId, resolver),
                Scalar("Responses", topic.ResponseCount.ToString()),
                Scalar("Flags", $"0x{topic.Flags:X2}"),
                Scalar("Priority", topic.Priority.ToString("F2")),
                Scalar("Journal Index", topic.JournalIndex.ToString()),
                Scalar("Dummy Prompt", topic.DummyPrompt)
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
            sections.Add(ListSection("INFO Records", infos));
        }

        return Model("DIAL", topic.FormId, topic.EditorId, topic.FullName, sections);
    }

    private static RecordDetailModel BuildCell(CellRecord cell, FormIdResolver resolver)
    {
        var sections = new List<RecordDetailSection>
        {
            Section("Identity",
            [
                Scalar("Form ID", $"0x{cell.FormId:X8}"),
                Scalar("Editor ID", cell.EditorId ?? "(none)"),
                Scalar("Name", cell.FullName ?? "(none)"),
                Scalar("Interior", cell.IsInterior ? "Yes" : "No"),
                Scalar("Grid", cell.GridX.HasValue && cell.GridY.HasValue ? $"({cell.GridX}, {cell.GridY})" : null),
                Link("Worldspace", cell.WorldspaceFormId, resolver)
            ]),
            Section("Environment",
            [
                Scalar("Flags", $"0x{cell.Flags:X2}"),
                Scalar("Has Water", cell.HasWater ? "Yes" : "No"),
                Scalar("Water Height", cell.WaterHeight?.ToString("F2")),
                Link("Encounter Zone", cell.EncounterZoneFormId, resolver),
                Link("Music Type", cell.MusicTypeFormId, resolver),
                Link("Acoustic Space", cell.AcousticSpaceFormId, resolver),
                Link("Image Space", cell.ImageSpaceFormId, resolver),
                Link("Lighting Template", cell.LightingTemplateFormId, resolver)
            ]),
            Section("Content",
            [
                Scalar("Placed Objects", cell.PlacedObjects.Count.ToString()),
                Scalar("Linked Cells", cell.LinkedCellFormIds.Count.ToString()),
                Scalar("Persistent Objects", cell.HasPersistentObjects ? "Yes" : "No"),
                Scalar("Persistent Cell Container", cell.IsPersistentCell ? "Yes" : "No"),
                Scalar("Virtual Cell", cell.IsVirtual ? "Yes" : "No"),
                Scalar("Unresolved Bucket", cell.IsUnresolvedBucket ? "Yes" : "No"),
                Scalar("Heightmap", cell.Heightmap != null ? "Present" : "Absent"),
                Scalar("Runtime Terrain Mesh", cell.RuntimeTerrainMesh != null ? "Present" : "Absent")
            ]),
            ListSection("Linked Cells", cell.LinkedCellFormIds.Select(id => ListLinkItem(id, resolver)).ToList())
        };

        return Model("CELL", cell.FormId, cell.EditorId, cell.FullName, sections);
    }

    private static RecordDetailModel BuildWorldspace(WorldspaceRecord worldspace, FormIdResolver resolver)
    {
        var sections = new List<RecordDetailSection>
        {
            Section("Identity",
            [
                Scalar("Form ID", $"0x{worldspace.FormId:X8}"),
                Scalar("Editor ID", worldspace.EditorId ?? "(none)"),
                Scalar("Name", worldspace.FullName ?? "(none)"),
                Link("Parent", worldspace.ParentWorldspaceFormId, resolver),
                Link("Climate", worldspace.ClimateFormId, resolver),
                Link("Water", worldspace.WaterFormId, resolver),
                Link("Encounter Zone", worldspace.EncounterZoneFormId, resolver),
                Link("Image Space", worldspace.ImageSpaceFormId, resolver),
                Link("Music Type", worldspace.MusicTypeFormId, resolver)
            ]),
            Section("Map Data",
            [
                Scalar("Usable Width", worldspace.MapUsableWidth?.ToString()),
                Scalar("Usable Height", worldspace.MapUsableHeight?.ToString()),
                Scalar("NW Cell", FormatCellCorner(worldspace.MapNWCellX, worldspace.MapNWCellY)),
                Scalar("SE Cell", FormatCellCorner(worldspace.MapSECellX, worldspace.MapSECellY)),
                Scalar("Default Land Height", worldspace.DefaultLandHeight?.ToString("F2")),
                Scalar("Default Water Height", worldspace.DefaultWaterHeight?.ToString("F2")),
                Scalar("Map Offset", FormatMapOffset(worldspace))
            ]),
            Section("Bounds & Stats",
            [
                Scalar("Bounds", FormatWorldBounds(worldspace)),
                Scalar("Flags", worldspace.Flags.HasValue ? $"0x{worldspace.Flags.Value:X2}" : null),
                Scalar("Parent Use Flags", worldspace.ParentUseFlags.HasValue
                    ? $"0x{worldspace.ParentUseFlags.Value:X4}"
                    : null),
                Scalar("Cells", worldspace.Cells.Count.ToString())
            ])
        };

        return Model("WRLD", worldspace.FormId, worldspace.EditorId, worldspace.FullName, sections);
    }

    private static bool TryFind<T>(
        IEnumerable<T> records,
        uint? formId,
        string? editorId,
        Func<T, uint> formIdSelector,
        Func<T, string?> editorIdSelector,
        out T? match)
        where T : class
    {
        match = records.FirstOrDefault(record =>
            (formId.HasValue && formIdSelector(record) == formId.Value) ||
            (!string.IsNullOrEmpty(editorId) &&
             string.Equals(editorIdSelector(record), editorId, StringComparison.OrdinalIgnoreCase)));
        return match != null;
    }

    private static RecordDetailModel Model(
        string signature,
        uint formId,
        string? editorId,
        string? displayName,
        IEnumerable<RecordDetailSection> sections)
    {
        return new RecordDetailModel
        {
            RecordSignature = signature,
            FormId = formId,
            EditorId = editorId,
            DisplayName = displayName,
            Sections = sections.Where(section => section.Entries.Count > 0).ToList()
        };
    }

    private static RecordDetailSection Section(string title, IEnumerable<RecordDetailEntry> entries)
    {
        return new RecordDetailSection
        {
            Title = title,
            Entries = entries.Where(entry =>
                    !string.IsNullOrEmpty(entry.Value) || entry.Kind == RecordDetailEntryKind.List)
                .ToList()
        };
    }

    private static RecordDetailSection ListSection(string title, List<RecordDetailListItem>? items)
    {
        items ??= [];
        return new RecordDetailSection
        {
            Title = title,
            Entries = items.Count == 0
                ? []
                :
                [
                    new RecordDetailEntry
                    {
                        Kind = RecordDetailEntryKind.List,
                        Label = title,
                        Items = items,
                        ExpandByDefault = items.Count <= 8
                    }
                ]
        };
    }

    private static RecordDetailEntry Scalar(string label, string? value)
    {
        return new RecordDetailEntry
        {
            Kind = RecordDetailEntryKind.Scalar,
            Label = label,
            Value = value
        };
    }

    private static RecordDetailEntry Link(string label, uint? formId, FormIdResolver resolver)
    {
        return new RecordDetailEntry
        {
            Kind = formId.HasValue ? RecordDetailEntryKind.Link : RecordDetailEntryKind.Scalar,
            Label = label,
            Value = formId.HasValue ? resolver.FormatWithEditorId(formId.Value) : null,
            LinkedFormId = formId
        };
    }

    private static RecordDetailListItem ListLinkItem(uint formId, FormIdResolver resolver)
    {
        return new RecordDetailListItem
        {
            Label = resolver.GetBestNameWithRefChain(formId) ?? $"0x{formId:X8}",
            Value = $"0x{formId:X8}",
            LinkedFormId = formId
        };
    }

    private static List<RecordDetailListItem> BuildStatItems(string[] names, string[]? values)
    {
        if (values == null)
        {
            return [];
        }

        return names.Zip(values, (name, value) => new RecordDetailListItem
        {
            Label = name,
            Value = value
        }).ToList();
    }

    private static List<RecordDetailListItem> BuildSkillItems(byte[]? skills, FormIdResolver resolver)
    {
        if (skills == null || skills.Length == 0)
        {
            return [];
        }

        var hasBigGuns = resolver.SkillEra?.BigGunsActive ?? false;
        var items = new List<RecordDetailListItem>();
        for (var i = 0; i < skills.Length && i < 14; i++)
        {
            if (i == 1 && !hasBigGuns)
            {
                continue;
            }

            items.Add(new RecordDetailListItem
            {
                Label = resolver.GetSkillName(i) ?? $"Skill#{i}",
                Value = skills[i].ToString()
            });
        }

        return items;
    }

    private static string? FormatBounds(ObjectBounds? bounds)
    {
        if (bounds == null)
        {
            return null;
        }

        return $"({bounds.X1}, {bounds.Y1}, {bounds.Z1}) -> ({bounds.X2}, {bounds.Y2}, {bounds.Z2})";
    }

    private static string? FormatPackageLocation(PackageLocation? location, FormIdResolver resolver)
    {
        if (location == null)
        {
            return null;
        }

        var union = location.Union != 0
            ? resolver.GetBestNameWithRefChain(location.Union) ?? $"0x{location.Union:X8}"
            : "(none)";
        return $"Type {location.Type}, {union}, radius {location.Radius}";
    }

    private static string? FormatPackageTarget(PackageTarget? target, FormIdResolver resolver)
    {
        if (target == null)
        {
            return null;
        }

        var targetValue = target.FormIdOrType != 0
            ? resolver.GetBestNameWithRefChain(target.FormIdOrType) ?? $"0x{target.FormIdOrType:X8}"
            : "(none)";
        return $"{target.TypeName}: {targetValue}, count {target.CountDistance}, radius {target.AcquireRadius:F1}";
    }

    private static string? FormatVolley(PackageUseWeaponData? useWeaponData)
    {
        if (useWeaponData == null)
        {
            return null;
        }

        return $"{useWeaponData.VolleyShotsMin}-{useWeaponData.VolleyShotsMax} shots, " +
               $"{useWeaponData.VolleyWaitMin:F1}-{useWeaponData.VolleyWaitMax:F1}s";
    }

    private static string? FormatCellCorner(short? x, short? y)
    {
        return x.HasValue && y.HasValue ? $"({x}, {y})" : null;
    }

    private static string? FormatWorldBounds(WorldspaceRecord worldspace)
    {
        if (!worldspace.BoundsMinX.HasValue || !worldspace.BoundsMinY.HasValue ||
            !worldspace.BoundsMaxX.HasValue || !worldspace.BoundsMaxY.HasValue)
        {
            return null;
        }

        return $"({worldspace.BoundsMinX:F1}, {worldspace.BoundsMinY:F1}) -> " +
               $"({worldspace.BoundsMaxX:F1}, {worldspace.BoundsMaxY:F1})";
    }

    private static string? FormatMapOffset(WorldspaceRecord worldspace)
    {
        if (!worldspace.MapOffsetScaleX.HasValue && !worldspace.MapOffsetScaleY.HasValue &&
            !worldspace.MapOffsetZ.HasValue)
        {
            return null;
        }

        return $"X {worldspace.MapOffsetScaleX?.ToString("F2") ?? "?"}, " +
               $"Y {worldspace.MapOffsetScaleY?.ToString("F2") ?? "?"}, " +
               $"Z {worldspace.MapOffsetZ?.ToString("F2") ?? "?"}";
    }

    private static string? BoolText(bool? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return value.Value ? "Yes" : "No";
    }
}
