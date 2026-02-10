using System.Collections;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils;

/// <summary>
///     Builds a hierarchical tree of ESM records for the data browser,
///     grouping records by category and type (inspired by TES5Edit/xEdit).
/// </summary>
internal static partial class EsmBrowserTreeBuilder
{
    /// <summary>
    ///     Known property names that represent FormID references but don't end with "FormId".
    /// </summary>
    private static readonly HashSet<string> KnownFormIdFields = new(StringComparer.Ordinal)
    {
        "Race", "Class", "VoiceType", "DefaultHair", "Script",
        "CombatStyle", "DeathItem", "Template", "BaseSpell",
        "Hair", "Eyes", "HeadPart", "AttackRace"
    };

    /// <summary>
    ///     Property names to rename for display (e.g., "EyesFormId" -> "Eyes").
    /// </summary>
    private static readonly Dictionary<string, string> PropertyDisplayNames = new(StringComparer.Ordinal)
    {
        ["EyesFormId"] = "Eyes",
        ["HairFormId"] = "Hair",
        ["CombatStyleFormId"] = "Combat Style"
    };

    /// <summary>
    ///     Category ordering for property display.
    /// </summary>
    private static readonly string[] CategoryOrder =
    [
        "Identity", "Attributes", "Derived Stats", "Characteristics", "AI", "Associations", "References", "General",
        "Metadata"
    ];

    /// <summary>
    ///     Icons for sub-categories that differ from their parent category.
    ///     Uses Segoe MDL2 Assets glyphs where available.
    /// </summary>
    private static readonly Dictionary<string, string> SubCategoryIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        // Characters sub-categories
        ["Creatures"] = "\uEBE8", // Bug (scorpions, flies, roaches)

        // Quests & Dialogue sub-categories
        ["Notes"] = "\uE8A5", // Page
        ["Books"] = "\uE736", // Library
        ["Terminals"] = "\uE7F4", // TVMonitor
        ["Scripts"] = "\uE8E5", // Code/DeveloperTools

        // Items sub-categories
        ["Weapons"] = "\uEC5A", // BarcodeScanner (looks like C4 detonator)
        ["Armor"] = "\uEC1B", // Badge (common armor stat symbol)
        ["Ammo"] = "\uE8F0", // Directions
        ["Consumables"] = "\uEB51", // Heart
        ["Misc Items"] = "\uE74C", // Refresh/Sync
        ["Keys"] = "\uE8D7", // Permissions
        ["Containers"] = "\uF540", // Safe

        // World sub-categories
        ["Cells"] = "\uE707", // MapPin
        ["Map Markers"] = "\uE707" // MapPin
    };

    /// <summary>
    ///     Maps (model type, property name) to flag bit definitions for centralized flag decoding.
    /// </summary>
    private static readonly Dictionary<(Type, string), FlagBit[]> FlagLookup = new()
    {
        [(typeof(FactionRecord), "Flags")] = FlagRegistry.FactionFlags,
        [(typeof(ClassRecord), "Flags")] = FlagRegistry.ClassFlags,
        [(typeof(ClassRecord), "BarterFlags")] = FlagRegistry.BarterFlags,
        [(typeof(RaceRecord), "DataFlags")] = FlagRegistry.RaceDataFlags,
        [(typeof(ChallengeRecord), "Flags")] = FlagRegistry.ChallengeFlags,
        [(typeof(BaseEffectRecord), "Flags")] = FlagRegistry.BaseEffectFlags,
        [(typeof(ExplosionRecord), "Flags")] = FlagRegistry.ExplosionFlags,
        [(typeof(ProjectileRecord), "Flags")] = FlagRegistry.ProjectileFlags,
        [(typeof(SpellRecord), "Flags")] = FlagRegistry.SpellFlags,
        [(typeof(EnchantmentRecord), "Flags")] = FlagRegistry.EnchantmentFlags,
        [(typeof(MessageRecord), "Flags")] = FlagRegistry.MessageFlags,
        [(typeof(QuestRecord), "Flags")] = FlagRegistry.QuestFlags,
        [(typeof(CellRecord), "Flags")] = FlagRegistry.CellFlags,
        [(typeof(ContainerRecord), "Flags")] = FlagRegistry.ContainerFlags,
        [(typeof(BookRecord), "Flags")] = FlagRegistry.BookFlags,
        [(typeof(AmmoRecord), "Flags")] = FlagRegistry.AmmoFlags,
        [(typeof(LeveledListRecord), "Flags")] = FlagRegistry.LeveledListFlags,
        [(typeof(TerminalRecord), "Flags")] = FlagRegistry.TerminalFlags,
        [(typeof(ConsumableRecord), "Flags")] = FlagRegistry.ConsumableFlags,
        [(typeof(ArmorRecord), "BipedFlags")] = FlagRegistry.ArmorBipedFlags,
        [(typeof(ArmorRecord), "GeneralFlags")] = FlagRegistry.ArmorGeneralFlags,
        [(typeof(WeaponRecord), "Flags")] = FlagRegistry.WeaponFlags,
        [(typeof(WeaponRecord), "FlagsEx")] = FlagRegistry.WeaponFlagsEx,
        [(typeof(LightRecord), "Flags")] = FlagRegistry.LightFlags,
        [(typeof(DoorRecord), "Flags")] = FlagRegistry.DoorFlags,
        [(typeof(FurnitureRecord), "MarkerFlags")] = FlagRegistry.FurnitureMarkerFlags,
    };

    /// <summary>
    ///     Fallout NV skill names indexed by skill ID.
    /// </summary>
    private static readonly string[] SkillNames =
    [
        "Barter", "Big Guns", "Energy Weapons", "Explosives", "Lockpick",
        "Medicine", "Melee Weapons", "Repair", "Science", "Guns",
        "Sneak", "Speech", "Survival", "Unarmed"
    ];

    /// <summary>
    ///     Cache for PropertyInfo[] by type - avoids repeated GetProperties() calls.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();

    /// <summary>
    ///     Cache for named property lookups - avoids repeated GetProperty(name) calls.
    /// </summary>
    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> NamedPropertyCache = new();

    /// <summary>
    ///     Gets cached PropertyInfo[] for a type.
    /// </summary>
    private static PropertyInfo[] GetCachedProperties(Type type) =>
        PropertyCache.GetOrAdd(type, t => t.GetProperties());

    /// <summary>
    ///     Gets a cached named property for a type.
    /// </summary>
    private static PropertyInfo? GetCachedProperty(Type type, string name) =>
        NamedPropertyCache.GetOrAdd((type, name), key => key.Item1.GetProperty(key.Item2));

    public static ObservableCollection<EsmBrowserNode> BuildTree(
        RecordCollection result,
        Dictionary<uint, string>? lookup = null)
    {
        var root = new ObservableCollection<EsmBrowserNode>();

        AddCategory(root, "Characters", "\uE77B", [
            ("NPCs", result.Npcs),
            ("Creatures", result.Creatures),
            ("Races", result.Races),
            ("Factions", result.Factions),
            ("Classes", result.Classes)
        ]);

        AddCategory(root, "Quests & Dialogue", "\uE8BD", [
            ("Quests", result.Quests),
            ("Dialog Topics", result.DialogTopics),
            ("Dialogues", result.Dialogues),
            ("Notes", result.Notes),
            ("Books", result.Books),
            ("Terminals", result.Terminals),
            ("Messages", result.Messages),
            ("Scripts", result.Scripts)
        ]);

        AddCategory(root, "Items", "\uE7BF", [
            ("Weapons", result.Weapons),
            ("Armor", result.Armor),
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

        AddWorldCategory(root, result.Worldspaces, result.Cells, result.MapMarkers, result.LeveledLists,
            result.Activators, result.Lights, result.Doors, result.Statics, result.Furniture);

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

        return root;
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
    ///     Adds the World category with cells nested under worldspaces.
    /// </summary>
    private static void AddWorldCategory(
        ObservableCollection<EsmBrowserNode> root,
        List<WorldspaceRecord> worldspaces,
        IList<CellRecord> cells,
        List<PlacedReference> mapMarkers,
        List<LeveledListRecord> leveledLists,
        List<ActivatorRecord> activators,
        List<LightRecord> lights,
        List<DoorRecord> doors,
        List<StaticRecord> statics,
        List<FurnitureRecord> furniture)
    {
        // Group cells by worldspace FormID
        var cellsByWorldspace = cells
            .Where(c => c.WorldspaceFormId.HasValue)
            .GroupBy(c => c.WorldspaceFormId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Interior cells = cells without WorldspaceFormId
        var interiorCells = cells.Where(c => !c.WorldspaceFormId.HasValue).ToList();

        var totalCount = worldspaces.Count + interiorCells.Count + mapMarkers.Count + leveledLists.Count +
                         activators.Count + lights.Count + doors.Count + statics.Count + furniture.Count;
        if (totalCount == 0)
        {
            return;
        }

        // Build record types with special handling for worldspaces
        var recordTypes =
            new List<(string Name, IList Records, Dictionary<uint, List<CellRecord>>? CellLookup)>();

        if (worldspaces.Count > 0)
        {
            recordTypes.Add(("Worldspaces", (IList)worldspaces, cellsByWorldspace));
        }

        if (interiorCells.Count > 0)
        {
            recordTypes.Add(("Interior Cells", (IList)interiorCells, null));
        }

        if (mapMarkers.Count > 0)
        {
            recordTypes.Add(("Map Markers", (IList)mapMarkers, null));
        }

        if (leveledLists.Count > 0)
        {
            recordTypes.Add(("Leveled Lists", (IList)leveledLists, null));
        }

        if (activators.Count > 0)
        {
            recordTypes.Add(("Activators", (IList)activators, null));
        }

        if (lights.Count > 0)
        {
            recordTypes.Add(("Lights", (IList)lights, null));
        }

        if (doors.Count > 0)
        {
            recordTypes.Add(("Doors", (IList)doors, null));
        }

        if (statics.Count > 0)
        {
            recordTypes.Add(("Statics", (IList)statics, null));
        }

        if (furniture.Count > 0)
        {
            recordTypes.Add(("Furniture", (IList)furniture, null));
        }

        var categoryNode = new EsmBrowserNode
        {
            DisplayName = $"World ({totalCount:N0})",
            NodeType = "Category",
            IconGlyph = "\uE774",
            HasUnrealizedChildren = true,
            DataObject = ("World", recordTypes, cellsByWorldspace)
        };

        root.Add(categoryNode);
    }

    /// <summary>
    ///     Populates children for a category node (record type sub-nodes).
    /// </summary>
    public static void LoadCategoryChildren(EsmBrowserNode categoryNode)
    {
        // Handle World category specially (has cells nested under worldspaces)
        if (categoryNode.DataObject is
                ValueTuple<string,
                    List<(string Name, IList Records, Dictionary<uint, List<CellRecord>>? CellLookup)>,
                    Dictionary<uint, List<CellRecord>>> worldData
            && worldData.Item1 == "World")
        {
            LoadWorldCategoryChildren(categoryNode, worldData.Item2, worldData.Item3);
            return;
        }

        // Standard category handling
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

            var typeIcon = SubCategoryIcons.GetValueOrDefault(name, categoryNode.IconGlyph);
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
    ///     Populates children for the World category with worldspaces containing nested cells.
    /// </summary>
    private static void LoadWorldCategoryChildren(
        EsmBrowserNode categoryNode,
        List<(string Name, IList Records, Dictionary<uint, List<CellRecord>>? CellLookup)> recordTypes,
        Dictionary<uint, List<CellRecord>> cellsByWorldspace)
    {
        foreach (var (name, records, _) in recordTypes)
        {
            if (records.Count == 0)
            {
                continue;
            }

            var typeIcon = SubCategoryIcons.GetValueOrDefault(name, categoryNode.IconGlyph);

            // For Worldspaces, store the cell lookup for later expansion
            object dataObject = name == "Worldspaces"
                ? (records, cellsByWorldspace)
                : records;

            var typeNode = new EsmBrowserNode
            {
                DisplayName = $"{name} ({records.Count:N0})",
                NodeType = "RecordType",
                IconGlyph = typeIcon,
                ParentIconGlyph = typeIcon,
                ParentTypeName = name,
                HasUnrealizedChildren = true,
                DataObject = dataObject
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
        Dictionary<uint, string>? lookup = null,
        Dictionary<uint, string>? displayNameLookup = null)
    {
        // Handle Worldspaces with nested cells
        if (typeNode.DataObject is
            (IList worldspaceRecords, Dictionary<uint, List<CellRecord>> cellsByWorldspace))
        {
            LoadWorldspacesWithCells(typeNode, worldspaceRecords, cellsByWorldspace, lookup, displayNameLookup);
            return;
        }

        if (typeNode.DataObject is not IList records)
        {
            return;
        }

        // Build all nodes first (outside of lock for better performance)
        var recordNodes = new List<EsmBrowserNode>(records.Count);

        foreach (var record in records)
        {
            var (formId, editorId, fullName, offset) = ExtractRecordIdentity(record);
            var formIdHex = $"0x{formId:X8}";

            // Build display name and detail (shown as secondary text in tree)
            string displayName;
            string? detail;

            if (record is DialogueRecord dialogue)
            {
                // Dialogue records: show response text with quest/topic context
                displayName = BuildDialogueDisplayName(dialogue, formIdHex, lookup, displayNameLookup);
                detail = BuildDialogueDetail(dialogue, formIdHex, lookup);
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
                Properties = BuildProperties(record, lookup, displayNameLookup)
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
    ///     Builds display name for dialogue (INFO) records.
    ///     Priority: response text > prompt text > quest-topic names > FormID.
    /// </summary>
    private static string BuildDialogueDisplayName(
        DialogueRecord dialogue,
        string formIdHex,
        Dictionary<uint, string>? editorIdLookup,
        Dictionary<uint, string>? displayNameLookup)
    {
        // Try first response text
        var responseText = dialogue.Responses.FirstOrDefault()?.Text;
        if (!string.IsNullOrEmpty(responseText))
        {
            return responseText;
        }

        // Try prompt text
        if (!string.IsNullOrEmpty(dialogue.PromptText))
        {
            return dialogue.PromptText;
        }

        // Fallback: Quest - Topic names
        var questName = ResolveEditorIdOrName(dialogue.QuestFormId, editorIdLookup, displayNameLookup);
        var topicName = ResolveEditorIdOrName(dialogue.TopicFormId, editorIdLookup, displayNameLookup);
        if (questName != null || topicName != null)
        {
            var parts = new[] { questName, topicName }.Where(p => p != null);
            return string.Join(" - ", parts);
        }

        return formIdHex;
    }

    /// <summary>
    ///     Builds detail text for dialogue (INFO) records.
    ///     Format: "(QuestEditorId - TopicEditorId)" or FormID fallback.
    /// </summary>
    private static string? BuildDialogueDetail(
        DialogueRecord dialogue,
        string formIdHex,
        Dictionary<uint, string>? editorIdLookup)
    {
        // Only show quest/topic context when display name is response or prompt text.
        // When display is already quest/topic (no response/prompt), showing it again is redundant.
        var hasTextContent = dialogue.Responses.Any(r => !string.IsNullOrEmpty(r.Text))
                             || !string.IsNullOrEmpty(dialogue.PromptText);

        if (!hasTextContent)
        {
            return formIdHex;
        }

        var questId = dialogue.QuestFormId is > 0
            ? editorIdLookup?.GetValueOrDefault(dialogue.QuestFormId.Value)
            : null;
        var topicId = dialogue.TopicFormId is > 0
            ? editorIdLookup?.GetValueOrDefault(dialogue.TopicFormId.Value)
            : null;

        if (questId != null || topicId != null)
        {
            var parts = new[] { questId, topicId }.Where(p => p != null);
            return $"({string.Join(" - ", parts)})";
        }

        return formIdHex;
    }

    private static string? ResolveEditorIdOrName(
        uint? formId,
        Dictionary<uint, string>? editorIdLookup,
        Dictionary<uint, string>? displayNameLookup)
    {
        if (formId is not > 0) return null;
        return editorIdLookup?.GetValueOrDefault(formId.Value)
            ?? displayNameLookup?.GetValueOrDefault(formId.Value);
    }

    /// <summary>
    ///     Populates worldspace nodes with their cells as nested children.
    /// </summary>
    private static void LoadWorldspacesWithCells(
        EsmBrowserNode typeNode,
        IList worldspaceRecords,
        Dictionary<uint, List<CellRecord>> cellsByWorldspace,
        Dictionary<uint, string>? lookup,
        Dictionary<uint, string>? displayNameLookup)
    {
        var recordNodes = new List<EsmBrowserNode>(worldspaceRecords.Count);

        foreach (var record in worldspaceRecords)
        {
            var (formId, editorId, fullName, offset) = ExtractRecordIdentity(record);
            var formIdHex = $"0x{formId:X8}";

            // Get cells for this worldspace
            var worldspaceCells = cellsByWorldspace.GetValueOrDefault(formId) ?? [];
            var cellCount = worldspaceCells.Count;

            // Build display name with cell count
            string displayName;
            string baseName;
            if (!string.IsNullOrEmpty(fullName))
            {
                baseName = fullName;
            }
            else if (!string.IsNullOrEmpty(editorId))
            {
                baseName = editorId;
            }
            else
            {
                baseName = formIdHex;
            }

            displayName = cellCount > 0
                ? $"{baseName} ({cellCount:N0} cells)"
                : baseName;

            var detail = !string.IsNullOrEmpty(editorId) && editorId != baseName
                ? $"({editorId})"
                : null;

            // Prepare DataObject - if has cells, store tuple for lazy loading; otherwise just the record
            // Use empty dictionaries as fallback to avoid nullable pattern matching issues
            object dataObj = cellCount > 0
                ? (record, worldspaceCells, lookup ?? new Dictionary<uint, string>(),
                    displayNameLookup ?? new Dictionary<uint, string>())
                : record;

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
                DataObject = dataObj,
                Properties = BuildProperties(record, lookup, displayNameLookup),
                // Cells will be loaded as children when expanded
                HasUnrealizedChildren = cellCount > 0
            };

            recordNodes.Add(recordNode);
        }

        // Sort by display name
        var sorted = recordNodes.OrderBy(n => n.DisplayName ?? "", StringComparer.OrdinalIgnoreCase).ToList();

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
    ///     Populates cells as children of a worldspace node.
    /// </summary>
    public static void LoadWorldspaceCellChildren(
        EsmBrowserNode worldspaceNode,
        Dictionary<uint, string>? lookup = null,
        Dictionary<uint, string>? displayNameLookup = null)
    {
        // DataObject is a tuple: (record, cells, lookup, displayNameLookup)
        if (worldspaceNode.DataObject is not
            ValueTuple<object, List<CellRecord>, Dictionary<uint, string>, Dictionary<uint, string>> cellData)
        {
            return;
        }

        var record = cellData.Item1;
        var cells = cellData.Item2;

        // Use stored lookups if available
        lookup ??= cellData.Item3;
        displayNameLookup ??= cellData.Item4;

        // Get parent worldspace name for fallback display
        var (wsFormId, wsEditorId, _, _) = ExtractRecordIdentity(record);
        var worldspaceName = !string.IsNullOrEmpty(wsEditorId) ? wsEditorId : $"0x{wsFormId:X8}";

        var cellNodes = new List<EsmBrowserNode>(cells.Count);

        foreach (var cell in cells)
        {
            var formIdHex = $"0x{cell.FormId:X8}";

            // Build display name with fallback to "WorldspaceName [GridX, GridY]" or "[FormID]"
            string displayName;
            if (!string.IsNullOrEmpty(cell.FullName))
            {
                displayName = cell.FullName;
            }
            else if (!string.IsNullOrEmpty(cell.EditorId))
            {
                displayName = cell.EditorId;
            }
            else if (cell.GridX.HasValue && cell.GridY.HasValue)
            {
                displayName = $"{worldspaceName} [{cell.GridX}, {cell.GridY}]";
            }
            else
            {
                displayName = $"{worldspaceName} [{formIdHex}]";
            }

            var detail = !string.IsNullOrEmpty(cell.EditorId) && cell.EditorId != displayName
                ? $"({cell.EditorId})"
                : formIdHex;

            var cellNode = new EsmBrowserNode
            {
                DisplayName = displayName,
                Detail = detail,
                FormIdHex = formIdHex,
                EditorId = cell.EditorId,
                NodeType = "Record",
                IconGlyph = "\uE707", // MapPin
                ParentTypeName = "Cells",
                ParentIconGlyph = "\uE707",
                FileOffset = cell.Offset,
                DataObject = cell,
                Properties = BuildProperties(cell, lookup, displayNameLookup)
            };

            cellNodes.Add(cellNode);
        }

        // Sort by display name
        var sorted = cellNodes.OrderBy(n => n.DisplayName ?? "", StringComparer.OrdinalIgnoreCase).ToList();

        lock (worldspaceNode.Children)
        {
            worldspaceNode.Children.Clear();
            foreach (var node in sorted)
            {
                worldspaceNode.Children.Add(node);
            }
        }

        worldspaceNode.HasUnrealizedChildren = false;
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

    private static (uint FormId, string? EditorId, string? FullName, long Offset) ExtractRecordIdentity(object record)
    {
        // All reconstructed records have FormId, EditorId, and Offset as properties
        // Use cached property lookups for performance (avoids repeated reflection overhead)
        var type = record.GetType();
        var formId = (uint)(GetCachedProperty(type, "FormId")?.GetValue(record) ?? 0u);
        var editorId = GetCachedProperty(type, "EditorId")?.GetValue(record) as string;
        var fullName = GetCachedProperty(type, "FullName")?.GetValue(record) as string;
        var offset = (long)(GetCachedProperty(type, "Offset")?.GetValue(record) ?? 0L);
        return (formId, editorId, fullName, offset);
    }

    /// <summary>
    ///     Builds a property list from a record's public properties for the detail panel.
    /// </summary>
    public static List<EsmPropertyEntry> BuildProperties(
        object record,
        Dictionary<uint, string>? lookup = null,
        Dictionary<uint, string>? displayNameLookup = null)
    {
        var properties = new List<EsmPropertyEntry>();
        var type = record.GetType();

        // Record-type flags for special handling
        var isCreature = record is CreatureRecord;
        var isRace = record is RaceRecord;

        foreach (var prop in GetCachedProperties(type))
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

            // Skip race SPECIAL attributes (combined into single line below)
            if (isRace && prop.Name is "Strength" or "Perception" or "Endurance" or "Charisma"
                    or "Intelligence" or "Agility" or "Luck")
            {
                continue;
            }

            var value = prop.GetValue(record);
            var displayName = FormatPropertyName(prop.Name);

            // Special handling for ActorBaseSubrecord - extract into Characteristics and Attributes
            // NPC and Creature have different relevant fields from ACBS
            if (value is ActorBaseSubrecord stats)
            {
                var isNpc = record is NpcRecord;

                // Common fields (both NPC and Creature)
                var gender = (stats.Flags & 1) == 1 ? "Female" : "Male";
                properties.Add(new EsmPropertyEntry { Name = "Gender", Value = gender, Category = "Characteristics" });
                properties.Add(new EsmPropertyEntry
                {
                    Name = "Actor Flags",
                    Value = FlagRegistry.DecodeFlagNamesWithHex(stats.Flags, FlagRegistry.ActorBaseFlags),
                    Category = "Characteristics"
                });
                properties.Add(new EsmPropertyEntry
                { Name = "Level", Value = stats.Level.ToString(), Category = "Attributes" });
                properties.Add(new EsmPropertyEntry
                { Name = "Calc Min Level", Value = stats.CalcMin.ToString(), Category = "Attributes" });
                properties.Add(new EsmPropertyEntry
                { Name = "Calc Max Level", Value = stats.CalcMax.ToString(), Category = "Attributes" });
                properties.Add(new EsmPropertyEntry
                { Name = "Fatigue", Value = stats.FatigueBase.ToString(), Category = "Attributes" });
                properties.Add(new EsmPropertyEntry
                { Name = "Speed Multiplier", Value = $"{stats.SpeedMultiplier}%", Category = "Attributes" });

                // NPC-only fields (creatures don't have barter gold, karma, disposition)
                if (isNpc)
                {
                    properties.Add(new EsmPropertyEntry
                    { Name = "Barter Gold", Value = stats.BarterGold.ToString(), Category = "Attributes" });
                    properties.Add(new EsmPropertyEntry
                    { Name = "Karma", Value = $"{stats.KarmaAlignment:F2}", Category = "Attributes" });
                    properties.Add(new EsmPropertyEntry
                    { Name = "Disposition", Value = stats.DispositionBase.ToString(), Category = "Attributes" });
                }

                if (stats.TemplateFlags != 0)
                {
                    properties.Add(new EsmPropertyEntry
                    {
                        Name = "Template Flags",
                        Value = FlagRegistry.DecodeFlagNamesWithHex(stats.TemplateFlags, FlagRegistry.TemplateUseFlags),
                        Category = "Characteristics"
                    });
                }

                continue;
            }

            // Special handling for NpcAiData
            if (value is NpcAiData ai)
            {
                properties.Add(new EsmPropertyEntry
                { Name = "Aggression", Value = $"{ai.AggressionName} ({ai.Aggression})", Category = "AI" });
                properties.Add(new EsmPropertyEntry
                { Name = "Confidence", Value = $"{ai.ConfidenceName} ({ai.Confidence})", Category = "AI" });
                properties.Add(new EsmPropertyEntry
                { Name = "Mood", Value = $"{ai.MoodName} ({ai.Mood})", Category = "AI" });
                properties.Add(new EsmPropertyEntry
                { Name = "Assistance", Value = $"{ai.AssistanceName} ({ai.Assistance})", Category = "AI" });
                properties.Add(new EsmPropertyEntry
                { Name = "Energy Level", Value = ai.EnergyLevel.ToString(), Category = "AI" });
                properties.Add(new EsmPropertyEntry
                {
                    Name = "Responsibility",
                    Value = $"{ai.ResponsibilityName} ({ai.Responsibility})",
                    Category = "AI"
                });
                continue;
            }

            // Special handling for SpecialStats (byte[7]) - S.P.E.C.I.A.L.
            if (prop.Name == "SpecialStats" && value is byte[] special && special.Length == 7)
            {
                var total = special.Sum(b => b);
                var formatted = $"{special[0]} ST, {special[1]} PE, {special[2]} EN, {special[3]} CH, " +
                                $"{special[4]} IN, {special[5]} AG, {special[6]} LK  (Total: {total})";
                properties.Add(new EsmPropertyEntry
                { Name = "S.P.E.C.I.A.L.", Value = formatted, Category = "Attributes" });
                continue;
            }

            // Special handling for Skills (byte[14])
            if (prop.Name == "Skills" && value is byte[] skills && skills.Length >= 13)
            {
                var subItems = new List<EsmPropertyEntry>();
                for (var i = 0; i < skills.Length && i < SkillNames.Length; i++)
                {
                    if (i == 1) continue; // Skip BigGuns (index 1) - unused in Fallout NV
                    subItems.Add(new EsmPropertyEntry { Name = SkillNames[i], Value = skills[i].ToString() });
                }

                properties.Add(new EsmPropertyEntry
                {
                    Name = "Skills",
                    Value = $"{subItems.Count} skills",
                    Category = "Attributes",
                    IsExpandable = true,
                    SubItems = subItems
                });
                continue;
            }

            // Special handling for FaceGen float arrays - show raw hex in expandable panel
            // No offsets, single selectable hex block
            if (prop.Name.StartsWith("FaceGen", StringComparison.Ordinal) && value is float[] morphs &&
                morphs.Length > 0)
            {
                // Convert floats to raw bytes
                var rawBytes = new byte[morphs.Length * 4];
                Buffer.BlockCopy(morphs, 0, rawBytes, 0, rawBytes.Length);

                // Create hex block with 16 bytes per line, no offsets
                var hexLines = new List<string>();
                for (var i = 0; i < rawBytes.Length; i += 16)
                {
                    var lineBytes = rawBytes.Skip(i).Take(16);
                    hexLines.Add(string.Join(" ", lineBytes.Select(b => b.ToString("X2"))));
                }

                var hexBlock = string.Join("\n", hexLines);

                // Single sub-item with the entire hex block (selectable as one)
                var subItems = new List<EsmPropertyEntry>
                {
                    new() { Name = "", Value = hexBlock }
                };

                properties.Add(new EsmPropertyEntry
                {
                    Name = displayName,
                    Value = $"{rawBytes.Length} bytes",
                    Category = "Characteristics",
                    IsExpandable = true,
                    SubItems = subItems
                });
                continue;
            }

            // Special handling for multi-line script text (SourceText, DecompiledText)
            if (prop.Name is "SourceText" or "DecompiledText" && value is string textContent &&
                !string.IsNullOrEmpty(textContent))
            {
                var lines = textContent.Split('\n');
                var subItems = new List<EsmPropertyEntry>
                {
                    new() { Name = "", Value = textContent }
                };

                properties.Add(new EsmPropertyEntry
                {
                    Name = displayName,
                    Value = $"{lines.Length} lines",
                    Category = "General",
                    IsExpandable = true,
                    SubItems = subItems
                });
                continue;
            }

            // Special handling for CompiledData (SCDA bytecode) - show hex in expandable panel
            if (prop.Name == "CompiledData" && value is byte[] compiledBytes && compiledBytes.Length > 0)
            {
                var hexLines = new List<string>();
                for (var i = 0; i < compiledBytes.Length; i += 16)
                {
                    var lineBytes = compiledBytes.Skip(i).Take(16);
                    hexLines.Add(string.Join(" ", lineBytes.Select(b => b.ToString("X2"))));
                }

                var hexBlock = string.Join("\n", hexLines);
                var subItems = new List<EsmPropertyEntry>
                {
                    new() { Name = "", Value = hexBlock }
                };

                properties.Add(new EsmPropertyEntry
                {
                    Name = displayName,
                    Value = $"{compiledBytes.Length} bytes",
                    Category = CategorizeProperty(prop.Name),
                    IsExpandable = true,
                    SubItems = subItems
                });
                continue;
            }

            // Class-specific: Tag Skills as names instead of raw indices
            if (prop.Name == "TagSkills" && value is int[] tagSkillIndices)
            {
                var names = tagSkillIndices
                    .Where(i => i >= 0 && i < SkillNames.Length)
                    .Select(i => SkillNames[i]);
                properties.Add(new EsmPropertyEntry
                {
                    Name = "Tag Skills",
                    Value = string.Join(", ", names),
                    Category = "Attributes"
                });
                continue;
            }

            // Class-specific: Training Skill as name
            if (prop.Name == "TrainingSkill" && value is byte trainingIdx)
            {
                var skillName = trainingIdx < SkillNames.Length
                    ? SkillNames[trainingIdx]
                    : $"Unknown ({trainingIdx})";
                properties.Add(new EsmPropertyEntry
                {
                    Name = "Training Skill",
                    Value = skillName,
                    Category = "Attributes"
                });
                continue;
            }

            // Centralized flag decoding: check FlagLookup for any recognized (type, property) pair
            if (FlagLookup.TryGetValue((type, prop.Name), out var flagDefs))
            {
                var flagValue = Convert.ToUInt32(value);
                properties.Add(new EsmPropertyEntry
                {
                    Name = displayName,
                    Value = FlagRegistry.DecodeFlagNamesWithHex(flagValue, flagDefs),
                    Category = CategorizeProperty(prop.Name)
                });
                continue;
            }

            // Class-specific: Attribute Weights as S.P.E.C.I.A.L.
            if (prop.Name == "AttributeWeights" && value is byte[] attrWeights && attrWeights.Length == 7)
            {
                var formatted =
                    $"{attrWeights[0]} ST, {attrWeights[1]} PE, {attrWeights[2]} EN, {attrWeights[3]} CH, " +
                    $"{attrWeights[4]} IN, {attrWeights[5]} AG, {attrWeights[6]} LK";
                properties.Add(new EsmPropertyEntry
                {
                    Name = "Attribute Weights",
                    Value = formatted,
                    Category = "Attributes"
                });
                continue;
            }

            // Handle expandable list properties (skip empty lists)
            if (value is IList list)
            {
                if (list.Count == 0)
                {
                    continue;
                }

                var subItems = new List<EsmPropertyEntry>();
                foreach (var item in list)
                {
                    // Create table-like entries for Factions and Inventory (with column data)
                    subItems.Add(CreateListItemEntry(item, lookup, displayNameLookup));
                }

                properties.Add(new EsmPropertyEntry
                {
                    Name = $"{displayName} ({list.Count} items)",
                    Value = "",
                    Category = CategorizeProperty(prop.Name),
                    IsExpandable = true,
                    SubItems = subItems
                });
                continue;
            }

            // Check if this is a FormID reference field that should use column layout
            var isFormIdField = (prop.PropertyType == typeof(uint) || prop.PropertyType == typeof(uint?)) &&
                                (prop.Name.EndsWith("FormId", StringComparison.Ordinal) ||
                                 KnownFormIdFields.Contains(prop.Name)) &&
                                prop.Name != "FormId"; // Exclude the main FormId property

            if (isFormIdField && value is uint formIdVal && formIdVal != 0)
            {
                var editorId = lookup?.GetValueOrDefault(formIdVal);
                var fullName = displayNameLookup?.GetValueOrDefault(formIdVal);

                properties.Add(new EsmPropertyEntry
                {
                    Name = displayName,
                    Value = FormatFormIdReference(formIdVal, editorId, fullName),
                    Category = CategorizeProperty(prop.Name)
                });
                continue;
            }

            var valueStr = FormatPropertyValue(prop.Name, value, prop.PropertyType, lookup, displayNameLookup);
            if (valueStr == null)
            {
                continue;
            }

            properties.Add(new EsmPropertyEntry
            {
                Name = displayName,
                Value = valueStr,
                Category = CategorizeProperty(prop.Name)
            });
        }

        // Creature-specific fields (type, skills, damage)
        if (record is CreatureRecord crea)
        {
            // Always show creature type (even if 0 = Animal)
            properties.Add(new EsmPropertyEntry
            {
                Name = "Creature Type",
                Value = crea.CreatureTypeName,
                Category = "Characteristics"
            });

            // Show skills if any are populated
            if (crea.CombatSkill > 0 || crea.MagicSkill > 0 || crea.StealthSkill > 0)
            {
                properties.Add(new EsmPropertyEntry
                { Name = "Combat Skill", Value = crea.CombatSkill.ToString(), Category = "Attributes" });
                properties.Add(new EsmPropertyEntry
                { Name = "Magic Skill", Value = crea.MagicSkill.ToString(), Category = "Attributes" });
                properties.Add(new EsmPropertyEntry
                { Name = "Stealth Skill", Value = crea.StealthSkill.ToString(), Category = "Attributes" });
            }

            // Show attack damage if set
            if (crea.AttackDamage != 0)
            {
                properties.Add(new EsmPropertyEntry
                { Name = "Attack Damage", Value = crea.AttackDamage.ToString(), Category = "Attributes" });
            }
        }

        // Add Derived Stats section for NPCs (computed from S.P.E.C.I.A.L. and Level)
        if (record is NpcRecord npc && npc.SpecialStats?.Length >= 7 && npc.Stats != null)
        {
            var str = npc.SpecialStats[0];
            var end = npc.SpecialStats[2];
            var lck = npc.SpecialStats[6];
            var level = npc.Stats.Level;
            var fatigueBase = npc.Stats.FatigueBase;

            // Calculate derived stats (same formulas as GeckReportGenerator)
            var baseHealth = end * 5 + 50;
            var calcHealth = baseHealth + level * 10;
            var calcFatigue = fatigueBase + (str + end) * 10;
            var critChance = (float)lck;
            var meleeDamage = str * 0.5f;
            var unarmedDamage = 0.5f + str * 0.1f;
            var poisonResist = (end - 1) * 5;
            var radResist = (end - 1) * 2;

            properties.Add(new EsmPropertyEntry
            {
                Name = "Health",
                Value = $"{calcHealth} (Base: {baseHealth} + Level×10)",
                Category = "Derived Stats"
            });
            properties.Add(new EsmPropertyEntry
            {
                Name = "Fatigue",
                Value = $"{calcFatigue} (Base: {fatigueBase} + (STR+END)×10)",
                Category = "Derived Stats"
            });
            properties.Add(new EsmPropertyEntry
            {
                Name = "Critical Chance",
                Value = $"{critChance:F0}%",
                Category = "Derived Stats"
            });
            properties.Add(new EsmPropertyEntry
            {
                Name = "Melee Damage",
                Value = $"{meleeDamage:F1}",
                Category = "Derived Stats"
            });
            properties.Add(new EsmPropertyEntry
            {
                Name = "Unarmed Damage",
                Value = $"{unarmedDamage:F1}",
                Category = "Derived Stats"
            });
            properties.Add(new EsmPropertyEntry
            {
                Name = "Poison Resistance",
                Value = $"{poisonResist}%",
                Category = "Derived Stats"
            });
            properties.Add(new EsmPropertyEntry
            {
                Name = "Radiation Resistance",
                Value = $"{radResist}%",
                Category = "Derived Stats"
            });
        }

        // Race-specific: Combine S.P.E.C.I.A.L. modifiers into single line
        if (record is RaceRecord raceRecord)
        {
            var formatted =
                $"{raceRecord.Strength} ST, {raceRecord.Perception} PE, {raceRecord.Endurance} EN, " +
                $"{raceRecord.Charisma} CH, {raceRecord.Intelligence} IN, {raceRecord.Agility} AG, " +
                $"{raceRecord.Luck} LK";
            properties.Add(new EsmPropertyEntry
            {
                Name = "S.P.E.C.I.A.L. Modifiers",
                Value = formatted,
                Category = "Attributes"
            });
        }

        // Sort properties by category for consistent grouping
        return properties.OrderBy(p => Array.IndexOf(CategoryOrder, p.Category ?? "General"))
            .ThenBy(p => p.Category == "General" ? 1 : 0) // Unknown categories at end
            .ToList();
    }

    /// <summary>
    ///     Converts CamelCase property names to "Title Case" for display,
    ///     with proper handling of common acronyms and custom renames.
    /// </summary>
    private static string FormatPropertyName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        // Check for custom display name first
        if (PropertyDisplayNames.TryGetValue(name, out var customName))
        {
            return customName;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            // Insert space before uppercase letters (except at start or after another uppercase)
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
            {
                sb.Append(' ');
            }

            sb.Append(name[i]);
        }

        var result = sb.ToString();

        // Fix common acronyms that should be all-caps
        result = result.Replace(" Id", " ID");

        return result;
    }

    /// <summary>
    ///     Creates an EsmPropertyEntry for a list item with column layout for FormID references.
    /// </summary>
    private static EsmPropertyEntry CreateListItemEntry(
        object? item,
        Dictionary<uint, string>? lookup,
        Dictionary<uint, string>? displayNameLookup)
    {
        if (item == null)
        {
            return new EsmPropertyEntry { Name = "", Value = "" };
        }

        // Factions: 4 columns - Editor ID, Full Name, Form ID, Rank
        if (item is FactionMembership faction)
        {
            var fullName = displayNameLookup?.GetValueOrDefault(faction.FactionFormId);
            var editorId = lookup?.GetValueOrDefault(faction.FactionFormId);
            return new EsmPropertyEntry
            {
                Col1 = editorId ?? "",
                Col2 = fullName ?? "",
                Col3 = $"0x{faction.FactionFormId:X8}",
                Col4 = $"Rank {faction.Rank}"
            };
        }

        // Faction Ranks: Rank Number, Male Title, Female Title
        if (item is FactionRank rank)
        {
            return new EsmPropertyEntry
            {
                Col1 = $"Rank {rank.RankNumber}",
                Col2 = rank.MaleTitle ?? "",
                Col3 = rank.FemaleTitle ?? "",
                Col4 = rank.Insignia ?? ""
            };
        }

        // Faction Relations: Editor ID/Name, Full Name, Modifier, Combat Reaction
        if (item is FactionRelation relation)
        {
            var fullName = displayNameLookup?.GetValueOrDefault(relation.FactionFormId);
            var editorId = lookup?.GetValueOrDefault(relation.FactionFormId);
            return new EsmPropertyEntry
            {
                Col1 = editorId ?? $"0x{relation.FactionFormId:X8}",
                Col2 = fullName ?? "",
                Col3 = $"Modifier: {relation.Modifier}",
                Col4 = $"Combat: {relation.CombatFlags}"
            };
        }

        // Inventory: 4 columns - Quantity, Editor ID, Full Name, Form ID
        if (item is InventoryItem inv)
        {
            var fullName = displayNameLookup?.GetValueOrDefault(inv.ItemFormId);
            var editorId = lookup?.GetValueOrDefault(inv.ItemFormId);
            return new EsmPropertyEntry
            {
                Col1 = $"{inv.Count}×",
                Col2 = editorId ?? "",
                Col3 = fullName ?? "",
                Col4 = $"0x{inv.ItemFormId:X8}"
            };
        }

        // Leveled List Entries: 4 columns - Level, Editor ID, Full Name, Form ID + Count
        if (item is LeveledEntry entry)
        {
            var fullName = displayNameLookup?.GetValueOrDefault(entry.FormId);
            var editorId = lookup?.GetValueOrDefault(entry.FormId);
            return new EsmPropertyEntry
            {
                Col1 = $"Lvl {entry.Level}",
                Col2 = editorId ?? "",
                Col3 = fullName ?? "",
                Col4 = $"0x{entry.FormId:X8} (×{entry.Count})"
            };
        }

        // For uint items that might be FormIDs (Spells, Packages, etc.)
        if (item is uint formId && formId != 0)
        {
            var fullName = displayNameLookup?.GetValueOrDefault(formId);
            var editorId = lookup?.GetValueOrDefault(formId);
            return new EsmPropertyEntry
            {
                Name = FormatFormIdReference(formId, editorId, fullName),
                Value = ""
            };
        }

        // For complex objects, format as Name = type, Value = properties
        var type = item.GetType();
        if (!type.IsPrimitive && type != typeof(string) && !type.IsEnum)
        {
            var props = GetCachedProperties(type);
            if (props.Length > 0)
            {
                var parts = new List<string>();
                foreach (var prop in props)
                {
                    if (!prop.CanRead)
                    {
                        continue;
                    }

                    var val = prop.GetValue(item);
                    if (val == null)
                    {
                        continue;
                    }

                    // Resolve FormID-like fields in sub-items
                    if (val is uint fid && fid != 0 &&
                        (prop.Name.EndsWith("FormId", StringComparison.Ordinal) ||
                         prop.Name == "FormId" || KnownFormIdFields.Contains(prop.Name)))
                    {
                        var dispName = displayNameLookup?.GetValueOrDefault(fid);
                        var edId = lookup?.GetValueOrDefault(fid);
                        parts.Add($"{FormatPropertyName(prop.Name)}: {FormatFormIdReference(fid, edId, dispName)}");
                    }
                    else if (val is float f)
                    {
                        parts.Add($"{FormatPropertyName(prop.Name)}: {f:F2}");
                    }
                    else
                    {
                        parts.Add($"{FormatPropertyName(prop.Name)}: {val}");
                    }
                }

                return new EsmPropertyEntry { Name = "", Value = string.Join(", ", parts) };
            }
        }

        return new EsmPropertyEntry { Name = "", Value = item.ToString() ?? "" };
    }

    private static string? FormatPropertyValue(
        string name,
        object? value,
        Type propertyType,
        Dictionary<uint, string>? lookup,
        Dictionary<uint, string>? displayNameLookup = null)
    {
        // Show FullName even when empty (important for creatures without display names)
        if (name == "FullName")
        {
            var str = value as string;
            return string.IsNullOrEmpty(str) ? "—" : str;
        }

        if (value == null)
        {
            return null;
        }

        // Skip binary data in property view
        if (value is byte[] bytes)
        {
            return bytes.Length > 0 ? $"[{bytes.Length} bytes]" : null;
        }

        if (value is float[] floats)
        {
            return floats.Length > 0 ? $"[{floats.Length} values]" : null;
        }

        // Format FormId property (just hex, no editor ID - that's shown separately)
        if (name == "FormId" && value is uint fid)
        {
            return $"0x{fid:X8}";
        }

        // Handle FormID reference fields - unified format per matrix:
        // Full Name (Editor ID) [Form ID] | Editor ID [Form ID] | Full Name [Form ID] | Form ID
        if ((propertyType == typeof(uint) || propertyType == typeof(uint?)) &&
            (name.EndsWith("FormId", StringComparison.Ordinal) || KnownFormIdFields.Contains(name)))
        {
            var formId = value is uint u ? u : ((uint?)value).GetValueOrDefault();
            if (formId == 0)
            {
                return null;
            }

            var editorId = lookup?.GetValueOrDefault(formId);
            var displayName = displayNameLookup?.GetValueOrDefault(formId);

            return FormatFormIdReference(formId, editorId, displayName);
        }

        // Format offset as hex
        if (name == "Offset" && value is long offset)
        {
            return $"0x{offset:X8}";
        }

        // Standard formatting
        if (value is float f)
        {
            return $"{f:F2}";
        }

        return value.ToString();
    }

    /// <summary>
    ///     Formats a FormID reference with inline labels.
    ///     Format: "DisplayName (Editor ID: EditorID) [0xFormID]"
    /// </summary>
    private static string FormatFormIdReference(uint formId, string? editorId, string? displayName)
    {
        var hasFormId = formId != 0;
        var hasEditorId = !string.IsNullOrEmpty(editorId);
        var hasDisplayName = !string.IsNullOrEmpty(displayName);

        return (hasFormId, hasEditorId, hasDisplayName) switch
        {
            // Display Name + Editor ID + Form ID
            (true, true, true) => $"{displayName} (Editor ID: {editorId}) [0x{formId:X8}]",

            // Display Name + Form ID (no Editor ID)
            (true, false, true) => $"{displayName} [0x{formId:X8}]",

            // Display Name + Editor ID (no Form ID)
            (false, true, true) => $"{displayName} (Editor ID: {editorId})",

            // Display Name only
            (false, false, true) => displayName!,

            // Editor ID + Form ID (no Display Name)
            (true, true, false) => $"{editorId} [0x{formId:X8}]",

            // Editor ID only
            (false, true, false) => editorId!,

            // Form ID only
            (true, false, false) => $"0x{formId:X8}",

            // Nothing
            (false, false, false) => "Unknown"
        };
    }

    private static string CategorizeProperty(string name)
    {
        return name switch
        {
            // Identity (minimal)
            "FormId" or "EditorId" or "FullName" => "Identity",
            "Offset" or "IsBigEndian" => "Metadata",

            // Characteristics (appearance-related)
            "Gender" or "Race" or "VoiceType" or "Eyes" or "EyesFormId" or "Hair" or "HairFormId" or "HairLength"
                or "MaleHeight" or "FemaleHeight" or "MaleWeight" or "FemaleWeight"
                or "IsPlayable" or "IsChild"
                => "Characteristics",
            _ when name.StartsWith("FaceGen", StringComparison.Ordinal) => "Characteristics",

            // Attributes (stats and abilities)
            "Level" or "Fatigue" or "BarterGold" or "SpeedMultiplier" or "Karma" or "Disposition"
                or "Class" or "Template" or "SpecialStats" or "Skills"
                or "Weight" or "Value" or "Health" or "Damage" or "Speed" or "Reach"
                or "DamageThreshold" or "DamageResistance"
                or "ClipSize" or "MinSpread" or "Spread" or "Drift" or "ShotsPerSec" or "ActionPoints"
                or "DamagePerSecond" or "AttackMultiplier" or "LimbDamageMult" or "AimArc"
                or "CriticalDamage" or "CriticalChance" or "AmmoPerShot" or "NumProjectiles"
                or "VatsToHitChance" or "StrengthRequirement" or "SkillRequirement"
                or "IronSightFov" or "MinRange" or "MaxRange"
                => "Attributes",

            // AI (behavior-related)
            "Aggression" or "Confidence" or "Mood" or "Assistance" or "EnergyLevel"
                or "Responsibility" or "CombatStyle" or "CombatStyleFormId"
                => "AI",

            // Associations (references to other records)
            "Factions" or "Spells" or "Inventory" or "Packages" or "Ranks" or "Relations"
                or "AbilityFormIds" or "HairStyleFormIds" or "EyeColorFormIds"
                => "Associations",

            // References (other FormID fields)
            _ when name.EndsWith("FormId", StringComparison.Ordinal) => "References",
            _ when KnownFormIdFields.Contains(name) => "References",
            _ when name.EndsWith("Sound", StringComparison.Ordinal) => "References",

            _ => "General"
        };
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
