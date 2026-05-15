using System.Text.RegularExpressions;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Sanity-check rules for <see cref="ReportField" /> values.
///     Matching is layered — first-hit wins:
///     <list type="number">
///         <item>Explicit <c>(RecordType, Section, Field)</c> rule.</item>
///         <item>
///             Section-wildcard rule (<c>(RecordType, sectionGlob)</c>, covers repeating
///             structures like <c>Contents (0)</c> / <c>Contents (1)</c>).
///         </item>
///         <item>
///             Field-suffix family — the last whitespace-separated token of the field
///             name maps to a family rule (<c>*Path</c>→string, <c>*Count</c>→int≥0, etc.).
///         </item>
///         <item>
///             Sample-shape classifier — regex on the value's pre-formatted display
///             string infers the expected shape (int/float/hex/bool/path/resolved-formid).
///         </item>
///     </list>
///     Tuples that miss all four layers become <see cref="UnknownKey" /> entries and
///     are surfaced to the CLI so the rule corpus grows from real data, not a one-shot audit.
/// </summary>
internal static class ReportFieldDomain
{
    private static readonly Dictionary<(string Record, string Section, string Field), IDomainRule> Rules =
        BuildRules();

    /// <summary>
    ///     Layer 2. Applied in order — first match wins. <c>RecordType="*"</c> matches any.
    ///     Section supports a trailing <c>*</c> wildcard (<c>"Contents *"</c> etc.).
    /// </summary>
    private static readonly List<(string RecordType, string SectionGlob, IDomainRule Rule)> SectionWildcardRules =
        BuildSectionWildcardRules();

    /// <summary>
    ///     Layer 3. Key = last whitespace-separated token of the field key (lowercase,
    ///     trimmed of surrounding parentheses). Hit covers patterns like
    ///     <c>Body Path</c>, <c>Clip Count</c>, <c>Damage Mult</c>.
    /// </summary>
    private static readonly Dictionary<string, IDomainRule> SuffixFamily = BuildSuffixFamily();

    /// <summary>
    ///     Layer 4. Regex on the field value's <c>Display</c> string → best-effort shape
    ///     rule. Tried in order; first match wins.
    /// </summary>
    private static readonly List<(Regex Pattern, IDomainRule Rule)> SampleShapeRules = BuildSampleShapeRules();

    /// <summary>
    ///     Total number of (RecordType, Section, Field) rules. Useful for diagnostics.
    /// </summary>
    internal static int RuleCount => Rules.Count;

    /// <summary>
    ///     Walk every field in the report and apply rules. Returns violations and unknown keys.
    /// </summary>
    internal static RecordEvaluation Evaluate(RecordReport report, FormIdResolver? resolver)
    {
        var result = new RecordEvaluation();
        foreach (var section in report.Sections)
        {
            foreach (var field in section.Fields)
            {
                EvaluateField(report, section.Name, field, resolver, result);
            }
        }

        return result;
    }

    private static void EvaluateField(
        RecordReport report,
        string sectionName,
        ReportField field,
        FormIdResolver? resolver,
        RecordEvaluation result)
    {
        // Recurse into composite values so nested fields (inventory rows, effects, etc.)
        // get their own (Section + outer key) namespace.
        if (field.Value is ReportValue.CompositeVal composite)
        {
            foreach (var nested in composite.Fields)
            {
                EvaluateField(report,
                    $"{sectionName}/{field.Key}",
                    nested,
                    resolver,
                    result);
            }

            return;
        }

        if (field.Value is ReportValue.ListVal list)
        {
            foreach (var item in list.Items)
            {
                if (item is ReportValue.CompositeVal innerComposite)
                {
                    foreach (var nested in innerComposite.Fields)
                    {
                        EvaluateField(report,
                            $"{sectionName}/{field.Key}",
                            nested,
                            resolver,
                            result);
                    }
                }
            }

            return;
        }

        // Layered match: explicit → section-wildcard → field-suffix family → sample-shape.
        var rule = ResolveRule(report.RecordType, sectionName, field);
        if (rule == null)
        {
            result.UnknownKeys.Add(new UnknownKey(
                report.RecordType,
                sectionName,
                field.Key,
                Truncate(field.Value.Display, 60)));
            return;
        }

        if (!rule.Matches(field.Value, resolver, out var reason))
        {
            result.Violations.Add(new DomainViolation(
                report.RecordType,
                report.FormId,
                report.EditorId,
                sectionName,
                field.Key,
                reason ?? "(no reason)",
                Truncate(field.Value.Display, 80)));
        }
    }

    /// <summary>First-hit-wins traversal of the matching layers. Null = unknown.</summary>
    private static IDomainRule? ResolveRule(string recordType, string sectionName, ReportField field)
    {
        // Layer 1 — explicit tuple.
        if (Rules.TryGetValue((recordType, sectionName, field.Key), out var explicitRule))
            return explicitRule;

        // Layer 2 — section wildcard (record-type-specific first, then wildcard record-type).
        foreach (var (rt, sectionGlob, rule) in SectionWildcardRules)
        {
            if ((rt == "*" || rt == recordType) && MatchesSectionGlob(sectionGlob, sectionName))
                return rule;
        }

        // Layer 3 — field-suffix family (last whitespace-separated token, lowercased).
        var suffix = LastToken(field.Key);
        if (suffix != null && SuffixFamily.TryGetValue(suffix, out var familyRule))
            return familyRule;

        // Layer 4 — sample-shape classifier on the pre-formatted display string.
        var display = field.Value.Display;
        foreach (var (pattern, rule) in SampleShapeRules)
        {
            if (pattern.IsMatch(display)) return rule;
        }

        // Layer 5 — catch-all for any formatted value with a non-empty Display string.
        // If the producer went through the typed-value pipeline and gave us a display,
        // we accept the value as-is and still reject NaN/Infinity floats. Keeps only
        // truly untyped / empty values in the unknown-keys bucket.
        if (!string.IsNullOrEmpty(field.Value.Display))
        {
            return new AnyNonNaN();
        }

        return null;
    }

    private static bool MatchesSectionGlob(string glob, string sectionName)
    {
        // Tiny glob: `Prefix*` matches any section starting with `Prefix`, else exact.
        if (glob.EndsWith('*'))
        {
            return sectionName.StartsWith(glob[..^1], StringComparison.Ordinal);
        }

        return string.Equals(glob, sectionName, StringComparison.Ordinal);
    }

    private static string? LastToken(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var spaceIdx = key.LastIndexOf(' ');
        var token = spaceIdx < 0 ? key : key[(spaceIdx + 1)..];
        // Strip surrounding punctuation like "(0)" → "0" before lowercasing.
        token = token.Trim('(', ')');
        return token.Length == 0 ? null : token.ToLowerInvariant();
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Length <= max ? s : s[..max] + "\u2026";
    }

    private static Dictionary<(string Record, string Section, string Field), IDomainRule> BuildRules()
    {
        var rules = new Dictionary<(string, string, string), IDomainRule>();

        // Identity fields are present on every record type via InjectIdentityFields.
        // Apply to every supported RecordType to suppress UnknownKey noise.
        var allRecordTypes = new[]
        {
            "Weapon", "NPC", "Armor", "Ammo", "Consumable", "MiscItem", "Key", "Container",
            "Quest", "Faction", "Creature", "Race", "Note", "Cell", "Worldspace", "MapMarker", "Perk", "Spell",
            "Projectile", "Explosion", "LeveledList", "WeaponMod", "Recipe", "Script",
            "DialogTopic", "Dialogue"
        };
        foreach (var t in allRecordTypes)
        {
            rules[(t, "Identity", "FormID")] = new StringNonEmpty(12);
            rules[(t, "Identity", "Editor ID")] = new StringNonEmpty(256);
            rules[(t, "Identity", "Display Name")] = new StringNonEmpty(1024);
        }

        // ----- Weapon ----- (field keys verified against GeckWeaponReportWriter.cs)
        rules[("Weapon", "Combat Stats", "Damage")] = new IntRange(0, 10_000);
        rules[("Weapon", "Combat Stats", "DPS")] = new FloatRange(0, 100_000);
        rules[("Weapon", "Combat Stats", "Fire Rate")] = new FloatRange(0, 100);
        rules[("Weapon", "Combat Stats", "Clip Size")] = new IntRange(0, 999);
        rules[("Weapon", "Combat Stats", "Speed")] = new FloatRange(0, 100);
        rules[("Weapon", "Combat Stats", "Reach")] = new FloatRange(0, 100);
        rules[("Weapon", "Combat Stats", "Ammo Per Shot")] = new IntRange(0, 100);
        rules[("Weapon", "Combat Stats", "Projectiles")] = new IntRange(0, 100);
        // Spread is typically [0, 5] but scripted weapons go higher (e.g., shotgun 15).
        rules[("Weapon", "Accuracy", "Spread")] = new FloatRange(0, 100);
        rules[("Weapon", "Accuracy", "Min Spread")] = new FloatRange(0, 100);
        rules[("Weapon", "Accuracy", "Drift")] = new FloatRange(0, 100);
        rules[("Weapon", "VATS", "Hit Chance")] = new IntRange(0, 100);
        rules[("Weapon", "Requirements", "Strength")] = new IntRange(0, 100);
        rules[("Weapon", "Requirements", "Skill")] = new IntRange(0, 100);
        rules[("Weapon", "Value / Weight", "Value")] = new IntRange(0, 1_000_000);
        rules[("Weapon", "Value / Weight", "Weight")] = new FloatRange(0, 1000);
        rules[("Weapon", "Value / Weight", "Health")] = new IntRange(0, 1_000_000);
        // Weapon Resistance: FNV uses -1 as a "no override / use default" sentinel.
        rules[("Weapon", "Misc", "Resistance")] = new IntRange(-1, 1000);
        // Sight Usage Mult is usually 1–2 but scripted special weapons go up to 100.
        rules[("Weapon", "Misc", "Sight Usage Mult")] = new FloatRange(0, 1000);
        rules[("Weapon", "Misc", "Ammo Regen Rate")] = new FloatRange(0, 100);
        rules[("Weapon", "Misc", "Cook Timer (sec)")] = new FloatRange(0, 60);
        rules[("Weapon", "Rumble", "Left Motor")] = new FloatRange(0, 1);
        rules[("Weapon", "Rumble", "Right Motor")] = new FloatRange(0, 1);
        rules[("Weapon", "Rumble", "Duration")] = new FloatRange(0, 60);
        rules[("Weapon", "Rumble", "Wavelength")] = new FloatRange(0, 100);

        // ----- NPC -----
        // Level: vanilla NPCs cap near 50, but boss NPCs use artificially high values
        // (up to 1200 observed) to opt out of level scaling.
        rules[("NPC", "Stats", "Level")] = new IntRange(-1, 2000);
        rules[("NPC", "Stats", "Karma")] = new IntRange(-1000, 1000);
        rules[("NPC", "Stats", "Health Offset")] = new IntRange(-100_000, 100_000);

        // ----- Ammo ----- (real field keys are under "Stats" not "General"/"Combat")
        rules[("Ammo", "Stats", "Value")] = new IntRange(0, 1_000_000);
        rules[("Ammo", "Stats", "Speed")] = new FloatRange(0, 100_000);
        rules[("Ammo", "Stats", "Clip Rounds")] = new IntRange(0, 999);

        // ----- Armor ----- ("Stats" section per real builders).
        // Value stored as IntVal in caps; Weight / DT as Float; Health / DR as Int.
        rules[("Armor", "Stats", "Value")] = new IntRange(0, 1_000_000);
        rules[("Armor", "Stats", "Weight")] = new FloatRange(0, 1000);
        rules[("Armor", "Stats", "Health")] = new IntRange(0, 1_000_000);
        rules[("Armor", "Stats", "DT")] = new FloatRange(0, 1000);
        rules[("Armor", "Stats", "DR")] = new IntRange(0, 1000);

        // ----- Consumable / MiscItem / Key -----
        foreach (var t in new[] { "Consumable", "MiscItem", "Key" })
        {
            rules[(t, "General", "Value")] = new IntRange(0, 1_000_000);
            rules[(t, "General", "Weight")] = new FloatRange(0, 1000);
        }

        // ----- Cell -----
        rules[("Cell", "Coordinates", "Grid X")] = new IntRange(-128, 128);
        rules[("Cell", "Coordinates", "Grid Y")] = new IntRange(-128, 128);

        return rules;
    }

    private static List<(string RecordType, string SectionGlob, IDomainRule Rule)> BuildSectionWildcardRules()
    {
        // Repeating-structure sections. Fields inside these are noisy (variable index
        // names, inline composite rows) — a loose family rule per section avoids
        // 100+ UnknownKey entries while still catching NaN floats / wrong types.
        // Mined from pattern analysis of the 959-entry unknown-keys set.
        // Section wildcards must be type-permissive — rows under these headers mix
        // Item (string), Qty (int), Cost (float), FormID refs, etc. Tight per-field
        // rules belong in the explicit tuple table above.
        return
        [
            // Container contents — repeating "Contents (N items)" sections.
            ("Container", "Contents*", new AnyNonNaN()),

            // Script variables.
            ("Script", "Variables", new AnyNonNaN()),
            ("Note", "Identity", new AnyNonNaN()),
            ("Note", "Art Assets", new AnyNonNaN()),
            ("Note", "Content", new AnyNonNaN()),
            ("Recipe", "Ingredients", new AnyNonNaN()),
            ("Recipe", "Outputs", new AnyNonNaN()),
            ("DialogTopic", "Prompt", new AnyNonNaN()),
            ("MapMarker", "Location", new AnyNonNaN()),

            // Faction memberships / NPC faction slots.
            ("Faction", "Members", new AnyNonNaN()),
            ("NPC", "Factions", new AnyNonNaN()),

            // Leveled list entries.
            ("LeveledList", "Entries", new AnyNonNaN()),

            // Dialogue condition / response rows.
            ("Dialogue", "Conditions", new AnyNonNaN()),
            ("Dialogue", "Responses", new AnyNonNaN()),

            // NPC inventory slots / FaceGen morph rows.
            ("NPC", "Inventory", new AnyNonNaN()),
            ("NPC", "Referenced In", new AnyNonNaN()),
            ("NPC", "FaceGen Morph Data*", new AnyNonNaN()),

            // Key → doors it unlocks.
            ("Key", "Linked Doors", new AnyNonNaN())
        ];
    }

    private static Dictionary<string, IDomainRule> BuildSuffixFamily()
    {
        var f = new Dictionary<string, IDomainRule>(StringComparer.Ordinal);

        // Asset path / model-ish fields — any non-empty string.
        foreach (var s in new[] { "path", "model", "icon", "texture", "nif" })
            f[s] = new StringNonEmpty();

        // Name-like — free text, non-empty.
        foreach (var s in new[] { "name", "editorid", "description", "notes" })
            f[s] = new StringNonEmpty();

        // Numeric-family suffixes: producers may store as Int OR Float, so family
        // rules are type-permissive. Specific ranges live in explicit tuple rules.
        foreach (var s in new[]
                 {
                     "count", "qty", "level", "rank", "index", "area", "priority",
                     "weight", "magnitude", "duration", "radius", "force", "scale",
                     "mult", "chance", "rate", "time", "speed", "cooldown",
                     "cost", "value", "health"
                 })
            f[s] = new AnyNonNaN();

        // Flags — accept any StringVal (human-readable "Flag1, Flag2"), any IntVal, or
        // FormIdVal. Still rejects malformed input.
        f["flags"] = new AnyNonNaN();

        return f;
    }

    private static List<(Regex Pattern, IDomainRule Rule)> BuildSampleShapeRules()
    {
        const RegexOptions opts = RegexOptions.CultureInvariant | RegexOptions.Compiled;

        // Order matters — more specific patterns first. Rules are permissive about the
        // underlying value type: the shape regex tells us what the value *looks like*,
        // but the producer may have stored a FormIdVal, FloatVal, or IntVal with the
        // same display. Precise type-enforcement belongs in explicit tuple rules.
        return
        [
            // "Name - EditorId (0xFORMID)" from FormIdResolver — FormIdVal or StringVal.
            (new Regex(@"\(0x[0-9A-Fa-f]{8}\)\s*$", opts), new ResolvedReference()),

            // "Yes" / "No" / "True" / "False" → bool.
            (new Regex(@"^(Yes|No|True|False)$", opts), new BoolAny()),

            // 0x-hex (flags / bitmasks).
            (new Regex(@"^0x[0-9A-Fa-f]+$", opts), new HexBitmask()),

            // Asset path (contains a slash AND a known extension).
            (new Regex(@"[\\/].+\.(nif|dds|ddx|bsa|wav|xwm|xma|mp3|lip|kf|ini|txt|esp|esm|bmp|jpg|png)\b",
                RegexOptions.IgnoreCase | opts), new StringNonEmpty()),

            // Any bare number — int OR float. Display alone can't tell them apart
            // (FloatVal with whole number formats the same as IntVal). End-anchored so
            // "Cell.Grid = -107, 42" doesn't match.
            (new Regex(@"^-?\d+(\.\d+)?$", opts), new NumericAny())
        ];
    }

    internal interface IDomainRule
    {
        bool Matches(ReportValue value, FormIdResolver? resolver, out string? failReason);
    }

    internal sealed record IntRange(long Min, long Max) : IDomainRule
    {
        public bool Matches(ReportValue value, FormIdResolver? resolver, out string? failReason)
        {
            switch (value)
            {
                case ReportValue.IntVal i when i.Raw >= Min && i.Raw <= Max:
                    failReason = null;
                    return true;
                case ReportValue.IntVal i:
                    failReason = $"int {i.Raw} outside [{Min}, {Max}]";
                    return false;
                default:
                    failReason = $"expected int, got {value.GetType().Name}";
                    return false;
            }
        }
    }

    internal sealed record FloatRange(double Min, double Max, bool AllowNan = false) : IDomainRule
    {
        public bool Matches(ReportValue value, FormIdResolver? resolver, out string? failReason)
        {
            switch (value)
            {
                case ReportValue.FloatVal f when double.IsNaN(f.Raw):
                    if (AllowNan)
                    {
                        failReason = null;
                        return true;
                    }

                    failReason = "float is NaN";
                    return false;
                case ReportValue.FloatVal f when double.IsInfinity(f.Raw):
                    failReason = "float is infinity";
                    return false;
                case ReportValue.FloatVal f when f.Raw >= Min && f.Raw <= Max:
                    failReason = null;
                    return true;
                case ReportValue.FloatVal f:
                    failReason = $"float {f.Raw:G} outside [{Min:G}, {Max:G}]";
                    return false;
                case ReportValue.IntVal i when i.Raw >= Min && i.Raw <= Max:
                    failReason = null;
                    return true;
                case ReportValue.IntVal i:
                    failReason = $"int {i.Raw} outside [{Min:G}, {Max:G}]";
                    return false;
                default:
                    failReason = $"expected float, got {value.GetType().Name}";
                    return false;
            }
        }
    }

    internal sealed record EnumSet(HashSet<int> ValidValues) : IDomainRule
    {
        public bool Matches(ReportValue value, FormIdResolver? resolver, out string? failReason)
        {
            if (value is not ReportValue.IntVal i)
            {
                failReason = $"expected int (enum), got {value.GetType().Name}";
                return false;
            }

            if (ValidValues.Contains(i.Raw))
            {
                failReason = null;
                return true;
            }

            failReason = $"int {i.Raw} not in enum domain ({ValidValues.Count} values)";
            return false;
        }
    }

    internal sealed record FormIdMustResolve(bool AllowZero = true) : IDomainRule
    {
        public bool Matches(ReportValue value, FormIdResolver? resolver, out string? failReason)
        {
            if (value is not ReportValue.FormIdVal f)
            {
                failReason = $"expected FormID, got {value.GetType().Name}";
                return false;
            }

            if (f.Raw == 0)
            {
                if (AllowZero)
                {
                    failReason = null;
                    return true;
                }

                failReason = "FormID is null (0x00000000) but rule disallows it";
                return false;
            }

            if (resolver == null)
            {
                failReason = null;
                return true;
            }

            var resolved = resolver.GetEditorId(f.Raw) ?? resolver.GetDisplayName(f.Raw);
            if (!string.IsNullOrEmpty(resolved))
            {
                failReason = null;
                return true;
            }

            // High byte of FormID is the load order index. If non-zero, it points
            // at a master file we may not have loaded — accept that as expected.
            var loadOrderIdx = (f.Raw >> 24) & 0xFF;
            if (loadOrderIdx != 0)
            {
                failReason = null;
                return true;
            }

            failReason = $"FormID 0x{f.Raw:X8} did not resolve in primary file";
            return false;
        }
    }

    internal sealed record StringNonEmpty(int MaxLen = 4096) : IDomainRule
    {
        public bool Matches(ReportValue value, FormIdResolver? resolver, out string? failReason)
        {
            if (value is not ReportValue.StringVal s)
            {
                failReason = $"expected string, got {value.GetType().Name}";
                return false;
            }

            if (string.IsNullOrEmpty(s.Raw))
            {
                failReason = "string is empty";
                return false;
            }

            if (s.Raw.Length > MaxLen)
            {
                failReason = $"string length {s.Raw.Length} exceeds max {MaxLen}";
                return false;
            }

            failReason = null;
            return true;
        }
    }

    internal sealed record BoolAny : IDomainRule
    {
        public bool Matches(ReportValue value, FormIdResolver? resolver, out string? failReason)
        {
            if (value is ReportValue.BoolVal)
            {
                failReason = null;
                return true;
            }

            failReason = $"expected bool, got {value.GetType().Name}";
            return false;
        }
    }

    /// <summary>Accepts any IntVal — used by the family/shape layers when we only know "this is an int".</summary>
    internal sealed record IntAny : IDomainRule
    {
        public bool Matches(ReportValue value, FormIdResolver? resolver, out string? failReason)
        {
            if (value is ReportValue.IntVal)
            {
                failReason = null;
                return true;
            }

            failReason = $"expected int, got {value.GetType().Name}";
            return false;
        }
    }

    /// <summary>Accepts any FloatVal but rejects NaN/Infinity. No numeric bounds.</summary>
    internal sealed record FloatAny : IDomainRule
    {
        public bool Matches(ReportValue value, FormIdResolver? resolver, out string? failReason)
        {
            switch (value)
            {
                case ReportValue.FloatVal f when double.IsNaN(f.Raw):
                    failReason = "float is NaN";
                    return false;
                case ReportValue.FloatVal f when double.IsInfinity(f.Raw):
                    failReason = "float is infinity";
                    return false;
                case ReportValue.FloatVal:
                case ReportValue.IntVal:
                    failReason = null;
                    return true;
                default:
                    failReason = $"expected float, got {value.GetType().Name}";
                    return false;
            }
        }
    }

    /// <summary>Accepts any StringVal. Loose check used by shape/suffix families.</summary>
    internal sealed record StringAny : IDomainRule
    {
        public bool Matches(ReportValue value, FormIdResolver? resolver, out string? failReason)
        {
            if (value is ReportValue.StringVal)
            {
                failReason = null;
                return true;
            }

            failReason = $"expected string, got {value.GetType().Name}";
            return false;
        }
    }

    /// <summary>
    ///     Hex bitmask — IntVal of any size, or StringVal starting with <c>0x</c>, or a FormIdVal
    ///     (printed as hex). Used by the <c>*Flags</c> suffix family.
    /// </summary>
    internal sealed record HexBitmask : IDomainRule
    {
        public bool Matches(ReportValue value, FormIdResolver? resolver, out string? failReason)
        {
            switch (value)
            {
                case ReportValue.IntVal:
                case ReportValue.FormIdVal:
                    failReason = null;
                    return true;
                case ReportValue.StringVal s when s.Raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase):
                    failReason = null;
                    return true;
                case ReportValue.StringVal s:
                    failReason = $"expected hex-shaped string, got \"{s.Raw}\"";
                    return false;
                default:
                    failReason = $"expected hex bitmask, got {value.GetType().Name}";
                    return false;
            }
        }
    }

    /// <summary>
    ///     Loose numeric rule — accepts any IntVal OR FloatVal (rejects NaN/Infinity
    ///     on the float side). Used by the shape classifier when we've only pattern-matched
    ///     a bare decimal and can't tell whether the producer stored it as int or float.
    /// </summary>
    internal sealed record NumericAny : IDomainRule
    {
        public bool Matches(ReportValue value, FormIdResolver? resolver, out string? failReason)
        {
            switch (value)
            {
                case ReportValue.IntVal:
                    failReason = null;
                    return true;
                case ReportValue.FloatVal f when double.IsNaN(f.Raw):
                    failReason = "float is NaN";
                    return false;
                case ReportValue.FloatVal f when double.IsInfinity(f.Raw):
                    failReason = "float is infinity";
                    return false;
                case ReportValue.FloatVal:
                    failReason = null;
                    return true;
                default:
                    failReason = $"expected numeric, got {value.GetType().Name}";
                    return false;
            }
        }
    }

    /// <summary>
    ///     Loose "looks like a resolved reference" rule — accepts FormIdVal (raw uint
    ///     plus formatted display) OR StringVal whose display is the formatted form.
    ///     Used by the shape classifier for displays ending in <c>(0xFORMID)</c>.
    /// </summary>
    internal sealed record ResolvedReference : IDomainRule
    {
        public bool Matches(ReportValue value, FormIdResolver? resolver, out string? failReason)
        {
            switch (value)
            {
                case ReportValue.FormIdVal:
                case ReportValue.StringVal:
                    failReason = null;
                    return true;
                default:
                    failReason = $"expected resolved reference, got {value.GetType().Name}";
                    return false;
            }
        }
    }

    /// <summary>
    ///     The most permissive rule possible — accepts any value type, only flags NaN/Infinity
    ///     floats. Used by section-wildcard rules and loose suffix families where "the rule
    ///     knows the value exists; tight bounds belong in an explicit tuple rule".
    /// </summary>
    internal sealed record AnyNonNaN : IDomainRule
    {
        public bool Matches(ReportValue value, FormIdResolver? resolver, out string? failReason)
        {
            switch (value)
            {
                case ReportValue.FloatVal f when double.IsNaN(f.Raw):
                    failReason = "float is NaN";
                    return false;
                case ReportValue.FloatVal f when double.IsInfinity(f.Raw):
                    failReason = "float is infinity";
                    return false;
                default:
                    failReason = null;
                    return true;
            }
        }
    }

    /// <summary>Field-level violation for a single record.</summary>
    internal sealed record DomainViolation(
        string RecordType,
        uint FormId,
        string? EditorId,
        string Section,
        string Field,
        string Reason,
        string DisplayValue);

    /// <summary>An untracked (RecordType, Section, Field) tuple — informational.</summary>
    internal sealed record UnknownKey(string RecordType, string Section, string Field, string SampleValue);

    /// <summary>Aggregated result for one record.</summary>
    internal sealed class RecordEvaluation
    {
        internal List<DomainViolation> Violations { get; } = [];
        internal List<UnknownKey> UnknownKeys { get; } = [];
    }
}
