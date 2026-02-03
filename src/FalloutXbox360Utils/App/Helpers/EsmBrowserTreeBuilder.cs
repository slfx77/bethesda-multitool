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
internal static class EsmBrowserTreeBuilder
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
    ///     FaceGen Geometry Symmetric morph labels from GECK (50 slots).
    /// </summary>
    private static readonly string[] FaceGenGeometryLabels =
    [
        "Brow Ridge - high / low",
        "Brow Ridge Inner - up / down",
        "Brow Ridge Outer - up / down",
        "Cheekbones - low / high",
        "Cheekbones - shallow / pronounced",
        "Cheekbones - thin / wide",
        "Cheeks - concave / convex",
        "Cheeks - round / gaunt",
        "Chin - forward / backward",
        "Chin - pronounced / recessed",
        "Chin - retracted / jutting",
        "Chin - shallow / deep",
        "Chin - small / large",
        "Chin - tall / short",
        "Chin - wide / thin",
        "Eyes - down / up",
        "Eyes - small / large",
        "Eyes - tilt inward / outward",
        "Eyes - together / apart",
        "Face - brow-nose-chin ratio",
        "Face - forehead-sellion-nose ratio",
        "Face - heavy / light",
        "Face - round / gaunt",
        "Face - thin / wide",
        "Forehead - small / large",
        "Forehead - tall / short",
        "Forehead - tilt forward / back",
        "Jaw - retracted / jutting",
        "Jaw - wide / thin",
        "Jaw-Neck slope high / low",
        "Jawline - concave / convex",
        "Mouth - drawn / pursed",
        "Mouth - happy / sad",
        "Mouth - high / low",
        "Mouth - Lips deflated / inflated",
        "Mouth - Lips large / small",
        "Mouth - lips puckered / retracted",
        "Mouth - protruding / retracted",
        "Mouth - tilt up / down",
        "Mouth - underbite / overbite",
        "Mouth-Chin distance - short / long",
        "Nose - bridge shallow / deep",
        "Nose - bridge short / long",
        "Nose - down / up",
        "Nose - flat / pointed",
        "Nose - nostril tilt down / up",
        "Nose - nostrils small / large",
        "Nose - nostrils wide / thin",
        "Nose - region concave / convex",
        "Nose - sellion down / up"
    ];

    /// <summary>
    ///     FaceGen Geometry Asymmetric morph labels from GECK (30 slots).
    /// </summary>
    private static readonly string[] FaceGenAsymmetryLabels =
    [
        "Brow Ridge - forward axis twist",
        "Cheekbones - protrusion asymmetry",
        "Chin - chin axis twist",
        "Chin - forward axis twist",
        "Chin - transverse shift",
        "Eyes - height disparity",
        "Eyes - transverse shift",
        "Face - coronal bend",
        "Face - coronal shear",
        "Face - vertical axis twist",
        "Forehead - forward axis twist",
        "Mouth - corners transverse shift",
        "Mouth - forward axis twist",
        "Mouth - transverse shift",
        "Mouth - twist and shift",
        "Mouth-Nose - coronal shear",
        "Mouth-Nose - transverse shift",
        "Nose - bridge transverse shift",
        "Nose - frontal axis twist",
        "Nose - sellion transverse shift",
        "Nose - tip transverse shift",
        "Nose - transverse shift",
        "Nose - vertical axis twist",
        "Nose Region - frontal axis twist",
        "Nostrils - frontal axis twist",
        "Asymmetric 26",
        "Asymmetric 27",
        "Asymmetric 28",
        "Asymmetric 29",
        "Asymmetric 30"
    ];

    /// <summary>
    ///     FaceGen Texture Symmetric morph labels from GECK (50 slots).
    /// </summary>
    private static readonly string[] FaceGenTextureLabels =
    [
        "Beard Flushed / Pale",
        "Beard Light / Dark",
        "Beard Cheeks Light / Dark",
        "Beard Circle Light / Dark",
        "Beard Goatee Light / Dark",
        "Beard Moustache Light / Dark",
        "Cheek Blush Light / Red",
        "Eye Sockets Bruised / Bright",
        "Eye Sockets Dark / Light",
        "Eyebrows Dark / Light",
        "Eyebrows Low / High",
        "Eyebrows Thick / Thin",
        "Eyebrows Very Thin / Thick",
        "Eyebrows Lower Light / Dark",
        "Eyebrows Outer Light / Dark",
        "Eyebrows Upper Dark / Light",
        "Eyelids Light / Dark",
        "Eyelids Pale / Red",
        "Eyeliner Light / Dark",
        "Eyeshadow Light / Dark",
        "Eyes Dark Brown / Light Blue",
        "Eyes Whites Dim / Bright",
        "Lips Flushed / Pale",
        "Lipstick Dark Red / Light Blue",
        "Lipstick Dark Blue / Light Red",
        "Naso Labial Lines Light / Dark",
        "Nares Small / Large",
        "Nose Pale / Red",
        "Skin Flushed / Pale",
        "Skin Shade Dark / Light",
        "Skin Tint Orange / Blue",
        "Skin Tint Purple / Yellow",
        "Texture 33",
        "Texture 34",
        "Texture 35",
        "Texture 36",
        "Texture 37",
        "Texture 38",
        "Texture 39",
        "Texture 40",
        "Texture 41",
        "Texture 42",
        "Texture 43",
        "Texture 44",
        "Texture 45",
        "Texture 46",
        "Texture 47",
        "Texture 48",
        "Texture 49",
        "Texture 50"
    ];

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
        Dictionary<uint, string>? lookup = null)
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
                Properties = BuildProperties(record, lookup)
            };

            typeNode.Children.Add(recordNode);
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
        Dictionary<uint, string>? lookup = null)
    {
        var properties = new List<EsmPropertyEntry>();
        var type = record.GetType();

        foreach (var prop in GetCachedProperties(type))
        {
            if (!prop.CanRead)
            {
                continue;
            }

            var value = prop.GetValue(record);
            var displayName = FormatPropertyName(prop.Name);

            // Special handling for ActorBaseSubrecord (Stats)
            if (value is ActorBaseSubrecord stats)
            {
                var gender = (stats.Flags & 1) == 1 ? "Female" : "Male";
                properties.Add(new EsmPropertyEntry { Name = "Gender", Value = gender, Category = "Stats" });
                properties.Add(new EsmPropertyEntry
                    { Name = "Level", Value = stats.Level.ToString(), Category = "Stats" });
                properties.Add(new EsmPropertyEntry
                    { Name = "Fatigue", Value = stats.FatigueBase.ToString(), Category = "Stats" });
                properties.Add(new EsmPropertyEntry
                    { Name = "Barter Gold", Value = stats.BarterGold.ToString(), Category = "Stats" });
                properties.Add(new EsmPropertyEntry
                    { Name = "Speed Multiplier", Value = $"{stats.SpeedMultiplier}%", Category = "Stats" });
                properties.Add(new EsmPropertyEntry
                    { Name = "Karma", Value = $"{stats.KarmaAlignment:F2}", Category = "Stats" });
                properties.Add(new EsmPropertyEntry
                    { Name = "Disposition", Value = stats.DispositionBase.ToString(), Category = "Stats" });
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
                properties.Add(new EsmPropertyEntry { Name = "S.P.E.C.I.A.L.", Value = formatted, Category = "Stats" });
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
                    Category = "Stats",
                    IsExpandable = true,
                    SubItems = subItems
                });
                continue;
            }

            // Special handling for FaceGen float arrays with GECK morph labels
            if (prop.Name.StartsWith("FaceGen", StringComparison.Ordinal) && value is float[] morphs &&
                morphs.Length > 0)
            {
                var labels = GetFaceGenLabels(prop.Name, morphs.Length);
                var subItems = new List<EsmPropertyEntry>();
                for (var i = 0; i < morphs.Length; i++)
                {
                    subItems.Add(new EsmPropertyEntry
                    {
                        Name = labels[i],
                        Value = $"{morphs[i]:F3}"
                    });
                }

                properties.Add(new EsmPropertyEntry
                {
                    Name = displayName,
                    Value = $"{morphs.Length} morphs",
                    Category = "FaceGen",
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
                    subItems.Add(new EsmPropertyEntry
                    {
                        Name = $"[{subItems.Count}]",
                        Value = FormatListItem(item, lookup) ?? item?.ToString() ?? ""
                    });
                }

                properties.Add(new EsmPropertyEntry
                {
                    Name = displayName,
                    Value = $"{list.Count} items",
                    Category = CategorizeProperty(prop.Name),
                    IsExpandable = true,
                    SubItems = subItems
                });
                continue;
            }

            var valueStr = FormatPropertyValue(prop.Name, value, prop.PropertyType, lookup);
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

        return properties;
    }

    /// <summary>
    ///     Converts CamelCase property names to "Title Case" for display,
    ///     with proper handling of common acronyms.
    /// </summary>
    private static string FormatPropertyName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

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

    private static string? FormatListItem(object? item, Dictionary<uint, string>? lookup)
    {
        if (item == null)
        {
            return null;
        }

        // Special handling for FactionMembership
        if (item is FactionMembership faction)
        {
            var factionName = lookup?.GetValueOrDefault(faction.FactionFormId);
            return factionName != null
                ? $"{factionName} — Rank {faction.Rank}"
                : $"0x{faction.FactionFormId:X8} — Rank {faction.Rank}";
        }

        // Special handling for InventoryItem
        if (item is InventoryItem inv)
        {
            var itemName = lookup?.GetValueOrDefault(inv.ItemFormId);
            return itemName != null
                ? $"{itemName} × {inv.Count}"
                : $"0x{inv.ItemFormId:X8} × {inv.Count}";
        }

        // For uint items that might be FormIDs (Spells, Packages, etc.)
        if (item is uint formId && formId != 0)
        {
            var resolved = lookup?.GetValueOrDefault(formId);
            return resolved != null ? $"{resolved}" : $"0x{formId:X8}";
        }

        // For complex objects, format their properties inline
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
                        var resolved = lookup?.GetValueOrDefault(fid);
                        parts.Add(resolved != null
                            ? $"{FormatPropertyName(prop.Name)}: {resolved}"
                            : $"{FormatPropertyName(prop.Name)}: 0x{fid:X8}");
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

                return string.Join(", ", parts);
            }
        }

        return item.ToString();
    }

    private static string? FormatPropertyValue(
        string name,
        object? value,
        Type propertyType,
        Dictionary<uint, string>? lookup)
    {
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

        // Format FormID references with lookup (named *FormId)
        if (propertyType == typeof(uint) && name.EndsWith("FormId", StringComparison.Ordinal))
        {
            var formId = (uint)value;
            var resolved = lookup?.GetValueOrDefault(formId);
            return resolved != null
                ? $"0x{formId:X8} ({resolved})"
                : $"0x{formId:X8}";
        }

        if (propertyType == typeof(uint?) && name.EndsWith("FormId", StringComparison.Ordinal))
        {
            var formId = (uint?)value;
            if (!formId.HasValue)
            {
                return null;
            }

            var resolved = lookup?.GetValueOrDefault(formId.Value);
            return resolved != null
                ? $"0x{formId.Value:X8} ({resolved})"
                : $"0x{formId.Value:X8}";
        }

        // Format uint as hex for FormId
        if (name == "FormId" && value is uint fid)
        {
            var resolved = lookup?.GetValueOrDefault(fid);
            return resolved != null
                ? $"0x{fid:X8} ({resolved})"
                : $"0x{fid:X8}";
        }

        // Known FormID fields that don't end with "FormId" (non-nullable)
        if (propertyType == typeof(uint) && KnownFormIdFields.Contains(name))
        {
            var formId = (uint)value;
            if (formId == 0)
            {
                return null;
            }

            var resolved = lookup?.GetValueOrDefault(formId);
            return resolved != null
                ? $"0x{formId:X8} ({resolved})"
                : $"0x{formId:X8}";
        }

        // Known FormID fields that don't end with "FormId" (nullable)
        if (propertyType == typeof(uint?) && KnownFormIdFields.Contains(name))
        {
            var formId = (uint?)value;
            if (!formId.HasValue || formId.Value == 0)
            {
                return null;
            }

            var resolved = lookup?.GetValueOrDefault(formId.Value);
            return resolved != null
                ? $"0x{formId.Value:X8} ({resolved})"
                : $"0x{formId.Value:X8}";
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
    ///     Gets the appropriate FaceGen morph labels based on property name.
    /// </summary>
    private static string[] GetFaceGenLabels(string propertyName, int count)
    {
        var labels = propertyName switch
        {
            "FaceGenGeometrySymmetric" => FaceGenGeometryLabels,
            "FaceGenGeometryAsymmetric" => FaceGenAsymmetryLabels,
            "FaceGenTextureSymmetric" => FaceGenTextureLabels,
            _ => null
        };

        if (labels == null)
        {
            return Enumerable.Range(1, count).Select(i => $"Morph {i}").ToArray();
        }

        // Return labels up to count, filling any extras with indexed names
        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = i < labels.Length ? labels[i] : $"Morph {i + 1}";
        }

        return result;
    }

    private static string CategorizeProperty(string name)
    {
        return name switch
        {
            "FormId" or "EditorId" or "FullName" => "Identity",
            "Offset" or "IsBigEndian" => "Metadata",
            "Weight" or "Value" or "Health" or "Damage" or "Speed" or "Reach" => "Stats",
            "Factions" or "Spells" or "Inventory" or "Packages" => "Associations",
            "HairLength" => "Appearance",
            _ when name.StartsWith("FaceGen", StringComparison.Ordinal) => "FaceGen",
            _ when name.EndsWith("FormId", StringComparison.Ordinal) => "References",
            _ when KnownFormIdFields.Contains(name) => "References",
            _ when name.EndsWith("Sound", StringComparison.Ordinal) => "Audio",
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
