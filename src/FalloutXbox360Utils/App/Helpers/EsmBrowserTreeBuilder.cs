using System.Collections;
using System.Collections.ObjectModel;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Presentation;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils;

/// <summary>
///     Builds a hierarchical tree of ESM records for the data browser,
///     grouping records by category and type (inspired by TES5Edit/xEdit).
/// </summary>
internal static class EsmBrowserTreeBuilder
{
    public static ObservableCollection<EsmBrowserNode> BuildTree(
        RecordCollection result,
        FormIdResolver? _resolver = null)
    {
        var root = new ObservableCollection<EsmBrowserNode>();

        AddCategory(root, "Characters", "\uE77B", [
            ("NPCs", result.Npcs),
            ("Creatures", result.Creatures),
            ("Races", result.Races),
            ("Factions", result.Factions),
            ("Classes", result.Classes),
            ("Body Part Data", result.BodyPartData),
            ("Actor Value Info", result.ActorValueInfos)
        ]);

        AddCategory(root, "AI", "\uE8AB", [
            ("AI Packages", result.Packages),
            ("Combat Styles", result.CombatStyles)
        ]);

        // Dialog Topics and Dialogues are in the Dialogue Viewer tab
        AddCategory(root, "Quests & Dialogue", "\uE8BD", [
            ("Quests", result.Quests),
            ("Notes", result.Notes),
            ("Books", result.Books),
            ("Terminals", result.Terminals),
            ("Messages", result.Messages),
            ("Scripts", result.Scripts)
        ]);

        AddCategory(root, "Items", "\uE7BF", [
            ("Weapons", result.Weapons),
            ("Armor", result.Armor),
            ("Armor Addons", result.ArmorAddons),
            ("Ammo", result.Ammo),
            ("Consumables", result.Consumables),
            ("Misc Items", result.MiscItems),
            ("Keys", result.Keys),
            ("Containers", result.Containers)
        ]);

        AddCategory(root, "Abilities", "\uE945", [
            ("Perks", result.Perks),
            ("Spells", result.Spells),
            ("Enchantments", result.Enchantments),
            ("Base Effects", result.BaseEffects)
        ]);

        // World objects (base definitions only - spatial visualization is in the World Map tab)
        AddCategory(root, "World Objects", "\uE774", [
            ("Worldspaces", result.Worldspaces),
            ("Leveled Lists", result.LeveledLists),
            ("Activators", result.Activators),
            ("Lights", result.Lights),
            ("Doors", result.Doors),
            ("Statics", result.Statics),
            ("Furniture", result.Furniture),
            ("Water", result.Water),
            ("Weather", result.Weather),
            ("Nav Meshes", result.NavMeshes),
            ("Lighting Templates", result.LightingTemplates)
        ]);

        AddCategory(root, "Game Data", "\uE8F1", [
            ("Game Settings", result.GameSettings),
            ("Globals", result.Globals),
            ("Form Lists", result.FormLists),
            ("Weapon Mods", result.WeaponMods),
            ("Recipes", result.Recipes),
            ("Challenges", result.Challenges),
            ("Reputations", result.Reputations),
            ("Projectiles", result.Projectiles),
            ("Explosions", result.Explosions)
        ]);

        // Add generic records grouped by type + specialized Phase 2 records into categories
        var byType = result.GenericRecords.Count > 0
            ? result.GenericRecords
                .GroupBy(r => r.RecordType)
                .ToDictionary(g => g.Key, g => (IList)g.ToList())
            : new Dictionary<string, IList>();

        var graphicsSubs = new List<(string Name, IList Records)>();
        if (result.TextureSets.Count > 0) graphicsSubs.Add(("Texture Sets", result.TextureSets));
        graphicsSubs.AddRange(BuildGenericSubcategories(byType,
            ("Camera Shots", "CAMS"),
            ("Effect Shaders", "EFSH"),
            ("Image Space Modifiers", "IMAD")));
        AddCategory(root, "Graphics", "\uE790", graphicsSubs.ToArray());

        var audioSubs = new List<(string Name, IList Records)>();
        if (result.Sounds.Count > 0) audioSubs.Add(("Sounds", result.Sounds));
        if (result.MusicTypes.Count > 0) audioSubs.Add(("Music Types", result.MusicTypes));
        audioSubs.AddRange(BuildGenericSubcategories(byType,
            ("Acoustic Spaces", "ASPC"),
            ("Media Sets", "MSET")));
        AddCategory(root, "Audio", "\uE767", audioSubs.ToArray());

        AddCategory(root, "Misc Data", "\uE71D", BuildGenericSubcategories(byType,
            ("Movable Statics", "MSTT"),
            ("Talking Activators", "TACT"),
            ("Trees", "TREE"),
            ("Addon Nodes", "ADDN"),
            ("Animated Objects", "ANIO"),
            ("Impact Data Sets", "IPDS"),
            ("Ragdolls", "RGDL"),
            ("Load Screens", "LSCR"),
            ("Casino Chips", "CHIP"),
            ("Casinos", "CSNO"),
            ("Default Objects", "DOBJ")));

        return root;
    }

    /// <summary>
    ///     Builds subcategory tuples for generic records, filtering to types that have instances.
    /// </summary>
    private static (string Name, IList Records)[] BuildGenericSubcategories(
        Dictionary<string, IList> byType,
        params (string DisplayName, string RecordType)[] mappings)
    {
        var result = new List<(string Name, IList Records)>();
        foreach (var (displayName, recordType) in mappings)
        {
            if (byType.TryGetValue(recordType, out var records) && records.Count > 0)
            {
                result.Add((displayName, records));
            }
        }

        return result.ToArray();
    }

    private static void AddCategory(
        ObservableCollection<EsmBrowserNode> root,
        string categoryName,
        string iconGlyph,
        (string Name, IList Records)[] recordTypes)
    {
        var totalCount = recordTypes.Sum(rt => rt.Records.Count);
        if (totalCount == 0)
        {
            return;
        }

        var categoryNode = new EsmBrowserNode
        {
            DisplayName = $"{categoryName} ({totalCount:N0})",
            NodeType = "Category",
            IconGlyph = iconGlyph,
            HasUnrealizedChildren = true,
            DataObject = recordTypes
        };

        root.Add(categoryNode);
    }

    /// <summary>
    ///     Populates children for a category node (record type sub-nodes).
    /// </summary>
    public static void LoadCategoryChildren(EsmBrowserNode categoryNode)
    {
        if (categoryNode.DataObject is not (string Name, IList Records)[] recordTypes)
        {
            return;
        }

        foreach (var (name, records) in recordTypes)
        {
            if (records.Count == 0)
            {
                continue;
            }

            var typeIcon = EsmPropertyFormatter.SubCategoryIcons.GetValueOrDefault(name, categoryNode.IconGlyph);
            var typeNode = new EsmBrowserNode
            {
                DisplayName = $"{name} ({records.Count:N0})",
                NodeType = "RecordType",
                IconGlyph = typeIcon,
                ParentIconGlyph = typeIcon,
                ParentTypeName = name,
                HasUnrealizedChildren = true,
                DataObject = records
            };

            categoryNode.Children.Add(typeNode);
        }

        categoryNode.HasUnrealizedChildren = false;
    }

    /// <summary>
    ///     Populates children for a record type node (individual records).
    /// </summary>
    public static void LoadRecordTypeChildren(
        EsmBrowserNode typeNode,
        RecordCollection? allRecords = null,
        FormIdResolver? resolver = null,
        Dictionary<uint, List<WorldPlacement>>? placementIndex = null,
        FormUsageIndex? usageIndex = null,
        IReadOnlyDictionary<uint, RaceRecord>? raceLookup = null,
        Dictionary<uint, List<(uint FormId, string? Name)>>? factionMembersIndex = null)
    {
        if (typeNode.DataObject is not IList records)
        {
            return;
        }

        // Build all nodes first (outside of lock for better performance)
        var recordNodes = new List<EsmBrowserNode>(records.Count);

        foreach (var record in records)
        {
            var (formId, editorId, fullName, offset) = EsmPropertyFormatter.ExtractRecordIdentity(record);
            var formIdHex = $"0x{formId:X8}";

            // Look up Count/Use data
            var placementCount = 0;
            if (placementIndex != null && formId != 0 &&
                placementIndex.TryGetValue(formId, out var placements))
            {
                placementCount = placements.Count;
            }

            var useCount = usageIndex?.GetUseCount(formId) ?? 0;

            // Build display name and detail (shown as secondary text in tree)
            string displayName;
            string? detail;

            if (record is DialogueRecord dialogue)
            {
                // Dialogue records: show response text with quest/topic context
                displayName = EsmPropertyFormatter.BuildDialogueDisplayName(dialogue, formIdHex, resolver);
                detail = EsmPropertyFormatter.BuildDialogueDetail(dialogue, formIdHex, resolver);
            }
            else if (record is PackageRecord pkg)
            {
                // AI Packages: show EditorId with type name in parens
                displayName = pkg.EditorId ?? formIdHex;
                detail = $"({pkg.TypeName})";
            }
            else if (!string.IsNullOrEmpty(fullName) && !string.IsNullOrEmpty(editorId))
            {
                displayName = fullName;
                detail = $"({editorId})";
            }
            else if (!string.IsNullOrEmpty(fullName))
            {
                displayName = fullName;
                detail = formIdHex;
            }
            else if (!string.IsNullOrEmpty(editorId))
            {
                displayName = editorId;
                detail = formIdHex;
            }
            else
            {
                displayName = formIdHex;
                detail = null;
            }

            // Append Count/Use summary to detail text
            if (placementCount > 0 || useCount > 0)
            {
                var countParts = new List<string>();
                if (placementCount > 0)
                {
                    countParts.Add($"Count {placementCount}");
                }

                if (useCount > 0)
                {
                    countParts.Add($"Use {useCount}");
                }

                var summary = $"[{string.Join(", ", countParts)}]";
                detail = detail != null
                    ? $"{detail} {summary}"
                    : summary;
            }

            var recordNode = new EsmBrowserNode
            {
                DisplayName = displayName,
                Detail = detail,
                FormIdHex = formIdHex,
                EditorId = editorId,
                NodeType = "Record",
                IconGlyph = typeNode.IconGlyph,
                ParentTypeName = typeNode.ParentTypeName,
                ParentIconGlyph = typeNode.IconGlyph,
                FileOffset = offset,
                DataObject = record,
                Properties = BuildProperties(record, allRecords, resolver, placementIndex, usageIndex, raceLookup,
                    factionMembersIndex)
            };

            recordNodes.Add(recordNode);
        }

        // Sort all nodes
        var sorted = recordNodes.OrderBy(n => n.DisplayName ?? "", StringComparer.OrdinalIgnoreCase).ToList();

        // Add all sorted nodes under a single lock to prevent concurrent modification
        lock (typeNode.Children)
        {
            typeNode.Children.Clear();
            foreach (var node in sorted)
            {
                typeNode.Children.Add(node);
            }
        }

        typeNode.HasUnrealizedChildren = false;
    }

    /// <summary>
    ///     Re-sorts children of all record type nodes based on the selected sort mode.
    /// </summary>
    public static void SortRecordChildren(ObservableCollection<EsmBrowserNode> root, RecordSortMode mode)
    {
#pragma warning disable S3267 // Loop body has lock/continue that makes LINQ impractical
        foreach (var typeNode in root.SelectMany(c => c.Children))
#pragma warning restore S3267
        {
            // Take a snapshot to avoid concurrent modification issues
            EsmBrowserNode[] snapshot;
            lock (typeNode.Children)
            {
                if (typeNode.Children.Count == 0)
                {
                    continue;
                }

                snapshot = typeNode.Children.ToArray();
            }

            var sorted = mode switch
            {
                RecordSortMode.EditorId => snapshot
                    .OrderBy(n => n.EditorId ?? n.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
                RecordSortMode.FormId => snapshot
                    .OrderBy(n => n.FormIdHex ?? "", StringComparer.OrdinalIgnoreCase).ToList(),
                _ => snapshot
                    .OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase).ToList()
            };

            lock (typeNode.Children)
            {
                typeNode.Children.Clear();
                foreach (var node in sorted)
                {
                    typeNode.Children.Add(node);
                }
            }
        }
    }

    /// <summary>
    ///     Builds a property list from a record's public properties for the detail panel.
    /// </summary>
    public static List<EsmPropertyEntry> BuildProperties(
        object record,
        RecordCollection? allRecords = null,
        FormIdResolver? resolver = null,
        Dictionary<uint, List<WorldPlacement>>? placementIndex = null,
        FormUsageIndex? usageIndex = null,
        IReadOnlyDictionary<uint, RaceRecord>? raceLookup = null,
        Dictionary<uint, List<(uint FormId, string? Name)>>? factionMembersIndex = null)
    {
        if (resolver != null &&
            RecordDetailPresenter.TryBuildForRecord(record, allRecords, resolver, out var detailModel) &&
            detailModel != null)
        {
            return RecordDetailPropertyAdapter.Convert(detailModel);
        }

        var properties = new List<EsmPropertyEntry>();
        var type = record.GetType();
        var (recordFormId, _, _, _) = EsmPropertyFormatter.ExtractRecordIdentity(record);

        // Add record type signature as the first Identity property
        if (record is GenericEsmRecord generic)
        {
            properties.Add(new EsmPropertyEntry
            {
                Name = "Record Type",
                Value = generic.RecordType,
                Category = "Identity"
            });
        }
        else if (EsmPropertyFormatter.RecordTypeSignatures.TryGetValue(type, out var signature))
        {
            properties.Add(new EsmPropertyEntry
            {
                Name = "Record Type",
                Value = signature,
                Category = "Identity"
            });
        }

        AddCountAndUse(properties, recordFormId, placementIndex, usageIndex);

        // Record-type flags for special handling
        var isCreature = record is CreatureRecord;
        var isPackage = record is PackageRecord;
        var scriptRecord = record as ScriptRecord;

        foreach (var prop in EsmPropertyFormatter.GetCachedProperties(type))
        {
            if (!prop.CanRead)
            {
                continue;
            }

            // Skip creature-specific properties in default loop - handled explicitly below
            if (isCreature && prop.Name is "CreatureType" or "CreatureTypeName"
                    or "CombatSkill" or "MagicSkill" or "StealthSkill" or "AttackDamage")
            {
                continue;
            }

            // Skip package-specific properties - handled in custom block below
            if (isPackage && prop.Name is "Data" or "Schedule" or "Location" or "Location2"
                    or "Target" or "Target2" or "TypeName")
            {
                continue;
            }

            var value = prop.GetValue(record);

            // Skip WaterHeight when HasWater is false or value is a sentinel
            if (record is CellRecord cellRec && prop.Name == "WaterHeight"
                                             && (!cellRec.HasWater || (value is float wh &&
                                                                       (wh < -1_000_000f || wh > 1_000_000f))))
            {
                continue;
            }

            var displayName = EsmPropertyFormatter.FormatPropertyName(prop.Name);

            // Delegate to character property builder for actor/NPC-specific subrecords
            if (value is ActorBaseSubrecord stats)
            {
                EsmCharacterPropertyBuilder.AddActorBaseStats(properties, stats, record is NpcRecord);
                continue;
            }

            if (value is NpcAiData ai)
            {
                EsmCharacterPropertyBuilder.AddAiData(properties, ai);
                continue;
            }

            if (prop.Name == "SpecialStats" && value is byte[] special && special.Length == 7)
            {
                EsmCharacterPropertyBuilder.AddSpecialStats(properties, special);
                continue;
            }

            if (prop.Name == "Skills" && value is byte[] skills && skills.Length >= 13)
            {
                EsmCharacterPropertyBuilder.AddSkills(properties, skills, resolver);
                continue;
            }

            if (prop.Name.StartsWith("FaceGen", StringComparison.Ordinal) && value is float[] morphs &&
                morphs.Length > 0)
            {
                EsmCharacterPropertyBuilder.AddFaceGenMorphs(
                    properties, prop.Name, displayName, morphs, record, raceLookup);
                continue;
            }

            if (prop.Name == "TagSkills" && value is int[] tagSkillIndices)
            {
                EsmCharacterPropertyBuilder.AddTagSkills(properties, tagSkillIndices, resolver);
                continue;
            }

            if (prop.Name == "TrainingSkill" && value is byte trainingIdx)
            {
                EsmCharacterPropertyBuilder.AddTrainingSkill(properties, trainingIdx, resolver);
                continue;
            }

            if (prop.Name == "AttributeWeights" && value is byte[] attrWeights && attrWeights.Length == 7)
            {
                EsmCharacterPropertyBuilder.AddAttributeWeights(properties, attrWeights);
                continue;
            }

            // Delegate to item property builder for script text and compiled data
            if (prop.Name is "SourceText" or "DecompiledText" && value is string textContent &&
                !string.IsNullOrEmpty(textContent))
            {
                EsmItemPropertyBuilder.AddScriptText(properties, prop.Name, displayName, textContent, scriptRecord);
                continue;
            }

            if (prop.Name == "CompiledData" && value is byte[] compiledBytes && compiledBytes.Length > 0)
            {
                EsmItemPropertyBuilder.AddCompiledData(properties, displayName, prop.Name, compiledBytes);
                continue;
            }

            if (prop.Name == "RequiredSkill" && value is int requiredSkillAv)
            {
                EsmItemPropertyBuilder.AddRequiredSkill(properties, requiredSkillAv, resolver);
                continue;
            }

            // Skip Cells list for WorldspaceRecord (shown as summary instead)
            if (record is WorldspaceRecord && prop.Name == "Cells")
            {
                continue;
            }

            // Skip complex sub-objects on PlacedReference that don't render well as flat properties
            if (record is PlacedReference && prop.Name is "Bounds" or "StartingPosition" or "PackageStartLocation")
            {
                continue;
            }

            // Handle flags, fields dict, lists, FormID refs, and default values
            EsmPropertyFormatter.TryAddCommonProperty(properties, prop, value, displayName, type, resolver);
        }

        // Type-specific property blocks
        EsmCharacterPropertyBuilder.AddCreatureProperties(properties, record);
        EsmWorldPropertyBuilder.AddPackageProperties(properties, record, resolver);
        EsmCharacterPropertyBuilder.AddNpcDerivedStats(properties, record);
        EsmCharacterPropertyBuilder.AddRaceSkillBoosts(properties, record, resolver);
        EsmWorldPropertyBuilder.AddWorldspaceStats(properties, record);
        EsmWorldPropertyBuilder.AddWorldPlacements(properties, record, type, placementIndex);
        AddUsageReferences(properties, recordFormId, usageIndex, resolver);
        AddFactionMembers(properties, record, factionMembersIndex);

        // Sort properties by category for consistent grouping
        return properties
            .OrderBy(p => Array.IndexOf(EsmPropertyFormatter.CategoryOrder, p.Category ?? "General"))
            .ThenBy(p => p.Category == "General" ? 1 : 0)
            .ToList();
    }

    private static void AddCountAndUse(
        List<EsmPropertyEntry> properties,
        uint recordFormId,
        Dictionary<uint, List<WorldPlacement>>? placementIndex,
        FormUsageIndex? usageIndex)
    {
        if (recordFormId == 0)
        {
            return;
        }

        var placementCount = placementIndex != null && placementIndex.TryGetValue(recordFormId, out var placements)
            ? placements.Count
            : 0;
        var useCount = usageIndex?.GetUseCount(recordFormId) ?? 0;

        if (placementCount > 0)
        {
            properties.Add(new EsmPropertyEntry
            {
                Name = "Count",
                Value = placementCount.ToString("N0"),
                Category = "Statistics"
            });
        }

        if (useCount > 0)
        {
            properties.Add(new EsmPropertyEntry
            {
                Name = "Use",
                Value = useCount.ToString("N0"),
                Category = "Statistics"
            });
        }
    }

    private static void AddUsageReferences(
        List<EsmPropertyEntry> properties,
        uint recordFormId,
        FormUsageIndex? usageIndex,
        FormIdResolver? resolver)
    {
        if (recordFormId == 0 || usageIndex == null)
        {
            return;
        }

        var usages = usageIndex.GetUsages(recordFormId);
        if (usages.Count == 0)
        {
            return;
        }

        var subItems = usages
            .OrderBy(u => ResolveUsageSourceName(u, resolver), StringComparer.OrdinalIgnoreCase)
            .ThenBy(u => u.Context, StringComparer.OrdinalIgnoreCase)
            .Select(u =>
            {
                var sourceName = ResolveUsageSourceName(u, resolver);
                return new EsmPropertyEntry
                {
                    Col1 = sourceName,
                    Col2 = u.Context,
                    Col3 = $"0x{u.SourceFormId:X8}",
                    Col3FormId = u.SourceFormId,
                    Col4 = u.SourceKind
                };
            })
            .ToList();

        properties.Add(new EsmPropertyEntry
        {
            Name = $"Used By ({usages.Count})",
            Value = "",
            Category = "References",
            IsExpandable = true,
            SubItems = subItems
        });
    }

    /// <summary>
    ///     Resolves the display name for a usage reference source.
    ///     Dialog topics and dialogue result scripts prefer EditorId over FullName
    ///     to avoid showing long dialogue line text.
    /// </summary>
    private static string ResolveUsageSourceName(FormUsageReference u, FormIdResolver? resolver)
    {
        if (resolver != null && u.SourceKind is "Dialog Topic" or "Dialogue")
        {
            return resolver.EditorIds.GetValueOrDefault(u.SourceFormId)
                   ?? resolver.GetBestNameWithRefChain(u.SourceFormId)
                   ?? $"0x{u.SourceFormId:X8}";
        }

        return resolver?.GetBestNameWithRefChain(u.SourceFormId) ?? $"0x{u.SourceFormId:X8}";
    }

    /// <summary>
    ///     Adds faction member list for FactionRecord instances.
    /// </summary>
    private static void AddFactionMembers(
        List<EsmPropertyEntry> properties,
        object record,
        Dictionary<uint, List<(uint FormId, string? Name)>>? factionMembersIndex)
    {
        if (record is not FactionRecord faction || factionMembersIndex == null)
        {
            return;
        }

        if (!factionMembersIndex.TryGetValue(faction.FormId, out var members) || members.Count == 0)
        {
            return;
        }

        var subItems = members
            .OrderBy(m => m.Name ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(m => new EsmPropertyEntry
            {
                Name = m.Name ?? $"0x{m.FormId:X8}",
                Value = $"0x{m.FormId:X8}",
                LinkedFormId = m.FormId
            })
            .ToList();

        properties.Add(new EsmPropertyEntry
        {
            Name = $"Members ({members.Count} NPCs)",
            Value = "",
            Category = "References",
            IsExpandable = true,
            SubItems = subItems
        });
    }

    /// <summary>
    ///     Sort modes for record children within a record type node.
    /// </summary>
    public enum RecordSortMode
    {
        Name,
        EditorId,
        FormId
    }
}
