using System.Collections;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Models;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Subrecords;

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
        "Identity", "Attributes", "Derived Stats", "Characteristics", "AI", "Associations", "References", "General", "Metadata"
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
        SemanticReconstructionResult result,
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
            ("Messages", result.Messages)
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

        AddCategory(root, "World", "\uE774", [
            ("Cells", result.Cells),
            ("Worldspaces", result.Worldspaces),
            ("Map Markers", result.MapMarkers),
            ("Leveled Lists", result.LeveledLists)
        ]);

        AddCategory(root, "Game Data", "\uE8F1", [
            ("Game Settings", result.GameSettings),
            ("Globals", result.Globals),
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
    ///     Populates children for a record type node (individual records).
    /// </summary>
    public static void LoadRecordTypeChildren(
        EsmBrowserNode typeNode,
        Dictionary<uint, string>? lookup = null,
        Dictionary<uint, string>? displayNameLookup = null)
    {
        if (typeNode.DataObject is not IList records)
        {
            return;
        }

        foreach (var record in records)
        {
            var (formId, editorId, fullName, offset) = ExtractRecordIdentity(record);
            var formIdHex = $"0x{formId:X8}";

            // Build display name: "FullName (EditorId)" if both exist
            string displayName;
            if (!string.IsNullOrEmpty(fullName) && !string.IsNullOrEmpty(editorId))
            {
                displayName = $"{fullName} ({editorId})";
            }
            else if (!string.IsNullOrEmpty(fullName))
            {
                displayName = fullName;
            }
            else if (!string.IsNullOrEmpty(editorId))
            {
                displayName = editorId;
            }
            else
            {
                displayName = formIdHex;
            }

            var editorIdDisplay = !string.IsNullOrEmpty(editorId) && editorId != displayName
                ? editorId
                : null;

            var recordNode = new EsmBrowserNode
            {
                DisplayName = displayName,
                FormIdHex = formIdHex,
                EditorId = editorIdDisplay,
                NodeType = "Record",
                IconGlyph = typeNode.IconGlyph,
                ParentTypeName = typeNode.ParentTypeName,
                ParentIconGlyph = typeNode.IconGlyph,
                FileOffset = offset,
                DataObject = record,
                Properties = BuildProperties(record, lookup, displayNameLookup)
            };

            typeNode.Children.Add(recordNode);
        }

        // Sort children by display name (default sort)
        var sorted = typeNode.Children.OrderBy(n => n.DisplayName ?? "", StringComparer.OrdinalIgnoreCase).ToList();
        typeNode.Children.Clear();
        foreach (var node in sorted)
        {
            typeNode.Children.Add(node);
        }

        typeNode.HasUnrealizedChildren = false;
    }

    /// <summary>
    ///     Re-sorts children of all record type nodes based on the selected sort mode.
    /// </summary>
    public static void SortRecordChildren(ObservableCollection<EsmBrowserNode> root, RecordSortMode mode)
    {
        foreach (var categoryNode in root)
        {
            foreach (var typeNode in categoryNode.Children)
            {
                if (typeNode.Children.Count == 0)
                {
                    continue;
                }

                var sorted = mode switch
                {
                    RecordSortMode.EditorId => typeNode.Children
                        .OrderBy(n => n.EditorId ?? n.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
                    RecordSortMode.FormId => typeNode.Children
                        .OrderBy(n => n.FormIdHex ?? "", StringComparer.OrdinalIgnoreCase).ToList(),
                    _ => typeNode.Children
                        .OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase).ToList()
                };

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

        // Creature-specific properties handled separately (prevent duplication)
        var isCreature = record is ReconstructedCreature;

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

            var value = prop.GetValue(record);
            var displayName = FormatPropertyName(prop.Name);

            // Special handling for ActorBaseSubrecord - extract into Characteristics and Attributes
            // NPC and Creature have different relevant fields from ACBS
            if (value is ActorBaseSubrecord stats)
            {
                var isNpc = record is ReconstructedNpc;

                // Common fields (both NPC and Creature)
                var gender = (stats.Flags & 1) == 1 ? "Female" : "Male";
                properties.Add(new EsmPropertyEntry { Name = "Gender", Value = gender, Category = "Characteristics" });
                properties.Add(new EsmPropertyEntry
                    { Name = "Level", Value = stats.Level.ToString(), Category = "Attributes" });
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
                    Name = "Responsibility", Value = $"{ai.ResponsibilityName} ({ai.Responsibility})", Category = "AI"
                });
                continue;
            }

            // Special handling for SpecialStats (byte[7]) - S.P.E.C.I.A.L.
            if (prop.Name == "SpecialStats" && value is byte[] special && special.Length == 7)
            {
                var total = special.Sum(b => b);
                var formatted = $"{special[0]} ST, {special[1]} PE, {special[2]} EN, {special[3]} CH, " +
                                $"{special[4]} IN, {special[5]} AG, {special[6]} LK  (Total: {total})";
                properties.Add(new EsmPropertyEntry { Name = "S.P.E.C.I.A.L.", Value = formatted, Category = "Attributes" });
                continue;
            }

            // Special handling for Skills (byte[14])
            if (prop.Name == "Skills" && value is byte[] skills && skills.Length >= 13)
            {
                var skillNames = new[]
                {
                    "Barter", "Big Guns", "Energy Weapons", "Explosives", "Lockpick",
                    "Medicine", "Melee Weapons", "Repair", "Science", "Guns",
                    "Sneak", "Speech", "Survival", "Unarmed"
                };
                var subItems = new List<EsmPropertyEntry>();
                for (var i = 0; i < skills.Length && i < skillNames.Length; i++)
                {
                    if (i == 1) continue; // Skip BigGuns (index 1) - unused in Fallout NV
                    subItems.Add(new EsmPropertyEntry { Name = skillNames[i], Value = skills[i].ToString() });
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
        if (record is ReconstructedCreature crea)
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
        if (record is ReconstructedNpc npc && npc.SpecialStats?.Length >= 7 && npc.Stats != null)
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
                => "Characteristics",
            _ when name.StartsWith("FaceGen", StringComparison.Ordinal) => "Characteristics",

            // Attributes (stats and abilities)
            "Level" or "Fatigue" or "BarterGold" or "SpeedMultiplier" or "Karma" or "Disposition"
                or "Class" or "Template" or "SpecialStats" or "Skills"
                or "Weight" or "Value" or "Health" or "Damage" or "Speed" or "Reach"
                => "Attributes",

            // AI (behavior-related)
            "Aggression" or "Confidence" or "Mood" or "Assistance" or "EnergyLevel"
                or "Responsibility" or "CombatStyle" or "CombatStyleFormId"
                => "AI",

            // Associations (references to other records)
            "Factions" or "Spells" or "Inventory" or "Packages" => "Associations",

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
