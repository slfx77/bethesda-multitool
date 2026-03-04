using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils;

/// <summary>
///     Shared formatting and utility methods used by the ESM browser tree builder
///     and property builders.
/// </summary>
internal static class EsmPropertyFormatter
{
    /// <summary>
    ///     Known property names that represent FormID references but don't end with "FormId".
    /// </summary>
    internal static readonly HashSet<string> KnownFormIdFields = new(StringComparer.Ordinal)
    {
        "Race", "Class", "VoiceType", "DefaultHair", "Script",
        "CombatStyle", "DeathItem", "Template", "BaseSpell",
        "Hair", "Eyes", "HeadPart", "AttackRace"
    };

    /// <summary>
    ///     Property names to rename for display (e.g., "EyesFormId" -> "Eyes").
    /// </summary>
    internal static readonly Dictionary<string, string> PropertyDisplayNames = new(StringComparer.Ordinal)
    {
        ["EyesFormId"] = "Eyes",
        ["HairFormId"] = "Hair",
        ["HairColor"] = "Hair Color",
        ["CombatStyleFormId"] = "Combat Style"
    };

    /// <summary>
    ///     Category ordering for property display.
    /// </summary>
    internal static readonly string[] CategoryOrder =
    [
        "Identity", "Attributes", "Derived Stats", "Characteristics", "AI", "Associations", "References",
        "Statistics", "General", "Metadata"
    ];

    /// <summary>
    ///     Icons for sub-categories that differ from their parent category.
    ///     Uses Segoe MDL2 Assets glyphs where available.
    /// </summary>
    internal static readonly Dictionary<string, string> SubCategoryIcons = new(StringComparer.OrdinalIgnoreCase)
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
        ["Worldspaces"] = "\uE909", // Globe
        ["Cells"] = "\uE707", // MapPin
        ["Map Markers"] = "\uE707", // MapPin

        // AI sub-categories
        ["AI Packages"] = "\uE8AB" // Clock
    };

    /// <summary>
    ///     Maps (model type, property name) to flag bit definitions for centralized flag decoding.
    /// </summary>
    internal static readonly Dictionary<(Type, string), FlagBit[]> FlagLookup = new()
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
        [(typeof(FurnitureRecord), "MarkerFlags")] = FlagRegistry.FurnitureMarkerFlags
    };

    /// <summary>
    ///     Maps parsed record C# types to their 4-character ESM record signatures.
    /// </summary>
    internal static readonly Dictionary<Type, string> RecordTypeSignatures = new()
    {
        [typeof(NpcRecord)] = "NPC_",
        [typeof(CreatureRecord)] = "CREA",
        [typeof(RaceRecord)] = "RACE",
        [typeof(FactionRecord)] = "FACT",
        [typeof(ClassRecord)] = "CLAS",
        [typeof(QuestRecord)] = "QUST",
        [typeof(DialogueRecord)] = "INFO",
        [typeof(NoteRecord)] = "NOTE",
        [typeof(BookRecord)] = "BOOK",
        [typeof(TerminalRecord)] = "TERM",
        [typeof(WeaponRecord)] = "WEAP",
        [typeof(ArmorRecord)] = "ARMO",
        [typeof(AmmoRecord)] = "AMMO",
        [typeof(ConsumableRecord)] = "ALCH",
        [typeof(MiscItemRecord)] = "MISC",
        [typeof(KeyRecord)] = "KEYM",
        [typeof(ContainerRecord)] = "CONT",
        [typeof(PerkRecord)] = "PERK",
        [typeof(SpellRecord)] = "SPEL",
        [typeof(CellRecord)] = "CELL",
        [typeof(WorldspaceRecord)] = "WRLD",
        [typeof(GameSettingRecord)] = "GMST",
        [typeof(GlobalRecord)] = "GLOB",
        [typeof(EnchantmentRecord)] = "ENCH",
        [typeof(BaseEffectRecord)] = "MGEF",
        [typeof(WeaponModRecord)] = "IMOD",
        [typeof(RecipeRecord)] = "RCPE",
        [typeof(ChallengeRecord)] = "CHAL",
        [typeof(ReputationRecord)] = "REPU",
        [typeof(ProjectileRecord)] = "PROJ",
        [typeof(ExplosionRecord)] = "EXPL",
        [typeof(MessageRecord)] = "MESG",
        [typeof(LeveledListRecord)] = "LVLI",
        [typeof(FormListRecord)] = "FLST",
        [typeof(ActivatorRecord)] = "ACTI",
        [typeof(LightRecord)] = "LIGH",
        [typeof(DoorRecord)] = "DOOR",
        [typeof(StaticRecord)] = "STAT",
        [typeof(FurnitureRecord)] = "FURN",
        [typeof(ScriptRecord)] = "SCPT",
        [typeof(PackageRecord)] = "PACK",
        [typeof(SoundRecord)] = "SOUN",
        [typeof(TextureSetRecord)] = "TXST",
        [typeof(ArmaRecord)] = "ARMA",
        [typeof(WaterRecord)] = "WATR",
        [typeof(BodyPartDataRecord)] = "BPTD",
        [typeof(ActorValueInfoRecord)] = "AVIF",
        [typeof(CombatStyleRecord)] = "CSTY",
        [typeof(LightingTemplateRecord)] = "LGTM",
        [typeof(NavMeshRecord)] = "NAVM",
        [typeof(WeatherRecord)] = "WTHR"
    };

    /// <summary>
    ///     Fallout NV skill names indexed by skill ID (0-based).
    ///     Actor value codes 32-45 map to indices 0-13.
    ///     Delegates to the shared fallback array in FormIdResolver (single source of truth).
    /// </summary>
    internal static string[] SkillNames => FormIdResolver.FallbackSkillNames;

    /// <summary>
    ///     Cache for PropertyInfo[] by type - avoids repeated GetProperties() calls.
    /// </summary>
    internal static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();

    /// <summary>
    ///     Cache for named property lookups - avoids repeated GetProperty(name) calls.
    /// </summary>
    internal static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> NamedPropertyCache = new();

    internal static readonly string[] LocationTypeNames =
    [
        "Near Reference", "In Cell", "Near Current", "Near Editor",
        "Object ID", "Object Type", "Near Linked Reference", "At Package Location",
        "", "", "", "",
        "Near Linked Ref"
    ];

    /// <summary>
    ///     Maps an actor value code to a skill name.
    ///     AV codes 32-45 map to skills; returns null for non-skill AVs.
    /// </summary>
    internal static string? ActorValueToSkillName(int avCode)
    {
        var idx = avCode - 32;
        return idx >= 0 && idx < SkillNames.Length ? SkillNames[idx] : null;
    }

    /// <summary>
    ///     Gets cached PropertyInfo[] for a type.
    /// </summary>
    internal static PropertyInfo[] GetCachedProperties(Type type) =>
        PropertyCache.GetOrAdd(type, t => t.GetProperties());

    /// <summary>
    ///     Gets a cached named property for a type.
    /// </summary>
    internal static PropertyInfo? GetCachedProperty(Type type, string name) =>
        NamedPropertyCache.GetOrAdd((type, name), key => key.Item1.GetProperty(key.Item2));

    internal static (uint FormId, string? EditorId, string? FullName, long Offset) ExtractRecordIdentity(object record)
    {
        var type = record.GetType();
        var formId = (uint)(GetCachedProperty(type, "FormId")?.GetValue(record) ?? 0u);
        var editorId = GetCachedProperty(type, "EditorId")?.GetValue(record) as string;
        var fullName = GetCachedProperty(type, "FullName")?.GetValue(record) as string;
        var offset = (long)(GetCachedProperty(type, "Offset")?.GetValue(record) ?? 0L);
        return (formId, editorId, fullName, offset);
    }

    /// <summary>
    ///     Converts CamelCase property names to "Title Case" for display,
    ///     with proper handling of common acronyms and custom renames.
    /// </summary>
    internal static string FormatPropertyName(string name)
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

    internal static string? FormatPropertyValue(
        string name,
        object? value,
        Type propertyType,
        FormIdResolver? resolver = null)
    {
        // Show FullName even when empty (important for creatures without display names)
        if (name == "FullName")
        {
            var str = value as string;
            return string.IsNullOrEmpty(str) ? "\u2014" : str;
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

        // Format packed HCLR hair color as #RRGGBB (R, G, B)
        if (name == "HairColor" && value is uint hclr)
        {
            return NpcRecord.FormatHairColor(hclr);
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

            var editorId = resolver?.GetEditorId(formId);
            var displayName = resolver?.GetDisplayName(formId);

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
    internal static string FormatFormIdReference(uint formId, string? editorId, string? displayName)
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

    internal static string FormatSchemaFieldValue(object? value)
    {
        return value switch
        {
            null => "null",
            uint u => $"0x{u:X8}",
            int i => i.ToString(),
            ushort us => us.ToString(),
            short s => s.ToString(),
            float f => $"{f:F4}",
            byte b => $"0x{b:X2}",
            sbyte sb => sb.ToString(),
            string str => str,
            byte[] bytes => $"[{bytes.Length} bytes]",
            _ => value.ToString() ?? ""
        };
    }

    internal static string CategorizeProperty(string name)
    {
        return name switch
        {
            // Identity (minimal)
            "FormId" or "EditorId" or "FullName" => "Identity",
            "Offset" or "IsBigEndian" => "Metadata",

            // Characteristics (appearance-related)
            "Gender" or "Race" or "OriginalRace" or "VoiceType" or "Eyes" or "EyesFormId"
                or "Hair" or "HairFormId" or "HairLength" or "HairColor"
                or "MaleHeight" or "FemaleHeight" or "MaleWeight" or "FemaleWeight"
                or "Height" or "BloodImpactMaterial" or "RaceFacePreset"
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
                or "RelatedNpcFormIds" or "Variables" or "FaceNpc"
                => "Associations",

            // References (other FormID fields)
            _ when name.EndsWith("FormId", StringComparison.Ordinal) => "References",
            _ when KnownFormIdFields.Contains(name) => "References",
            _ when name.EndsWith("Sound", StringComparison.Ordinal) => "References",

            _ => "General"
        };
    }

    /// <summary>
    ///     Builds display name for dialogue (INFO) records.
    ///     Priority: response text > prompt text > quest-topic names > FormID.
    /// </summary>
    internal static string BuildDialogueDisplayName(
        DialogueRecord dialogue,
        string formIdHex,
        FormIdResolver? resolver)
    {
        var responseText = dialogue.Responses.FirstOrDefault()?.Text;
        if (!string.IsNullOrEmpty(responseText))
        {
            return responseText;
        }

        if (!string.IsNullOrEmpty(dialogue.PromptText))
        {
            return dialogue.PromptText;
        }

        var questName = ResolveEditorIdOrName(dialogue.QuestFormId, resolver);
        var topicName = ResolveEditorIdOrName(dialogue.TopicFormId, resolver);
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
    internal static string? BuildDialogueDetail(
        DialogueRecord dialogue,
        string formIdHex,
        FormIdResolver? resolver)
    {
        var hasTextContent = dialogue.Responses.Any(r => !string.IsNullOrEmpty(r.Text))
                             || !string.IsNullOrEmpty(dialogue.PromptText);

        if (!hasTextContent)
        {
            return formIdHex;
        }

        var questId = dialogue.QuestFormId is > 0
            ? resolver?.GetEditorId(dialogue.QuestFormId.Value)
            : null;
        var topicId = dialogue.TopicFormId is > 0
            ? resolver?.GetEditorId(dialogue.TopicFormId.Value)
            : null;

        if (questId != null || topicId != null)
        {
            var parts = new[] { questId, topicId }.Where(p => p != null);
            return $"({string.Join(" - ", parts)})";
        }

        return formIdHex;
    }

    internal static string? ResolveEditorIdOrName(
        uint? formId,
        FormIdResolver? resolver)
    {
        if (formId is not > 0) return null;
        return resolver?.GetEditorId(formId.Value)
               ?? resolver?.GetDisplayName(formId.Value);
    }

    /// <summary>
    ///     Tries to process a common property (flags, fields dict, lists, FormID refs, default values).
    ///     Returns true if the property was handled, false if it needs record-specific processing.
    /// </summary>
    internal static bool TryAddCommonProperty(
        List<EsmPropertyEntry> properties,
        PropertyInfo prop,
        object? value,
        string displayName,
        Type recordType,
        FormIdResolver? resolver)
    {
        // Centralized flag decoding
        if (FlagLookup.TryGetValue((recordType, prop.Name), out var flagDefs))
        {
            var flagValue = Convert.ToUInt32(value);
            properties.Add(new EsmPropertyEntry
            {
                Name = displayName,
                Value = FlagRegistry.DecodeFlagNamesWithHex(flagValue, flagDefs),
                Category = CategorizeProperty(prop.Name)
            });
            return true;
        }

        // Handle GenericEsmRecord Fields dictionary
        if (value is Dictionary<string, object?> fieldsDict && fieldsDict.Count > 0)
        {
            EsmItemPropertyBuilder.AddFieldsDictionary(properties, fieldsDict);
            return true;
        }

        // Handle expandable list properties (skip empty lists)
        if (value is IList list)
        {
            if (list.Count == 0)
            {
                return true; // Skip but mark as handled
            }

            EsmItemPropertyBuilder.AddListProperty(properties, displayName, prop.Name, list, resolver);
            return true;
        }

        // Check if this is a FormID reference field that should use column layout
        var isFormIdField = (prop.PropertyType == typeof(uint) || prop.PropertyType == typeof(uint?)) &&
                            (prop.Name.EndsWith("FormId", StringComparison.Ordinal) ||
                             KnownFormIdFields.Contains(prop.Name)) &&
                            prop.Name != "FormId";

        if (isFormIdField && value is uint formIdVal && formIdVal != 0)
        {
            var editorId = resolver?.GetEditorId(formIdVal);
            var fullName = resolver?.GetDisplayName(formIdVal);

            properties.Add(new EsmPropertyEntry
            {
                Name = displayName,
                Value = FormatFormIdReference(formIdVal, editorId, fullName),
                Category = CategorizeProperty(prop.Name),
                LinkedFormId = formIdVal
            });
            return true;
        }

        // Default value formatting
        var valueStr = FormatPropertyValue(prop.Name, value, prop.PropertyType, resolver);
        if (valueStr == null)
        {
            return true; // Null means skip, but mark as handled
        }

        properties.Add(new EsmPropertyEntry
        {
            Name = displayName,
            Value = valueStr,
            Category = CategorizeProperty(prop.Name)
        });
        return true;
    }
}
