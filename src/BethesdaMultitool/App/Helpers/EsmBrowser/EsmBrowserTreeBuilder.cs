using System.Collections;
using System.Collections.ObjectModel;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Presentation;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Recovery;

namespace BethesdaMultitool;

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

        // Generic (un-typed) records grouped by 4-char record type. Games whose records have no
        // dedicated typed list \u2014 Morrowind/TES3 \u2014 land here; the Pick() helper below routes them into
        // the same categories as FNV/Skyrim typed records (consuming the type so the "Other Records"
        // catch-all at the end only shows what's left).
        var byType = result.GenericRecords.Count > 0
            ? result.GenericRecords
                .GroupBy(r => r.RecordType, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IList)g.ToList(), StringComparer.Ordinal)
            : new Dictionary<string, IList>(StringComparer.Ordinal);

        IList Pick(IList typed, params string[] types)
        {
            if (typed.Count > 0)
            {
                return typed;
            }

            var combined = new List<GenericEsmRecord>();
            foreach (var t in types)
            {
                if (byType.TryGetValue(t, out var recs))
                {
                    combined.AddRange(recs.Cast<GenericEsmRecord>());
                    byType.Remove(t);
                }
            }

            return combined;
        }

        AddCategory(root, "Characters", "\uE77B", [
            ("NPCs", Pick(result.Npcs, "NPC_")),
            ("Creatures", Pick(result.Creatures, "CREA")),
            ("Races", Pick(result.Races, "RACE")),
            ("Factions", Pick(result.Factions, "FACT")),
            ("Classes", Pick(result.Classes, "CLAS")),
            ("Birthsigns", Pick(Array.Empty<GenericEsmRecord>(), "BSGN")),
            ("Skills", Pick(Array.Empty<GenericEsmRecord>(), "SKIL")),
            ("Body Parts", Pick(result.BodyPartData, "BODY")),
            ("Actor Value Info", result.ActorValueInfos)
        ]);

        AddCategory(root, "AI", "\uE8AB", [
            ("AI Packages", result.Packages),
            ("Combat Styles", result.CombatStyles)
        ]);

        // Dialog topics and dialogue lines are in the Dialogue tab.
        AddCategory(root, "Quests & Dialogue", "\uE8BD", [
            ("Quests", result.Quests),
            ("Notes", result.Notes),
            ("Books", Pick(result.Books, "BOOK")),
            ("Terminals", result.Terminals),
            ("Messages", result.Messages),
            ("Scripts", Pick(result.Scripts, "SCPT"))
        ]);

        AddCategory(root, "Items", "\uE7BF", [
            ("Weapons", Pick(result.Weapons, "WEAP")),
            ("Armor", Pick(result.Armor, "ARMO")),
            ("Clothing", Pick(Array.Empty<GenericEsmRecord>(), "CLOT")),
            ("Armor Addons", result.ArmorAddons),
            ("Ammo", result.Ammo),
            ("Consumables", result.Consumables),
            ("Potions", Pick(Array.Empty<GenericEsmRecord>(), "ALCH")),
            ("Ingredients", Pick(result.Ingredients, "INGR")),
            ("Apparatus", Pick(Array.Empty<GenericEsmRecord>(), "APPA")),
            ("Tools", Pick(Array.Empty<GenericEsmRecord>(), "REPA", "PROB", "LOCK")),
            ("Misc Items", Pick(result.MiscItems, "MISC")),
            ("Keys", result.Keys),
            ("Containers", Pick(result.Containers, "CONT"))
        ]);

        AddCategory(root, "Abilities", "\uE945", [
            ("Perks", result.Perks),
            ("Spells", Pick(result.Spells, "SPEL")),
            ("Enchantments", Pick(result.Enchantments, "ENCH")),
            ("Magic Effects", Pick(Array.Empty<GenericEsmRecord>(), "MGEF")),
            ("Base Effects", result.BaseEffects)
        ]);

        // World objects (base definitions only - spatial visualization is in the World Map tab)
        AddCategory(root, "World Objects", "\uE774", [
            ("Worldspaces", result.Worldspaces),
            ("Leveled Lists", Pick(result.LeveledLists, "LEVI", "LEVC")),
            ("Activators", Pick(result.Activators, "ACTI")),
            ("Lights", Pick(result.Lights, "LIGH")),
            ("Doors", Pick(result.Doors, "DOOR")),
            ("Statics", Pick(result.Statics, "STAT")),
            ("Furniture", result.Furniture),
            ("Land Textures", Pick(result.LandTextures, "LTEX")),
            ("Regions", Pick(result.Regions, "REGN")),
            ("Water", result.Water),
            ("Weather", result.Weather),
            ("Nav Meshes", result.NavMeshes),
            ("Lighting Templates", result.LightingTemplates)
        ]);

        AddCategory(root, "Game Data", "\uE8F1", [
            ("Game Settings", Pick(result.GameSettings, "GMST")),
            ("Globals", Pick(result.Globals, "GLOB")),
            ("Form Lists", result.FormLists),
            ("Weapon Mods", result.WeaponMods),
            ("Recipes", result.Recipes),
            ("Challenges", result.Challenges),
            ("Reputations", result.Reputations),
            ("Projectiles", result.Projectiles),
            ("Explosions", result.Explosions)
        ]);

        // Graphics + other specialized records (byType built at the top; Pick() above has already
        // consumed the types it routed into named categories).
        var graphicsSubs = new List<(string Name, IList Records)>();
        if (result.TextureSets.Count > 0) graphicsSubs.Add(("Texture Sets", result.TextureSets));
        graphicsSubs.AddRange(BuildGenericSubcategories(byType,
            ("Camera Shots", "CAMS"),
            ("Effect Shaders", "EFSH"),
            ("Image Space Modifiers", "IMAD")));
        AddCategory(root, "Graphics", "\uE790", graphicsSubs.ToArray());

        var audioSubs = new List<(string Name, IList Records)>();
        var sounds = Pick(result.Sounds, "SOUN");
        if (sounds.Count > 0) audioSubs.Add(("Sounds", sounds));
        if (result.MusicTypes.Count > 0) audioSubs.Add(("Music Types", result.MusicTypes));
        audioSubs.AddRange(BuildGenericSubcategories(byType,
            ("Sound Generators", "SNDG"),
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

        // Any remaining generic record types not mapped to a named subcategory above — surface them
        // by type so nothing is hidden (every Morrowind/TES3 record type lands here, decoded but
        // without a dedicated typed list). Sorted by count, descending.
        if (byType.Count > 0)
        {
            var otherSubs = byType
                .Where(kv => kv.Value.Count > 0)
                .OrderByDescending(kv => kv.Value.Count)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => (kv.Key, kv.Value))
                .ToArray();
            if (otherSubs.Length > 0)
            {
                AddCategory(root, "Other Records", "", otherSubs);
            }
        }

        return root;
    }

    public static void AppendRecoverableGapCategory(
        ObservableCollection<EsmBrowserNode> root,
        IReadOnlyList<DmpGapRecoveryCandidate>? candidates)
    {
        if (candidates is not { Count: > 0 })
        {
            return;
        }

        var recordTypes = candidates
            .GroupBy(c => c.DisplayFamily)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Name: g.Key, Records: (IList)g
                .OrderBy(c => c.FileOffset)
                .ToList()))
            .ToArray();

        AddCategory(root, "Recoverable Gaps", "\uE9D9", recordTypes);
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

            // Consume the type so the "Other Records" catch-all doesn't list it again.
            byType.Remove(recordType);
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

        // The UI thread (tree expansion) and the background pre-load task can both reach here for
        // the same node. Skip if already populated, build the type nodes lock-free into a local
        // list, then publish them under the collection's lock with a first-writer-wins re-check — so
        // a concurrent reader of categoryNode.Children never observes a partially-filled collection
        // (a torn read of an ObservableCollection's backing array is a GC hole / ExecutionEngineException).
        lock (categoryNode.Children)
        {
            if (categoryNode.Children.Count > 0)
            {
                return;
            }
        }

        var typeNodes = new List<EsmBrowserNode>(recordTypes.Length);
        foreach (var (name, records) in recordTypes)
        {
            if (records.Count == 0)
            {
                continue;
            }

            var typeIcon = EsmPropertyFormatter.SubCategoryIcons.GetValueOrDefault(name, categoryNode.IconGlyph);
            typeNodes.Add(new EsmBrowserNode
            {
                DisplayName = $"{name} ({records.Count:N0})",
                NodeType = "RecordType",
                IconGlyph = typeIcon,
                ParentIconGlyph = typeIcon,
                ParentTypeName = name,
                HasUnrealizedChildren = true,
                DataObject = records
            });
        }

        lock (categoryNode.Children)
        {
            if (categoryNode.Children.Count > 0)
            {
                return;
            }

            foreach (var typeNode in typeNodes)
            {
                categoryNode.Children.Add(typeNode);
            }

            categoryNode.HasUnrealizedChildren = false;
        }
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

        // Already populated (the UI thread or the background pre-load task may have won the race) —
        // skip the expensive node build entirely.
        lock (typeNode.Children)
        {
            if (typeNode.Children.Count > 0)
            {
                return;
            }
        }

        // Build all nodes first (outside of lock for better performance)
        var recordNodes = new List<EsmBrowserNode>(records.Count);

        foreach (var record in records)
        {
            if (record is DmpGapRecoveryCandidate candidate)
            {
                recordNodes.Add(BuildRecoverableGapNode(candidate, typeNode));
                continue;
            }

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
                // Defer the property build to first selection — see EsmBrowserNode.PropertyFactory.
                // Expanding a large type node (e.g. a DMP's tens-of-thousands of records) otherwise
                // builds ~80 EsmPropertyEntry per record up front for nodes the user never selects.
                PropertyFactory = () => BuildProperties(record, allRecords, resolver, placementIndex, usageIndex,
                    raceLookup, factionMembersIndex)
            };

            recordNodes.Add(recordNode);
        }

        // Sort all nodes
        var sorted = recordNodes.OrderBy(n => n.DisplayName ?? "", StringComparer.OrdinalIgnoreCase).ToList();

        // Publish under the collection's lock, first-writer-wins (no Clear): a concurrent reader of
        // typeNode.Children must never see it emptied or mid-resize — that torn read is a GC hole.
        lock (typeNode.Children)
        {
            if (typeNode.Children.Count > 0)
            {
                return;
            }

            foreach (var node in sorted)
            {
                typeNode.Children.Add(node);
            }

            typeNode.HasUnrealizedChildren = false;
        }
    }

    private static EsmBrowserNode BuildRecoverableGapNode(
        DmpGapRecoveryCandidate candidate,
        EsmBrowserNode typeNode)
    {
        var formIdHex = candidate.FormId.HasValue
            ? $"0x{candidate.FormId.Value:X8}"
            : null;
        var address = candidate.VirtualAddress.HasValue
            ? $"VA 0x{candidate.VirtualAddress.Value:X8}"
            : $"file 0x{candidate.FileOffset:X8}";
        var displayName = candidate.RecordType != null
            ? $"{candidate.RecordType} {formIdHex ?? ""} @ 0x{candidate.FileOffset:X8}".Trim()
            : $"{candidate.ClassName ?? candidate.Kind.ToString()} @ 0x{candidate.FileOffset:X8}";

        return new EsmBrowserNode
        {
            DisplayName = displayName,
            Detail = $"{candidate.Disposition}; confidence {candidate.Confidence}; {address}",
            FormIdHex = formIdHex,
            NodeType = "Record",
            IconGlyph = typeNode.IconGlyph,
            ParentTypeName = typeNode.ParentTypeName,
            ParentIconGlyph = typeNode.IconGlyph,
            FileOffset = candidate.FileOffset,
            DataObject = candidate,
            Properties = BuildRecoverableGapProperties(candidate)
        };
    }

    private static List<EsmPropertyEntry> BuildRecoverableGapProperties(DmpGapRecoveryCandidate candidate)
    {
        var properties = new List<EsmPropertyEntry>
        {
            Property("Kind", candidate.Kind.ToString(), "Identity"),
            Property("Family", candidate.DisplayFamily, "Identity"),
            Property("Disposition", candidate.Disposition.ToString(), "Recovery"),
            Property("Confidence", candidate.Confidence.ToString(), "Recovery"),
            Property("Evidence", candidate.Evidence, "Recovery"),
            Property("File Offset", $"0x{candidate.FileOffset:X8}", "Location"),
            Property("Length", $"{candidate.Length:N0} bytes", "Location"),
            Property("Gap Offset", $"0x{candidate.GapFileOffset:X8}", "Gap"),
            Property("Gap Size", $"{candidate.GapSize:N0} bytes", "Gap"),
            Property("Gap Classification", candidate.GapClassification, "Gap"),
            Property("Gap Context", candidate.GapContext, "Gap")
        };

        AddOptional(properties, "Record Type", candidate.RecordType, "Identity");
        AddOptional(properties, "Form Type", candidate.FormType.HasValue ? $"0x{candidate.FormType.Value:X2}" : null,
            "Identity");
        AddOptional(properties, "Class", candidate.ClassName, "Identity");
        if (candidate.FormId is { } formId)
        {
            properties.Add(new EsmPropertyEntry
            {
                Name = "Form ID",
                Value = $"0x{formId:X8}",
                Category = "Identity",
                LinkedFormId = formId
            });
        }

        AddOptional(properties, "Virtual Address",
            candidate.VirtualAddress.HasValue ? $"0x{candidate.VirtualAddress.Value:X8}" : null, "Location");
        AddOptional(properties, "Raw Data Size",
            candidate.RawDataSize.HasValue ? $"{candidate.RawDataSize.Value:N0} bytes" : null, "Raw Record");
        AddOptional(properties, "Raw Flags",
            candidate.RawFlags.HasValue ? $"0x{candidate.RawFlags.Value:X8}" : null, "Raw Record");
        if (candidate.Kind == DmpGapRecoveryCandidateKind.RawEsmRecord)
        {
            properties.Add(Property("Xbox/Reversed Header", candidate.IsBigEndian ? "Yes" : "No", "Raw Record"));
        }

        AddOptional(properties, "Decoded Text", candidate.DecodedText, "Dialogue");
        AddFormId(properties, "Topic", candidate.TopicFormId, "Dialogue");
        AddFormId(properties, "Quest", candidate.QuestFormId, "Dialogue");

        return properties
            .Where(p => !string.IsNullOrWhiteSpace(p.Value) || p.LinkedFormId.HasValue)
            .ToList();
    }

    private static EsmPropertyEntry Property(string name, string value, string category)
    {
        return new EsmPropertyEntry
        {
            Name = name,
            Value = value,
            Category = category
        };
    }

    private static void AddOptional(
        List<EsmPropertyEntry> properties,
        string name,
        string? value,
        string category)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            properties.Add(Property(name, value, category));
        }
    }

    private static void AddFormId(
        List<EsmPropertyEntry> properties,
        string name,
        uint? formId,
        string category)
    {
        if (formId is not > 0)
        {
            return;
        }

        properties.Add(new EsmPropertyEntry
        {
            Name = name,
            Value = $"0x{formId.Value:X8}",
            Category = category,
            LinkedFormId = formId.Value
        });
    }

    /// <summary>
    ///     Re-sorts children of all record type nodes based on the selected sort mode.
    /// </summary>
    public static void SortRecordChildren(ObservableCollection<EsmBrowserNode> root, RecordSortMode mode)
    {
        // Snapshot each category's Children under its lock before enumerating — the background
        // pre-load (_formIdBuildTask / search index build) Adds type nodes to these same
        // collections concurrently; a lock-free SelectMany over them is a torn-read GC hole.
        var typeNodes = new List<EsmBrowserNode>();
        foreach (var category in root)
        {
            lock (category.Children)
            {
                typeNodes.AddRange(category.Children);
            }
        }

        foreach (var typeNode in typeNodes)
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
