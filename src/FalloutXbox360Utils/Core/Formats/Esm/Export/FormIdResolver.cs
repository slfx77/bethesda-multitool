namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Unified FormID resolution: wraps EditorID + DisplayName + Ref→Base dictionaries
///     into a single object for consistent name resolution across reports and GUI.
///     Also provides actor value name resolution from parsed AVIF records.
/// </summary>
public sealed class FormIdResolver
{
    /// <summary>
    ///     Actor value display names indexed by AV code (0-76+), built from AVIF records.
    ///     Null when no AVIF data is available (e.g., DMP-only analysis).
    /// </summary>
    private readonly string[]? _actorValueNames;

    /// <summary>Backward-compatible constructor (no Ref→Base data).</summary>
    public FormIdResolver(
        Dictionary<uint, string> editorIds,
        Dictionary<uint, string> displayNames)
        : this(editorIds, displayNames, [], null)
    {
    }

    public FormIdResolver(
        Dictionary<uint, string> editorIds,
        Dictionary<uint, string> displayNames,
        Dictionary<uint, uint> refToBase,
        string[]? actorValueNames = null)
    {
        EditorIds = editorIds;
        DisplayNames = displayNames;
        RefToBase = refToBase;
        _actorValueNames = actorValueNames;
    }

    /// <summary>An empty resolver for contexts where no data is available.</summary>
    public static FormIdResolver Empty { get; } = new([], [], []);

    /// <summary>The underlying EditorID dictionary.</summary>
    public Dictionary<uint, string> EditorIds { get; }

    /// <summary>The underlying DisplayName dictionary.</summary>
    public Dictionary<uint, string> DisplayNames { get; }

    /// <summary>Reference FormID → Base object FormID mapping (REFR/ACHR/ACRE → base record).</summary>
    public Dictionary<uint, uint> RefToBase { get; }

    #region Merging

    /// <summary>
    ///     Creates a new resolver that merges this resolver with a fallback.
    ///     Entries from this resolver take precedence; the fallback fills gaps.
    /// </summary>
    public FormIdResolver MergeWith(FormIdResolver fallback)
    {
        var mergedEditorIds = new Dictionary<uint, string>(fallback.EditorIds);
        foreach (var (k, v) in EditorIds)
            mergedEditorIds[k] = v;

        var mergedDisplayNames = new Dictionary<uint, string>(fallback.DisplayNames);
        foreach (var (k, v) in DisplayNames)
            mergedDisplayNames[k] = v;

        var mergedRefToBase = new Dictionary<uint, uint>(fallback.RefToBase);
        foreach (var (k, v) in RefToBase)
            mergedRefToBase[k] = v;

        return new FormIdResolver(mergedEditorIds, mergedDisplayNames, mergedRefToBase,
            _actorValueNames ?? fallback._actorValueNames);
    }

    #endregion

    #region Actor Value Resolution

    /// <summary>
    ///     Fallback skill names (AV codes 32-45) for when AVIF data is not available.
    ///     Matches the canonical FNV ActorValue enum ordering.
    /// </summary>
    internal static readonly string[] FallbackSkillNames =
    [
        "Barter", "Big Guns", "Energy Weapons", "Explosives", "Lockpick",
        "Medicine", "Melee Weapons", "Repair", "Science", "Guns",
        "Sneak", "Speech", "Survival", "Unarmed"
    ];

    /// <summary>
    ///     Maps AVIF EditorIDs to their ActorValue enum codes (0-76).
    ///     EditorIDs use Fallout 3 heritage naming (AVSmallGuns, AVThrowing)
    ///     while display names use FNV naming (Guns, Survival).
    ///     Used to correctly position runtime AVIF records from DMP files,
    ///     where records arrive in arbitrary memory scan order.
    /// </summary>
    internal static readonly Dictionary<string, int> AvifEditorIdToAvCode = new(StringComparer.OrdinalIgnoreCase)
    {
        // AI attributes (0-4)
        ["AVAggression"] = 0, ["AVConfidence"] = 1, ["AVEnergy"] = 2,
        ["AVResponsibility"] = 3, ["AVMood"] = 4,
        // SPECIAL (5-11)
        ["AVStrength"] = 5, ["AVPerception"] = 6, ["AVEndurance"] = 7,
        ["AVCharisma"] = 8, ["AVIntelligence"] = 9, ["AVAgility"] = 10, ["AVLuck"] = 11,
        // Derived stats (12-31)
        ["AVActionPoints"] = 12, ["AVCarryWeight"] = 13, ["AVCritChance"] = 14,
        ["AVHealRate"] = 15, ["AVHealth"] = 16, ["AVMeleeDamage"] = 17,
        ["AVDamageResist"] = 18, ["AVPoisonResist"] = 19, ["AVRadResist"] = 20,
        ["AVSpeedMult"] = 21, ["AVFatigue"] = 22, ["AVKarma"] = 23,
        ["AVXP"] = 24,
        ["AVPerceptionCondition"] = 25, ["AVEnduranceCondition"] = 26,
        ["AVLeftAttackCondition"] = 27, ["AVRightAttackCondition"] = 28,
        ["AVLeftMobilityCondition"] = 29, ["AVRightMobilityCondition"] = 30,
        ["AVBrainCondition"] = 31,
        // Skills (32-45)
        ["AVBarter"] = 32, ["AVBigGuns"] = 33, ["AVEnergyWeapons"] = 34,
        ["AVExplosives"] = 35, ["AVLockpick"] = 36, ["AVMedicine"] = 37,
        ["AVMeleeWeapons"] = 38, ["AVRepair"] = 39, ["AVScience"] = 40,
        ["AVSmallGuns"] = 41, ["AVSneak"] = 42, ["AVSpeech"] = 43,
        ["AVThrowing"] = 44, ["AVUnarmed"] = 45,
        // Misc (46-76)
        ["AVInventoryWeight"] = 46, ["AVParalysis"] = 47, ["AVInvisibility"] = 48,
        ["AVChameleon"] = 49, ["AVNightEye"] = 50, ["AVTurbo"] = 51,
        ["AVFireResist"] = 52, ["AVWaterBreathing"] = 53, ["AVRadiationRads"] = 54,
        ["AVBloodyMess"] = 55, ["AVUnarmedDamage"] = 56, ["AVAssistance"] = 57,
        ["AVElectricResist"] = 58, ["AVFrostResist"] = 59, ["AVEnergyResist"] = 60,
        ["AVEMPResist"] = 61,
        ["AVVariable01"] = 62, ["AVVariable02"] = 63, ["AVVariable03"] = 64,
        ["AVVariable04"] = 65, ["AVVariable05"] = 66, ["AVVariable06"] = 67,
        ["AVVariable07"] = 68, ["AVVariable08"] = 69, ["AVVariable09"] = 70,
        ["AVVariable10"] = 71,
        ["AVIgnoreCrippledLimbs"] = 72, ["AVDehydration"] = 73,
        ["AVHunger"] = 74, ["AVSleepDeprivation"] = 75, ["AVDamageThreshold"] = 76
    };

    /// <summary>
    ///     Returns the display name for an actor value code (from AVIF records), or null.
    ///     AV codes 0-4 = AI attributes, 5-11 = SPECIAL, 12-31 = derived stats, 32-45 = skills, 46+ = misc.
    /// </summary>
    public string? GetActorValueName(int avCode)
    {
        if (_actorValueNames != null && avCode >= 0 && avCode < _actorValueNames.Length
            && _actorValueNames[avCode] != null)
        {
            return _actorValueNames[avCode];
        }

        // Fallback to hardcoded skill names for AV codes 32-45
        var skillIdx = avCode - 32;
        if (skillIdx >= 0 && skillIdx < FallbackSkillNames.Length)
        {
            return FallbackSkillNames[skillIdx];
        }

        return null;
    }

    /// <summary>
    ///     Returns the display name for a skill by its index within the 14-slot skill array (DNAM).
    ///     Skill index 0 = AV 32 (Barter), index 13 = AV 45 (Unarmed).
    /// </summary>
    public string? GetSkillName(int skillIndex)
    {
        return GetActorValueName(skillIndex + 32);
    }

    #endregion

    #region Core Lookups

    /// <summary>Returns the EditorID for a FormID, or null.</summary>
    public string? GetEditorId(uint formId)
    {
        return EditorIds.GetValueOrDefault(formId);
    }

    /// <summary>Returns the display name for a FormID, or null.</summary>
    public string? GetDisplayName(uint formId)
    {
        return DisplayNames.GetValueOrDefault(formId);
    }

    /// <summary>Returns the best available name: DisplayName > EditorID > null.</summary>
    public string? GetBestName(uint formId)
    {
        return DisplayNames.GetValueOrDefault(formId) ?? EditorIds.GetValueOrDefault(formId);
    }

    #endregion

    #region Reference Resolution

    /// <summary>Returns the base object FormID for a placed reference, or null if not a reference.</summary>
    public uint? GetBaseFormId(uint refFormId)
    {
        return RefToBase.TryGetValue(refFormId, out var baseId) ? baseId : null;
    }

    /// <summary>Returns true if the given FormID is a placed reference (REFR/ACHR/ACRE).</summary>
    public bool IsReference(uint formId)
    {
        return RefToBase.ContainsKey(formId);
    }

    /// <summary>
    ///     Returns the best name, chaining through Ref→Base if direct lookup fails.
    ///     For a reference with no direct name, resolves the base object's name instead.
    /// </summary>
    public string? GetBestNameWithRefChain(uint formId)
    {
        var directName = GetBestName(formId);
        if (directName != null)
        {
            return directName;
        }

        if (RefToBase.TryGetValue(formId, out var baseFormId))
        {
            return GetBestName(baseFormId);
        }

        return null;
    }

    #endregion

    #region Formatting

    /// <summary>Formats "EditorId (0x00123456)" or just "0x00123456".</summary>
    public string FormatWithEditorId(uint formId)
    {
        return Fmt.FIdWithName(formId, EditorIds);
    }

    /// <summary>
    ///     Formats "DisplayName - EditorId (0xFormID)" with all available info.
    ///     Falls back to fewer fields when data is missing.
    /// </summary>
    public string FormatFull(uint formId)
    {
        var editorId = EditorIds.GetValueOrDefault(formId);
        var displayName = DisplayNames.GetValueOrDefault(formId);

        if (editorId != null && displayName != null)
        {
            return $"{displayName} - {editorId} ({Fmt.FIdAlways(formId)})";
        }

        if (editorId != null)
        {
            return $"{editorId} ({Fmt.FIdAlways(formId)})";
        }

        if (displayName != null)
        {
            return $"{displayName} ({Fmt.FIdAlways(formId)})";
        }

        return Fmt.FIdAlways(formId);
    }

    /// <summary>Resolves EditorID or falls back to formatted FormID hex.</summary>
    public string ResolveEditorId(uint formId)
    {
        return EditorIds.TryGetValue(formId, out var name) ? name : Fmt.FIdAlways(formId);
    }

    /// <summary>Resolves display name or falls back to "(none)".</summary>
    public string ResolveDisplayName(uint formId)
    {
        return DisplayNames.TryGetValue(formId, out var name) ? name : "(none)";
    }

    /// <summary>Resolves EditorID for CSV: EditorID or empty string.</summary>
    public string ResolveCsv(uint formId)
    {
        return Fmt.Resolve(formId, EditorIds);
    }

    /// <summary>Resolves display name for CSV: display name or empty string.</summary>
    public string ResolveDisplayNameCsv(uint formId)
    {
        return formId != 0 && DisplayNames.TryGetValue(formId, out var name) ? name : "";
    }

    #endregion
}
