namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Cross-build comparison built on top of <see cref="RecordReportComparer" />.
///     Walks per-FormID snapshots from N builds, runs pairwise diffs, and demotes
///     known platform/era differences to "expected drift" so real regressions stand out.
///
///     Drift entries are seeded from CLAUDE.md "Known Content Differences" and the
///     memory/ docs (formtype_drift.md, nov2009_struct_sizes.md). The allow-list is
///     keyed on (RecordType, SectionName, FieldKey) optionally pair-restricted by
///     build-label substring (e.g., only Xbox↔PC, only July2010 against anything).
/// </summary>
internal static class CrossBuildReportComparer
{
    /// <summary>
    ///     Why a particular field difference is expected and should not count as a regression.
    ///     The Pair predicate, when present, must match the (buildA, buildB) labels
    ///     (case-insensitive substring contains). Null = applies to any pair.
    /// </summary>
    internal sealed record DriftRule(
        string RecordType,
        string SectionName,
        string FieldKey,
        string Reason,
        Func<string, string, bool>? Pair = null);

    internal sealed record FieldDiff(
        string Section,
        string Field,
        string ValueA,
        string ValueB);

    internal sealed record RecordDiff(
        string RecordType,
        uint FormId,
        string? EditorId,
        List<FieldDiff> Differences);

    internal sealed class PairResult
    {
        internal string BuildA { get; init; } = "";
        internal string BuildB { get; init; } = "";

        // Per-record-type counts.
        internal Dictionary<string, int> SharedFormIds { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, int> Matching { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, int> DriftAllowed { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, List<RecordDiff>> Regressions { get; } = new(StringComparer.Ordinal);
    }

    private static readonly List<DriftRule> DefaultDriftRules =
    [
        // --- Global wildcards (evaluated first; cover the high-volume systematic noise) ---

        // Section length differing between prototype & ship builds accounts for ~28% of
        // all observed regressions. Appears whenever a subrecord was added/removed.
        new DriftRule("*", "*", "(field count)",
            "Section length drift between builds (prototype ↔ ship)",
            (a, b) => IsPrototype(a) || IsPrototype(b)),
        // Editor ID / Display Name churn across builds — real naming evolution, not a bug.
        new DriftRule("*", "Identity", "Editor ID",
            "Editor ID naming churn across builds"),
        new DriftRule("*", "Identity", "Display Name",
            "FULL string churn (localization / rewrites) across builds"),
        // File/memory position is intentionally excluded from the HTML diff renderer
        // (see ComparisonJsonBlobBuilder.ExcludedFieldKeys). Mirror that here.
        new DriftRule("*", "Identity", "Offset",
            "File/memory offset — excluded from comparison by HTML renderer too"),
        // Endianness is a platform marker (Xbox big-endian vs PC little-endian) — not drift.
        new DriftRule("*", "Identity", "Endianness",
            "Endianness is a platform label, not semantic drift"),

        // --- Structural field-reorder (`X vs Y` synthetic names) ---
        // These surface when a subrecord pair's serialization order flipped between builds.
        new DriftRule("DialogTopic", "Identity", "ResponseCount vs Priority",
            "DialogTopic Identity subrecord order swapped between builds"),
        new DriftRule("DialogTopic", "Identity", "Priority vs ResponseCount",
            "DialogTopic Identity subrecord order (reverse pair)"),
        new DriftRule("Creature", "Identity", "Type vs Display Name",
            "Creature FULL/DATA header order swap"),
        new DriftRule("NPC", "References", "Template vs Voice Type",
            "NPC TPLT/VTCK subrecord order swap"),
        new DriftRule("NPC", "References", "Voice Type vs Script",
            "NPC VTCK/SCRI subrecord order swap"),
        new DriftRule("NPC", "References", "Face NPC vs Template",
            "NPC PNAM/TPLT face-NPC ordering"),
        new DriftRule("NPC", "Physical", "Height vs Eyes",
            "NPC ENAM/HCLR ordering swap"),
        new DriftRule("NPC", "Physical", "Hair Color vs Hairstyle",
            "NPC HCLF/HCLR ordering swap"),
        new DriftRule("NPC", "Stats", "S.P.E.C.I.A.L. vs Skills",
            "NPC ATTR/DNAM ordering swap"),
        new DriftRule("Ammo", "Stats", "Projectile vs Model",
            "Ammo PROJ/MODL ordering swap"),

        // --- Schema-wide churn ---

        // Worldspace records differ in every single pair — global schema drift, not a bug.
        new DriftRule("Worldspace", "*", "*",
            "Worldspace schema drifts across every pair of builds (global change)"),
        // Ammo Flags default flipped between Xbox and everything else (0x00 ↔ 0xFF).
        new DriftRule("Ammo", "Stats", "Flags",
            "Ammo Flags default differs between Xbox and PC/prototype",
            (a, b) => IsXbox(a) || IsXbox(b)),

        // --- Balance rebalances ---

        new DriftRule("Creature", "Combat", "Attack Damage",
            "Creature Attack Damage rebalanced across builds",
            (a, b) => IsPrototype(a) || IsPrototype(b)),
        new DriftRule("Creature", "AI Data", "Assistance",
            "Creature AI Assistance rebalanced broadly"),
        new DriftRule("Creature", "Stats", "Level",
            "Creature Level rebalanced across builds"),
        new DriftRule("Creature", "Stats", "Flags",
            "Creature Flags churn across builds"),
        new DriftRule("Creature", "Stats", "Fatigue",
            "Creature Fatigue rebalanced across builds"),
        new DriftRule("Armor", "Stats", "DR",
            "Armor DR→DT migration across builds",
            (a, b) => IsPrototype(a) || IsPrototype(b)),
        new DriftRule("Armor", "Stats", "DT",
            "Armor DT subrecord added/retuned across builds"),
        new DriftRule("Container", "Identity", "Respawns",
            "Container Respawns flag flipped broadly between builds"),
        new DriftRule("Dialogue", "Conditions", "Condition 1",
            "Dialogue CTDA ordering shifts prototype ↔ ship",
            (a, b) => IsPrototype(a) || IsPrototype(b)),
        new DriftRule("Dialogue", "Conditions", "Condition 2",
            "Dialogue CTDA ordering shifts prototype ↔ ship",
            (a, b) => IsPrototype(a) || IsPrototype(b)),
        new DriftRule("Weapon", "Sound", "*",
            "Weapon sound-slot indices reordered across builds"),

        // --- NPC broad churn (prototype builds rebalance these extensively) ---

        new DriftRule("NPC", "AI Data", "Assistance",
            "NPC AI Assistance rebalanced broadly across builds"),
        new DriftRule("NPC", "References", "Voice Type",
            "NPC Voice Type retakes / reassignment between prototype builds",
            (a, b) => IsPrototype(a) || IsPrototype(b)),
        new DriftRule("NPC", "Derived Stats", "*",
            "NPC Derived Stats (Calculated Health, Fatigue, Critical Chance, …) shift with balance changes"),
        new DriftRule("NPC", "FaceGen Morph Data", "FGGS Hex",
            "NPC FaceGen geometry hash differs (coefficients retuned per build)"),
        new DriftRule("NPC", "FaceGen Morph Data", "FGTS Hex",
            "NPC FaceGen texture hash differs (coefficients retuned per build)"),
        new DriftRule("NPC", "FaceGen Morph Data", "FGGA Hex",
            "NPC FaceGen geometry advance hash differs (coefficients retuned per build)"),
        new DriftRule("NPC", "Stats", "S.P.E.C.I.A.L.",
            "NPC S.P.E.C.I.A.L. attribute layout changes across builds"),
        new DriftRule("NPC", "Physical Traits", "Hair Color vs Hair Length",
            "NPC HCLR/HCLG ordering swap"),
        new DriftRule("NPC", "Physical Traits", "Height vs Eyes",
            "NPC ENAM/HCLR ordering swap in Physical Traits section"),
        new DriftRule("NPC", "Physical Traits", "Height vs Hair Color",
            "NPC Physical Traits ordering swap"),
        new DriftRule("NPC", "Physical Traits", "Eyes vs Hair Color",
            "NPC Physical Traits ordering swap"),

        // --- Script rebuilds between builds (bytecode recompile, ref lists shift) ---

        new DriftRule("Script", "Decompiled", "Decompiled",
            "Script decompiled bytecode changes between builds"),
        new DriftRule("Script", "Stats", "Compiled Size",
            "Script compiled size tracks source edits"),
        new DriftRule("Script", "Stats", "Ref Object Count",
            "Script referenced-object count tracks source edits"),
        new DriftRule("Script", "References", "Referenced Objects",
            "Script referenced-object list tracks source edits"),

        // --- Cell + Dialogue content edits ---

        new DriftRule("Cell", "Placed Objects", "Objects",
            "Cell contents change between builds as content is added / moved"),
        new DriftRule("Cell", "Identity", "Flags",
            "Cell flags churn between builds (lighting / exterior flags toggled)"),
        new DriftRule("Cell", "Environment", "Water Height",
            "Cell water height tweaked across builds"),
        new DriftRule("Dialogue", "Links", "Links To vs Previous INFO",
            "Dialogue INFO ordering / linkage swap across builds"),
        new DriftRule("Dialogue", "Links", "Links To",
            "Dialogue INFO linkage churn across builds"),
        new DriftRule("Dialogue", "Conditions", "Condition 3",
            "Dialogue CTDA ordering shifts prototype ↔ ship",
            (a, b) => IsPrototype(a) || IsPrototype(b)),
        new DriftRule("Dialogue", "References", "Speaker",
            "Dialogue Speaker reassignment across builds"),

        // --- Second-pass rebalance / schema-evolution absorption ---
        // All explicitly tied to prototype builds so they don't hide real bugs between
        // two ship builds (e.g. xex ↔ debug or a future final/patch comparison).

        new DriftRule("NPC", "Stats", "Level",
            "NPC Level rebalanced broadly across prototype builds",
            (a, b) => IsPrototype(a) || IsPrototype(b)),
        new DriftRule("NPC", "AI Data", "Confidence",
            "NPC AI Confidence rebalanced across builds"),
        new DriftRule("NPC", "AI Data", "Class",
            "NPC AI Class reassignment across builds"),
        new DriftRule("NPC", "Combat", "*",
            "NPC Combat section evolves across prototype builds",
            (a, b) => IsPrototype(a) || IsPrototype(b)),
        new DriftRule("NPC", "FaceGen Morph Data", "FGGS Controls",
            "FaceGen geometry coefficient controls retuned across builds"),
        new DriftRule("NPC", "FaceGen Morph Data", "FGTS Controls",
            "FaceGen texture coefficient controls retuned across builds"),
        new DriftRule("NPC", "FaceGen Morph Data", "FGGA Controls",
            "FaceGen advance coefficient controls retuned across builds"),
        new DriftRule("NPC", "Physical Traits", "Hair Length",
            "NPC hair length tweaked across builds"),

        new DriftRule("Cell", "Identity", "Type",
            "Cell Type classification evolves across builds"),
        new DriftRule("Cell", "Identity", "Has Water",
            "Cell water flag flipped across builds"),

        new DriftRule("DialogTopic", "Identity", "Flags",
            "DialogTopic flags evolve across builds"),
        new DriftRule("DialogTopic", "Identity", "ResponseCount vs TopLevel",
            "DialogTopic Identity subrecord order variant (3-way)"),

        new DriftRule("Weapon", "Combat Stats", "Damage",
            "Weapon Damage rebalanced broadly across prototype builds",
            (a, b) => IsPrototype(a) || IsPrototype(b)),
        new DriftRule("Weapon", "Combat Stats", "DPS",
            "Weapon DPS is a derived stat — moves with Damage changes",
            (a, b) => IsPrototype(a) || IsPrototype(b)),
        new DriftRule("Weapon", "Combat Stats", "Fire Rate",
            "Weapon Fire Rate rebalanced broadly across prototype builds",
            (a, b) => IsPrototype(a) || IsPrototype(b)),

        new DriftRule("Script", "Source", "Source",
            "Script source code tracks edits between builds"),

        new DriftRule("Key", "Stats", "Value",
            "Key value rebalanced broadly across builds"),

        // --- Legacy Xbox↔PC platform drift (kept from the original seed) ---

        // PNAM-only-on-Xbox stripping during conversion.
        new DriftRule("Dialogue", "PNAM", "*",
            "PNAM stripped during Xbox→PC conversion",
            (a, b) => IsXbox(a) ^ IsXbox(b)),
        // AIDT padding: zero on Xbox, non-zero on PC. Documented in CLAUDE.md.
        new DriftRule("NPC", "AIDT", "Unused 1",
            "AIDT padding differs Xbox vs PC",
            (a, b) => IsXbox(a) ^ IsXbox(b)),
        new DriftRule("NPC", "AIDT", "Unused 2",
            "AIDT padding differs Xbox vs PC",
            (a, b) => IsXbox(a) ^ IsXbox(b)),
        // LVLO padding: FA 06 (Xbox) vs 15 06 (PC). Documented in CLAUDE.md.
        new DriftRule("LeveledList", "LVLO", "Padding",
            "LVLO padding bytes differ Xbox vs PC",
            (a, b) => IsXbox(a) ^ IsXbox(b)),
        // FormType +1 enum shift on pre-Dec-2009 prototype builds.
        new DriftRule("*", "Identity", "FormType",
            "Pre-Dec-2009 prototype FormType enum shift",
            (a, b) => IsPreDec2009(a) ^ IsPreDec2009(b))
    ];

    /// <summary>
    ///     Run the full pairwise comparison.
    ///     <paramref name="builds" /> is the ordered list of (label, structuredRecords)
    ///     produced by <see cref="CrossDumpAggregator" /> (one per loaded source).
    ///     Returns one PairResult per ordered pair (i, j) with i &lt; j.
    /// </summary>
    internal static List<PairResult> Compare(
        List<(string Label, Dictionary<string, Dictionary<uint, RecordReport>> Records)> builds,
        IEnumerable<DriftRule>? extraRules = null)
    {
        var rules = DefaultDriftRules.ToList();
        if (extraRules != null) rules.AddRange(extraRules);

        var results = new List<PairResult>();
        for (var i = 0; i < builds.Count; i++)
        {
            for (var j = i + 1; j < builds.Count; j++)
            {
                results.Add(ComparePair(builds[i], builds[j], rules));
            }
        }

        return results;
    }

    /// <summary>
    ///     Convenience: flatten the (RecordType → FormID → dumpIdx → RecordReport) view
    ///     produced by <see cref="CrossDumpAggregator" /> into the per-build maps this
    ///     comparator expects.
    /// </summary>
    internal static List<(string Label, Dictionary<string, Dictionary<uint, RecordReport>> Records)>
        ProjectFromIndex(CrossDumpRecordIndex index, IReadOnlyList<string>? buildLabels = null)
    {
        var labels = buildLabels ?? index.Dumps.Select(d => d.ShortName).ToList();
        var builds = new List<(string Label, Dictionary<string, Dictionary<uint, RecordReport>>)>();
        for (var idx = 0; idx < labels.Count; idx++)
        {
            builds.Add((labels[idx], new Dictionary<string, Dictionary<uint, RecordReport>>(StringComparer.Ordinal)));
        }

        foreach (var (recordType, formIdMap) in index.StructuredRecords)
        {
            foreach (var (formId, dumpMap) in formIdMap)
            {
                foreach (var (dumpIdx, report) in dumpMap)
                {
                    if (dumpIdx < 0 || dumpIdx >= builds.Count) continue;
                    if (!builds[dumpIdx].Item2.TryGetValue(recordType, out var perBuildMap))
                    {
                        perBuildMap = new Dictionary<uint, RecordReport>();
                        builds[dumpIdx].Item2[recordType] = perBuildMap;
                    }

                    perBuildMap[formId] = report;
                }
            }
        }

        return builds;
    }

    private static PairResult ComparePair(
        (string Label, Dictionary<string, Dictionary<uint, RecordReport>> Records) buildA,
        (string Label, Dictionary<string, Dictionary<uint, RecordReport>> Records) buildB,
        List<DriftRule> rules)
    {
        var result = new PairResult { BuildA = buildA.Label, BuildB = buildB.Label };

        var sharedTypes = buildA.Records.Keys.Intersect(buildB.Records.Keys, StringComparer.Ordinal);
        foreach (var recordType in sharedTypes)
        {
            var aMap = buildA.Records[recordType];
            var bMap = buildB.Records[recordType];

            var sharedFormIds = aMap.Keys.Intersect(bMap.Keys);
            var matching = 0;
            var driftCount = 0;
            var regressions = new List<RecordDiff>();

            foreach (var formId in sharedFormIds)
            {
                var aReport = aMap[formId];
                var bReport = bMap[formId];

                if (RecordReportComparer.Equals(aReport, bReport))
                {
                    matching++;
                    continue;
                }

                var diffs = DiffSections(aReport, bReport);
                if (diffs.Count == 0)
                {
                    matching++;
                    continue;
                }

                var (driftDiffs, realDiffs) = SplitDrift(
                    recordType, diffs, rules, buildA.Label, buildB.Label);

                if (realDiffs.Count == 0)
                {
                    driftCount++;
                }
                else
                {
                    if (driftDiffs.Count > 0) driftCount++;
                    regressions.Add(new RecordDiff(
                        recordType,
                        formId,
                        aReport.EditorId ?? bReport.EditorId,
                        realDiffs));
                }
            }

            var sharedCount = matching + driftCount + regressions.Count;
            result.SharedFormIds[recordType] = sharedCount;
            result.Matching[recordType] = matching;
            result.DriftAllowed[recordType] = driftCount;
            if (regressions.Count > 0)
                result.Regressions[recordType] = regressions;
        }

        return result;
    }

    private static List<FieldDiff> DiffSections(RecordReport a, RecordReport b)
    {
        var diffs = new List<FieldDiff>();
        var bByName = new Dictionary<string, ReportSection>(b.Sections.Count);
        foreach (var section in b.Sections)
            bByName.TryAdd(section.Name, section);

        foreach (var sectionA in a.Sections)
        {
            if (!bByName.TryGetValue(sectionA.Name, out var sectionB))
                continue; // section only in A — ignored, same as RecordReportComparer

            // Walk fields by index — match RecordReportComparer.FieldsEqual ordering rule.
            var minLen = Math.Min(sectionA.Fields.Count, sectionB.Fields.Count);
            for (var i = 0; i < minLen; i++)
            {
                var fa = sectionA.Fields[i];
                var fb = sectionB.Fields[i];
                if (fa.Key != fb.Key)
                {
                    diffs.Add(new FieldDiff(sectionA.Name, $"{fa.Key} vs {fb.Key}",
                        fa.Value.Display, fb.Value.Display));
                    continue;
                }

                if (!ValuesDisplayEqual(fa.Value, fb.Value))
                {
                    diffs.Add(new FieldDiff(sectionA.Name, fa.Key, fa.Value.Display, fb.Value.Display));
                }
            }

            if (sectionA.Fields.Count != sectionB.Fields.Count)
            {
                diffs.Add(new FieldDiff(sectionA.Name, "(field count)",
                    sectionA.Fields.Count.ToString(),
                    sectionB.Fields.Count.ToString()));
            }
        }

        return diffs;
    }

    private static bool ValuesDisplayEqual(ReportValue a, ReportValue b)
    {
        // Reuse the existing comparator's stricter notion of equality. We only get
        // here when RecordReportComparer.Equals returned false, so individual field
        // mismatches map back to whatever the comparer rejected.
        return (a, b) switch
        {
            (ReportValue.IntVal ia, ReportValue.IntVal ib) => ia.Raw == ib.Raw,
            (ReportValue.FloatVal fa, ReportValue.FloatVal fb) => Math.Abs(fa.Raw - fb.Raw) < 1e-6,
            (ReportValue.StringVal sa, ReportValue.StringVal sb) =>
                string.Equals(sa.Raw, sb.Raw, StringComparison.Ordinal),
            (ReportValue.BoolVal ba, ReportValue.BoolVal bb) => ba.Raw == bb.Raw,
            (ReportValue.FormIdVal fa, ReportValue.FormIdVal fb) => fa.Raw == fb.Raw,
            _ => string.Equals(a.Display, b.Display, StringComparison.Ordinal)
        };
    }

    private static (List<FieldDiff> Drift, List<FieldDiff> Real) SplitDrift(
        string recordType,
        List<FieldDiff> diffs,
        List<DriftRule> rules,
        string buildA,
        string buildB)
    {
        var drift = new List<FieldDiff>();
        var real = new List<FieldDiff>();

        foreach (var diff in diffs)
        {
            if (MatchesAnyRule(recordType, diff, rules, buildA, buildB))
                drift.Add(diff);
            else
                real.Add(diff);
        }

        return (drift, real);
    }

    private static bool MatchesAnyRule(
        string recordType,
        FieldDiff diff,
        List<DriftRule> rules,
        string buildA,
        string buildB)
    {
        foreach (var rule in rules)
        {
            if (rule.RecordType != "*" && !string.Equals(rule.RecordType, recordType, StringComparison.Ordinal))
                continue;
            if (rule.SectionName != "*" && !string.Equals(rule.SectionName, diff.Section, StringComparison.Ordinal))
                continue;
            if (rule.FieldKey != "*" && !string.Equals(rule.FieldKey, diff.Field, StringComparison.Ordinal))
                continue;
            if (rule.Pair != null && !rule.Pair(buildA, buildB))
                continue;
            return true;
        }

        return false;
    }

    private static bool IsXbox(string label)
    {
        return label.Contains("xbox", StringComparison.OrdinalIgnoreCase)
               || label.Contains("360", StringComparison.Ordinal)
               || label.Contains("dmp", StringComparison.OrdinalIgnoreCase)
               || label.StartsWith("xex", StringComparison.OrdinalIgnoreCase)
               || label.Equals("debug", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     True for the in-development builds that sit between "early prototype" and
    ///     "ship" — where broad rebalances / schema evolution are expected. Covers
    ///     the sample roster we carry (xex21/xex22/xex44, July2010, Aug2010) plus
    ///     generic month-year tokens.
    /// </summary>
    private static bool IsPrototype(string label)
    {
        return label.Contains("July2010", StringComparison.OrdinalIgnoreCase)
               || label.Contains("Aug2010", StringComparison.OrdinalIgnoreCase)
               || label.Contains("xex21", StringComparison.OrdinalIgnoreCase)
               || label.Contains("xex22", StringComparison.OrdinalIgnoreCase)
               || label.Contains("xex44", StringComparison.OrdinalIgnoreCase)
               || label.Contains("proto", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPreDec2009(string label)
    {
        // Conservative: only label-name-based detection. Real builds we know are
        // pre-Dec-2009 are typically labeled "nov2009" or similar — for the FNV
        // builds we ship samples for (July 2010, Aug 2010), this returns false.
        return label.Contains("nov2009", StringComparison.OrdinalIgnoreCase)
               || label.Contains("oct2009", StringComparison.OrdinalIgnoreCase);
    }
}
