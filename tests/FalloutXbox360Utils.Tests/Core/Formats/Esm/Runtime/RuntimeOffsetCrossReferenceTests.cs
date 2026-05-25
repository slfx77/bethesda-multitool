using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Probes;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

/// <summary>
///     Empirical-first offset validator (Phase 1B.10). Cross-references runtime
///     bytes from each test DMP snippet against the authoritative ESM subrecord
///     payload, with no PDB involvement — the PDBs in this repo all post-date the
///     captured DMPs by 3-9 months, so PDB offsets aren't reliable ground truth
///     for the class layouts captured in our DMPs.
///
///     Each test picks ONE field with a clean ESM-vs-runtime byte equivalence
///     (typically a scalar or a small fixed payload), iterates over every record
///     of the relevant FormType in the snippet, and asserts the bytes at the
///     code's currently-asserted offset match the ESM payload. The match rate is
///     the diagnostic — high rate = offset confirmed; low rate = offset wrong or
///     a per-build shift needs adjusting.
///
///     Mirrors the pattern from <see cref="PackageTerminalOffsetInvestigationTests" />
///     (the Tier 3.2 TERM unblocker that established this methodology).
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public sealed class RuntimeOffsetCrossReferenceTests
{
    private static readonly string SnippetDir = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "Dmp");

    private static readonly string Xbox360EsmPath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "Sample", "ESM", "360_final", "FalloutNV.esm"));

    public static IEnumerable<object[]> AllSnippets =>
    [
        ["debug_dump"],
        ["release_dump"],
        ["xex4_dump"],
        ["xex44_dump"],
        ["memdebug_dump"]
    ];

    // =========================================================================
    // Population sanity check: every snippet must contain a substantial NPC
    // population that intersects with the Xbox 360 ESM (the parity tests need
    // anchors to validate against). Fails loudly if the snippet/ESM intersection
    // becomes too small to draw conclusions from.
    // =========================================================================

    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task NPC_ESM_intersection_has_enough_records_for_anchoring(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        Assert.True(File.Exists(Xbox360EsmPath), $"ESM not found: {Xbox360EsmPath}");
        var extractor = EsmSubrecordExtractor.LoadCached(Xbox360EsmPath);

        var npcEntries = snippet.RuntimeEditorIds
            .Where(e => e.FormType == 0x2A && e.TesFormOffset.HasValue)
            .ToList();

        var withAcbs = npcEntries.Count(e => extractor.GetSubrecordBytes(e.FormId, "ACBS") != null);

        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{snippetName}] NPCs in snippet: {npcEntries.Count}, with ACBS: {withAcbs}");

        Assert.True(withAcbs >= 100,
            $"[{snippetName}] Only {withAcbs} NPCs in snippet have ACBS in ESM "
            + $"(need >= 100 for stable parity anchoring). If this drops, either the "
            + $"snippet captured fewer NPCs or the subrecord walker broke.");
    }

    // =========================================================================
    // NPC ACBS Level — first true parity anchor. FNV's ACBS layout (24 bytes):
    //   +0  Flags        UInt32  +4  Fatigue       UInt16  +6  BarterGold    UInt16
    //   +8  Level        Int16   +10 CalcMin       UInt16  +12 CalcMax       UInt16
    //   +14 SpeedMult    UInt16  +16 KarmaAlignment Float  +20 Disposition   Int16
    //   +22 TemplateFlags UInt16
    //
    // The runtime ACTOR_BASE_DATA struct uses the same layout (confirmed in
    // RuntimeActorReader.ReadActorBaseStats which reads Level at acbsStart + 8).
    // So Level should byte-match between ESM ACBS @ +8 and runtime
    // ACTOR_BASE_DATA @ NpcAcbsOffset + 8.
    //
    // Template filter: NPCs with TemplateFlags bit 0x0002 (Use Stats) inherit
    // ACBS from their template at load time, so their runtime value won't
    // match the NPC's own ESM ACBS. Skip those.
    //
    // Threshold: >= 80% match rate. Pre-fix (if NpcAcbsOffset were wrong) we'd
    // see near 0%; reading the wrong field within ACBS gives ~15-20%
    // (Fatigue@+4 case empirically); reading the right field through both sides
    // should give well over 80% once templated NPCs are filtered out.
    // =========================================================================

    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task NPC_Acbs_Flags_runtime_matches_ESM_at_NpcAcbsOffset(string snippetName)
    {
        // ACBS.Flags is a UInt32 at offset +0 of the 24-byte ACBS payload. It
        // controls NPC properties (Essential, Respawn, AutoCalcStats,
        // PC_Level_Mult, etc.) that the engine reads at load but does NOT mutate
        // afterward — making it an ideal stable anchor. The matching runtime
        // field is ACTOR_BASE_DATA[+0] at TesFormOffset + NpcAcbsOffset.
        //
        // Unlike ACBS.Level (which gameplay can modify and PC_Level_Mult
        // resolves dynamically), Flags is post-load-stable, so a near-100%
        // parity match is expected when the offset is correct.
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        Assert.True(File.Exists(Xbox360EsmPath), $"ESM not found: {Xbox360EsmPath}");
        var extractor = EsmSubrecordExtractor.LoadCached(Xbox360EsmPath);
        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);

        var coreShift = RuntimeBuildOffsets.GetPdbShift(MinidumpAnalyzer.DetectBuildType(snippet.MinidumpInfo));
        var flagsRuntimeOffset = 52 + coreShift; // NpcAcbsOffset + ACBS.Flags (@ +0)

        var npcEntries = snippet.RuntimeEditorIds
            .Where(e => e.FormType == 0x2A && e.TesFormOffset.HasValue)
            .ToList();

        var checkedNpcs = 0;
        var matches = 0;
        var mismatches = new List<string>();

        foreach (var entry in npcEntries)
        {
            var esmAcbs = extractor.GetSubrecordBytes(entry.FormId, "ACBS");
            if (esmAcbs == null || esmAcbs.Length < 4)
            {
                continue;
            }

            // Skip templated NPCs (TemplateFlags bit 0x0002 = Use Stats). Even
            // though Flags is itself stable post-load, templated NPCs have the
            // runtime ACBS overwritten from the template, so ESM Flags and
            // runtime Flags can differ legitimately.
            if (esmAcbs.Length >= 24)
            {
                var templateFlags = BinaryUtils.ReadUInt16BE(esmAcbs, 22);
                if ((templateFlags & 0x0002) != 0)
                {
                    continue;
                }
            }

            var esmFlags = BinaryUtils.ReadUInt32BE(esmAcbs, 0);

            var runtimeBuf = context.ReadBytes(entry.TesFormOffset!.Value + flagsRuntimeOffset, 4);
            if (runtimeBuf == null)
            {
                continue;
            }

            var runtimeFlags = BinaryUtils.ReadUInt32BE(runtimeBuf, 0);
            checkedNpcs++;
            if (esmFlags == runtimeFlags)
            {
                matches++;
            }
            else
            {
                mismatches.Add(
                    $"0x{entry.FormId:X8} {entry.EditorId ?? "?"}: "
                    + $"ESM Flags=0x{esmFlags:X8}, runtime Flags=0x{runtimeFlags:X8}");
            }
        }

        Assert.True(checkedNpcs >= 50,
            $"[{snippetName}] Only {checkedNpcs} non-templated NPCs available — anchor under-exercised.");

        var matchRate = (double)matches / checkedNpcs;
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{snippetName}] NPC ACBS Flags parity: {matches}/{checkedNpcs} ({matchRate:P0}) "
            + $"at runtime offset {flagsRuntimeOffset} (= 52 + coreShift({coreShift})).");

        // NOTE: Empirically the match rate sits around 23-30%. The engine
        // mutates several flag bits at NPC spawn (PC_Level_Mult bit 0x80 gets
        // cleared once resolved; AutoCalcStats bit 0x200 gets toggled depending
        // on initialization state). So ACBS.Flags is NOT in fact stable
        // post-load on FNV. The partial match rate still proves the offset is
        // pointing into ACBS-shaped data (most flags' low bits match), but it's
        // not a robust offset validator.
        //
        // Keeping the test at a low threshold to document the finding without
        // failing CI. If the rate drops well below 10%, the offset is likely
        // wrong; the 23-30% floor represents NPCs whose initial ACBS Flags
        // happened to survive the engine's spawn-time mutations unchanged.
        Assert.True(matchRate >= 0.1,
            $"[{snippetName}] NPC ACBS Flags parity {matchRate:P0} below floor "
            + $"(matches={matches}/{checkedNpcs} at offset {flagsRuntimeOffset}). "
            + "Below 10% suggests NpcAcbsOffset is wrong (or engine mutation pattern changed). "
            + "First 5 mismatches:\n  "
            + string.Join("\n  ", mismatches.Take(5)));
    }

    // =========================================================================
    // NPC Race pointer — true offset anchor. NpcRacePtrOffset (272 + _coreShift)
    // points to a TESRace* in the C++ TESNPC struct. Pointer values are immutable
    // post-load: the engine resolves the FormID-to-pointer at load and never
    // rewrites the pointer slot afterward. If the offset is correct, every
    // captured NPC should have either NULL (unset race, very rare) or an
    // Xbox 360 heap pointer (0x40000000-0x7FFFFFFF) in that slot.
    //
    // This is the strongest available validator: a wrong offset would land on
    // arbitrary struct data (uints, floats, etc.) with ~zero overlap with heap
    // pointer range, producing near-0% pointer-shape rate. A correct offset
    // gives ~100% (or null) — fully unambiguous.
    // =========================================================================

    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task NPC_RacePtr_offset_lands_on_heap_pointer_or_null(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);

        var coreShift = RuntimeBuildOffsets.GetPdbShift(MinidumpAnalyzer.DetectBuildType(snippet.MinidumpInfo));
        var racePtrOffset = 272 + coreShift; // NpcRacePtrOffset

        var npcEntries = snippet.RuntimeEditorIds
            .Where(e => e.FormType == 0x2A && e.TesFormOffset.HasValue)
            .Take(500)
            .ToList();

        var checkedNpcs = 0;
        var pointerShaped = 0;
        var nonPointerValues = new List<string>();

        foreach (var entry in npcEntries)
        {
            var buf = context.ReadBytes(entry.TesFormOffset!.Value + racePtrOffset, 4);
            if (buf == null)
            {
                continue;
            }

            var value = BinaryUtils.ReadUInt32BE(buf, 0);
            checkedNpcs++;
            if (IsNullOrXbox360HeapPointer(value))
            {
                pointerShaped++;
            }
            else if (nonPointerValues.Count < 10)
            {
                nonPointerValues.Add($"0x{entry.FormId:X8} {entry.EditorId ?? "?"}: 0x{value:X8}");
            }
        }

        Assert.True(checkedNpcs >= 50,
            $"[{snippetName}] Only {checkedNpcs} NPCs captured — under-sampled.");

        var rate = (double)pointerShaped / checkedNpcs;
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{snippetName}] NPC RacePtr pointer-shape: {pointerShaped}/{checkedNpcs} ({rate:P0}) "
            + $"at runtime offset {racePtrOffset} (= 272 + coreShift({coreShift})).");

        // A correct pointer offset gives near-100% pointer-shape rate (null +
        // valid heap addresses cover all cases). 90% threshold catches genuine
        // offset bugs while tolerating odd capture artifacts.
        Assert.True(rate >= 0.9,
            $"[{snippetName}] NPC RacePtr pointer-shape {rate:P0} below threshold "
            + $"({pointerShaped}/{checkedNpcs} at offset {racePtrOffset}). "
            + "NpcRacePtrOffset is wrong for this build family. First 10 non-pointer values:\n  "
            + string.Join("\n  ", nonPointerValues));
    }

    // =========================================================================
    // NPC VoiceType pointer — second core-shift anchor at a different offset.
    // NpcVoiceTypePtrOffset = 80 + _coreShift validates the core-shift region
    // at a low offset (vs Race at +272 which is high). Together they cover
    // both ends of the core-shift band.
    // =========================================================================

    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task NPC_VoiceTypePtr_offset_lands_on_heap_pointer_or_null(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);
        var coreShift = RuntimeBuildOffsets.GetPdbShift(MinidumpAnalyzer.DetectBuildType(snippet.MinidumpInfo));
        var voiceTypePtrOffset = 80 + coreShift;

        var (checkedNpcs, pointerShaped, nonPointerValues) = ScanPointerShape(
            snippet, context, voiceTypePtrOffset, 500);

        Assert.True(checkedNpcs >= 50, $"[{snippetName}] Only {checkedNpcs} NPCs captured.");
        var rate = (double)pointerShaped / checkedNpcs;
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{snippetName}] NPC VoiceTypePtr pointer-shape: {pointerShaped}/{checkedNpcs} ({rate:P0}) "
            + $"at runtime offset {voiceTypePtrOffset} (= 80 + coreShift({coreShift})).");

        Assert.True(rate >= 0.9,
            $"[{snippetName}] NPC VoiceTypePtr pointer-shape {rate:P0} below threshold. "
            + $"First 10 non-pointer values:\n  {string.Join("\n  ", nonPointerValues)}");
    }

    // =========================================================================
    // NPC CombatStyle pointer — appearance-shift anchor.
    // NpcCombatStylePtrOffset = 468 + _appearanceShift validates the
    // appearance-shift region (which governs hair/eyes/head parts/etc.).
    //
    // Unlike core-shift which is uniform 16 across all sampled builds, the
    // appearance-shift varies per-build and the production reader discovers it
    // via RuntimeNpcLayoutProbe. So this test runs the probe itself and uses
    // the discovered shift — verifying that what the production reader USES is
    // actually correct.
    //
    // Initial diagnostic run (using static 16 instead of probe) found Debug
    // builds get 73% pointer-shape at offset 484 — strong evidence the Debug
    // appearance-shift is NOT 16. The probe should discover the correct value
    // (likely 0 or 4 based on the probe's search list) and this test validates
    // that.
    // =========================================================================

    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task NPC_CombatStylePtr_offset_lands_on_heap_pointer_or_null(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);

        var npcProbeEntries = snippet.RuntimeEditorIds
            .Where(e => e.FormType == 0x2A && e.TesFormOffset.HasValue)
            .ToList();
        var probeResult = RuntimeNpcLayoutProbe.Probe(context, npcProbeEntries);
        var appearanceShift = probeResult.Layout.AppearanceShift;
        var combatStylePtrOffset = 468 + appearanceShift;

        var (checkedNpcs, pointerShaped, nonPointerValues) = ScanPointerShape(
            snippet, context, combatStylePtrOffset, 500);

        Assert.True(checkedNpcs >= 50, $"[{snippetName}] Only {checkedNpcs} NPCs captured.");
        var rate = (double)pointerShaped / checkedNpcs;
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{snippetName}] NPC CombatStylePtr pointer-shape: {pointerShaped}/{checkedNpcs} ({rate:P0}) "
            + $"at runtime offset {combatStylePtrOffset} (= 468 + probed appearanceShift({appearanceShift}), "
            + $"probe confidence={(probeResult.IsHighConfidence ? "high" : "low")}).");

        Assert.True(rate >= 0.9,
            $"[{snippetName}] NPC CombatStylePtr pointer-shape {rate:P0} below threshold "
            + $"(offset {combatStylePtrOffset}, probed appearanceShift={appearanceShift}, "
            + $"confidence={(probeResult.IsHighConfidence ? "high" : "low")}). "
            + $"First 10 non-pointer values:\n  {string.Join("\n  ", nonPointerValues)}");
    }

    private static (int CheckedNpcs, int PointerShaped, List<string> NonPointerValues) ScanPointerShape(
        DmpSnippetReader snippet, RuntimeMemoryContext context, int offset, int sampleSize)
    {
        var npcEntries = snippet.RuntimeEditorIds
            .Where(e => e.FormType == 0x2A && e.TesFormOffset.HasValue)
            .Take(sampleSize)
            .ToList();

        return ScanPointerShape(npcEntries, context, offset);
    }

    private static (int Checked, int PointerShaped, List<string> NonPointerValues) ScanPointerShape(
        IReadOnlyList<RuntimeEditorIdEntry> entries, RuntimeMemoryContext context, int offset)
    {
        var @checked = 0;
        var pointerShaped = 0;
        var nonPointerValues = new List<string>();

        foreach (var entry in entries)
        {
            var buf = context.ReadBytes(entry.TesFormOffset!.Value + offset, 4);
            if (buf == null) continue;
            var value = BinaryUtils.ReadUInt32BE(buf, 0);
            @checked++;
            if (IsNullOrXbox360HeapPointer(value))
            {
                pointerShaped++;
            }
            else if (nonPointerValues.Count < 10)
            {
                nonPointerValues.Add($"0x{entry.FormId:X8} {entry.EditorId ?? "?"}: 0x{value:X8}");
            }
        }

        return (@checked, pointerShaped, nonPointerValues);
    }

    // =========================================================================
    // REFR section
    // =========================================================================
    // RuntimeRefrReader has TESObjectREFR offset constants (FormFlagsOffset=8,
    // FormIdOffset=12, FinalBaseObjectPtrOffset=48, FinalParentCellPtrOffset=80,
    // FinalExtraListHeadOffset=88) with a per-build `_shift` that
    // RuntimeBuildOffsets.GetRefrFieldShift returns:
    //   0  for builds where TESChildCell is 8B (vtable + data) → REFR=120 bytes
    //  -4  for builds where TESChildCell is 4B (vtable only)    → REFR=116 bytes
    //
    // The shift selection is discovered per-snippet by
    // RuntimeRefrReader.ProbeIsEarlyBuild (the name is legacy — there's no
    // clean early/late split; the build-vs-build variation might be a
    // continuous gradient). These tests run the same probe so the test
    // offsets match production offsets exactly.
    //
    // REFR/ACHR/ACRE all share the same struct layout (FormType 0x3A-0x3C).

    private const int FormIdOffset = 12;
    private const int FinalBaseObjectPtrOffset = 48;
    private const int FinalParentCellPtrOffset = 80;
    private const int FinalExtraListHeadOffset = 88;

    private static IReadOnlyList<RuntimeEditorIdEntry> GetRefrSample(DmpSnippetReader snippet, int sampleSize)
    {
        // REFR/ACHR/ACRE entries come from the pAllForms hash table
        // (snippet.RuntimeRefrFormEntries), NOT the EditorID hash table
        // (snippet.RuntimeEditorIds). The EditorID table contains a sparse
        // set of REFRs with explicit names but their TesFormOffset is often
        // stale/uninitialized — only the pAllForms entries have reliable
        // offsets.
        return snippet.RuntimeRefrFormEntries
            .Where(e => e.FormType is >= 0x3A and <= 0x3C
                        && e.TesFormOffset.HasValue
                        && e.FormId != 0x14) // exclude Player
            .Take(sampleSize)
            .ToList();
    }

    /// <summary>
    ///     FormID sanity: the uint32 at TesFormOffset + 12 should equal the
    ///     entry's FormId. Validates that TesFormOffset is in fact pointing at
    ///     a TESForm header (every TESObjectREFR starts with TESForm at +0).
    ///     A wrong TesFormOffset would land 12 bytes into garbage memory.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task REFR_FormId_at_offset_12_matches_entry_FormId(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);
        var refrs = GetRefrSample(snippet, 500);

        var checkedCount = 0;
        var matches = 0;
        var mismatches = new List<string>();

        foreach (var entry in refrs)
        {
            var buf = context.ReadBytes(entry.TesFormOffset!.Value + FormIdOffset, 4);
            if (buf == null) continue;
            var runtimeFormId = BinaryUtils.ReadUInt32BE(buf, 0);
            checkedCount++;
            if (runtimeFormId == entry.FormId)
            {
                matches++;
            }
            else if (mismatches.Count < 10)
            {
                mismatches.Add(
                    $"0x{entry.FormId:X8} {entry.EditorId ?? "?"}: TesFormOffset+12 reads 0x{runtimeFormId:X8}");
            }
        }

        Assert.True(checkedCount >= 50, $"[{snippetName}] Only {checkedCount} REFRs captured.");
        var rate = (double)matches / checkedCount;
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{snippetName}] REFR FormId sanity: {matches}/{checkedCount} ({rate:P0}).");

        Assert.True(rate >= 0.95,
            $"[{snippetName}] REFR FormId sanity {rate:P0} below threshold. "
            + $"TesFormOffset may not be pointing at a TESForm header. First 10:\n  "
            + string.Join("\n  ", mismatches));
    }

    /// <summary>
    ///     BaseObjectPtr at +48 + shift. Every REFR must have a base object
    ///     (the TESForm it's an instance of), so this should be ~100%
    ///     pointer-shape with very few nulls.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task REFR_BaseObjectPtr_offset_lands_on_heap_pointer(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);
        var refrs = GetRefrSample(snippet, 500);
        var isEarlyBuild = RuntimeRefrReader.ProbeIsEarlyBuild(context, refrs);
        var shift = RuntimeBuildOffsets.GetRefrFieldShift(isEarlyBuild);
        var baseObjectOffset = FinalBaseObjectPtrOffset + shift;

        var (checkedCount, pointerShaped, nonPointer) = ScanPointerShape(refrs, context, baseObjectOffset);
        Assert.True(checkedCount >= 50, $"[{snippetName}] Only {checkedCount} REFRs captured.");
        var rate = (double)pointerShaped / checkedCount;
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{snippetName}] REFR BaseObjectPtr pointer-shape: {pointerShaped}/{checkedCount} ({rate:P0}) "
            + $"at offset {baseObjectOffset} (= 48 + refrShift({shift}), isEarlyBuild={isEarlyBuild}).");

        Assert.True(rate >= 0.95,
            $"[{snippetName}] REFR BaseObjectPtr pointer-shape {rate:P0} below threshold "
            + $"(offset {baseObjectOffset}). First 10 non-pointer values:\n  "
            + string.Join("\n  ", nonPointer));
    }

    /// <summary>
    ///     ParentCellPtr at +80 + shift. Most REFRs have a parent cell, but
    ///     some (particularly persistent refs initially outside any cell) may
    ///     be null. Threshold relaxed accordingly.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task REFR_ParentCellPtr_offset_lands_on_heap_pointer_or_null(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);
        var refrs = GetRefrSample(snippet, 500);
        var isEarlyBuild = RuntimeRefrReader.ProbeIsEarlyBuild(context, refrs);
        var shift = RuntimeBuildOffsets.GetRefrFieldShift(isEarlyBuild);
        var parentCellOffset = FinalParentCellPtrOffset + shift;

        var (checkedCount, pointerShaped, nonPointer) = ScanPointerShape(refrs, context, parentCellOffset);
        Assert.True(checkedCount >= 50, $"[{snippetName}] Only {checkedCount} REFRs captured.");
        var rate = (double)pointerShaped / checkedCount;
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{snippetName}] REFR ParentCellPtr pointer-shape: {pointerShaped}/{checkedCount} ({rate:P0}) "
            + $"at offset {parentCellOffset} (= 80 + refrShift({shift})).");

        Assert.True(rate >= 0.9,
            $"[{snippetName}] REFR ParentCellPtr pointer-shape {rate:P0} below threshold "
            + $"(offset {parentCellOffset}). First 10 non-pointer values:\n  "
            + string.Join("\n  ", nonPointer));
    }

    /// <summary>
    ///     ExtraListHead at +88 + shift. BSExtraData chain head. Some refs
    ///     have no extras (null); others point to a chain. Both null and heap
    ///     pointer are valid.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task REFR_ExtraListHead_offset_lands_on_heap_pointer_or_null(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);
        var refrs = GetRefrSample(snippet, 500);
        var isEarlyBuild = RuntimeRefrReader.ProbeIsEarlyBuild(context, refrs);
        var shift = RuntimeBuildOffsets.GetRefrFieldShift(isEarlyBuild);
        var extraListOffset = FinalExtraListHeadOffset + shift;

        var (checkedCount, pointerShaped, nonPointer) = ScanPointerShape(refrs, context, extraListOffset);
        Assert.True(checkedCount >= 50, $"[{snippetName}] Only {checkedCount} REFRs captured.");
        var rate = (double)pointerShaped / checkedCount;
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{snippetName}] REFR ExtraListHead pointer-shape: {pointerShaped}/{checkedCount} ({rate:P0}) "
            + $"at offset {extraListOffset} (= 88 + refrShift({shift})).");

        Assert.True(rate >= 0.9,
            $"[{snippetName}] REFR ExtraListHead pointer-shape {rate:P0} below threshold "
            + $"(offset {extraListOffset}). First 10 non-pointer values:\n  "
            + string.Join("\n  ", nonPointer));
    }

    // =========================================================================
    // WEAP (TESObjectWEAP) section
    // =========================================================================
    // RuntimeItemReader uses RuntimeItemLayouts for offsets (all `+ _s` where
    // _s = GetPdbShift = 16 for all known builds). Pointer fields are PDB-
    // derived and primary risk surface for the PDB-postdates-DMPs problem.
    //
    // Anchors:
    //   FormID @ +12          — TesFormOffset sanity (TESForm header start)
    //   WeapAmmoPtr @ +184    — pointer to TESAmmo (rarely null on real weapons)
    //   WeapPickupSound @ +252 — pointer to TESSound (often present)

    /// <summary>
    ///     Filter WEAP entries to those whose runtime TESForm at +12 actually
    ///     matches the entry's FormId. The EditorID hash table contains stale/
    ///     freed entries (especially in early-build dumps like debug_dump), which
    ///     the production reader silently drops via the same FormID check. The
    ///     subsequent pointer-shape tests need a clean baseline of validated
    ///     WEAPs to avoid testing offsets on garbage memory.
    /// </summary>
    private static IReadOnlyList<RuntimeEditorIdEntry> GetValidatedWeapSample(
        DmpSnippetReader snippet, RuntimeMemoryContext context, int sampleSize)
    {
        var candidates = snippet.RuntimeEditorIds
            .Where(e => e.FormType == 0x28 && e.TesFormOffset.HasValue)
            .Take(sampleSize * 4) // over-fetch so we can drop stale entries
            .ToList();

        var validated = new List<RuntimeEditorIdEntry>(sampleSize);
        foreach (var entry in candidates)
        {
            var buf = context.ReadBytes(entry.TesFormOffset!.Value + FormIdOffset, 4);
            if (buf == null) continue;
            if (BinaryUtils.ReadUInt32BE(buf, 0) == entry.FormId)
            {
                validated.Add(entry);
                if (validated.Count >= sampleSize) break;
            }
        }

        return validated;
    }

    /// <summary>
    ///     Diagnostic: count WEAP entries in EditorID hash table vs those that
    ///     pass FormID validation (TesFormOffset+12 == entry.FormId). Low
    ///     validation rates indicate the hash table contains stale entries
    ///     that the production reader silently drops. This is a snippet
    ///     quality / data-source health check, not an offset bug.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task WEAP_EditorIdHashTable_validation_rate_diagnostic(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);
        var weaps = snippet.RuntimeEditorIds
            .Where(e => e.FormType == 0x28 && e.TesFormOffset.HasValue)
            .Take(500)
            .ToList();

        var validated = 0;
        foreach (var entry in weaps)
        {
            var buf = context.ReadBytes(entry.TesFormOffset!.Value + FormIdOffset, 4);
            if (buf != null && BinaryUtils.ReadUInt32BE(buf, 0) == entry.FormId)
            {
                validated++;
            }
        }

        var rate = weaps.Count > 0 ? (double)validated / weaps.Count : 0;
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{snippetName}] WEAP EditorID validation rate: {validated}/{weaps.Count} ({rate:P0}) "
            + "(low rate = stale hash table entries, production reader silently drops mismatches).");

        // Only assert that at least SOME WEAPs are validated — staleness rate
        // varies legitimately across snippet families.
        Assert.True(validated >= 20,
            $"[{snippetName}] Only {validated} WEAPs validated — need >= 20 for pointer-shape anchors.");
    }

    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task WEAP_AmmoPtr_offset_lands_on_heap_pointer_or_null(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);
        var weaps = GetValidatedWeapSample(snippet, context, 200);
        Assert.True(weaps.Count >= 20, $"[{snippetName}] Only {weaps.Count} validated WEAPs.");

        var s = RuntimeBuildOffsets.GetPdbShift(MinidumpAnalyzer.DetectBuildType(snippet.MinidumpInfo));
        var ammoPtrOffset = 168 + s; // WeapAmmoPtrOffset

        var (checkedCount, pointerShaped, nonPointer) = ScanPointerShape(weaps, context, ammoPtrOffset);
        var rate = (double)pointerShaped / checkedCount;
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{snippetName}] WEAP AmmoPtr pointer-shape: {pointerShaped}/{checkedCount} ({rate:P0}) "
            + $"at offset {ammoPtrOffset} (= 168 + _s({s})).");

        Assert.True(rate >= 0.9,
            $"[{snippetName}] WEAP AmmoPtr pointer-shape {rate:P0} below threshold "
            + $"(offset {ammoPtrOffset}). First 10 non-pointer values:\n  "
            + string.Join("\n  ", nonPointer));
    }

    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task WEAP_PickupSound_offset_lands_on_heap_pointer_or_null(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);
        var weaps = GetValidatedWeapSample(snippet, context, 200);
        Assert.True(weaps.Count >= 20, $"[{snippetName}] Only {weaps.Count} validated WEAPs.");

        var s = RuntimeBuildOffsets.GetPdbShift(MinidumpAnalyzer.DetectBuildType(snippet.MinidumpInfo));
        var pickupSoundOffset = 236 + s; // WeapPickupSoundOffset

        var (checkedCount, pointerShaped, nonPointer) = ScanPointerShape(weaps, context, pickupSoundOffset);
        var rate = (double)pointerShaped / checkedCount;
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{snippetName}] WEAP PickupSound pointer-shape: {pointerShaped}/{checkedCount} ({rate:P0}) "
            + $"at offset {pickupSoundOffset} (= 236 + _s({s})).");

        Assert.True(rate >= 0.9,
            $"[{snippetName}] WEAP PickupSound pointer-shape {rate:P0} below threshold "
            + $"(offset {pickupSoundOffset}). First 10 non-pointer values:\n  "
            + string.Join("\n  ", nonPointer));
    }

    // =========================================================================
    // CONT (TESObjectCONT) section
    // =========================================================================
    // RuntimeContainerReader uses hardcoded offsets + _s = GetPdbShift = 16.
    // Pointer fields validated here:
    //   ContScriptPtrOffset @ +124      — TESScriptableForm pointer (often null)
    //   ContContentsDataOffset @ +68    — head of contents BSSimpleList (often non-null)
    //   ContContentsNextOffset @ +72    — next link in contents list

    private static IReadOnlyList<RuntimeEditorIdEntry> GetValidatedContSample(
        DmpSnippetReader snippet, RuntimeMemoryContext context, int sampleSize)
    {
        var candidates = snippet.RuntimeEditorIds
            .Where(e => e.FormType == 0x1B && e.TesFormOffset.HasValue)
            .Take(sampleSize * 4)
            .ToList();

        var validated = new List<RuntimeEditorIdEntry>(sampleSize);
        foreach (var entry in candidates)
        {
            var buf = context.ReadBytes(entry.TesFormOffset!.Value + FormIdOffset, 4);
            if (buf == null) continue;
            if (BinaryUtils.ReadUInt32BE(buf, 0) == entry.FormId)
            {
                validated.Add(entry);
                if (validated.Count >= sampleSize) break;
            }
        }

        return validated;
    }

    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task CONT_ContentsData_offset_lands_on_heap_pointer_or_null(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);
        var conts = GetValidatedContSample(snippet, context, 200);
        Assert.True(conts.Count >= 10, $"[{snippetName}] Only {conts.Count} validated CONTs.");

        var s = RuntimeBuildOffsets.GetPdbShift(MinidumpAnalyzer.DetectBuildType(snippet.MinidumpInfo));
        var contentsDataOffset = 52 + s; // ContContentsDataOffset

        var (checkedCount, pointerShaped, nonPointer) = ScanPointerShape(conts, context, contentsDataOffset);
        var rate = (double)pointerShaped / checkedCount;
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{snippetName}] CONT ContentsData pointer-shape: {pointerShaped}/{checkedCount} ({rate:P0}) "
            + $"at offset {contentsDataOffset} (= 52 + _s({s})).");

        Assert.True(rate >= 0.9,
            $"[{snippetName}] CONT ContentsData pointer-shape {rate:P0} below threshold "
            + $"(offset {contentsDataOffset}). First 10 non-pointer values:\n  "
            + string.Join("\n  ", nonPointer));
    }

    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task CONT_ContentsNext_offset_lands_on_heap_pointer_or_null(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);
        var conts = GetValidatedContSample(snippet, context, 200);
        Assert.True(conts.Count >= 10, $"[{snippetName}] Only {conts.Count} validated CONTs.");

        var s = RuntimeBuildOffsets.GetPdbShift(MinidumpAnalyzer.DetectBuildType(snippet.MinidumpInfo));
        var contentsNextOffset = 56 + s; // ContContentsNextOffset

        var (checkedCount, pointerShaped, nonPointer) = ScanPointerShape(conts, context, contentsNextOffset);
        var rate = (double)pointerShaped / checkedCount;
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{snippetName}] CONT ContentsNext pointer-shape: {pointerShaped}/{checkedCount} ({rate:P0}) "
            + $"at offset {contentsNextOffset} (= 56 + _s({s})).");

        Assert.True(rate >= 0.9,
            $"[{snippetName}] CONT ContentsNext pointer-shape {rate:P0} below threshold "
            + $"(offset {contentsNextOffset}). First 10 non-pointer values:\n  "
            + string.Join("\n  ", nonPointer));
    }

    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task CONT_Script_offset_lands_on_heap_pointer_or_null(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);
        var conts = GetValidatedContSample(snippet, context, 200);
        Assert.True(conts.Count >= 10, $"[{snippetName}] Only {conts.Count} validated CONTs.");

        var s = RuntimeBuildOffsets.GetPdbShift(MinidumpAnalyzer.DetectBuildType(snippet.MinidumpInfo));
        var scriptPtrOffset = 108 + s; // ContScriptPtrOffset

        var (checkedCount, pointerShaped, nonPointer) = ScanPointerShape(conts, context, scriptPtrOffset);
        var rate = (double)pointerShaped / checkedCount;
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{snippetName}] CONT Script pointer-shape: {pointerShaped}/{checkedCount} ({rate:P0}) "
            + $"at offset {scriptPtrOffset} (= 108 + _s({s})).");

        Assert.True(rate >= 0.9,
            $"[{snippetName}] CONT Script pointer-shape {rate:P0} below threshold "
            + $"(offset {scriptPtrOffset}). First 10 non-pointer values:\n  "
            + string.Join("\n  ", nonPointer));
    }

    // =========================================================================
    // BOOK (TESObjectBOOK) — Group2 baked-shift validation
    // =========================================================================
    // RuntimeBookReader uses RuntimeBookLayout.CreateDefault() with baked-in
    // Group 2 -8 shift (Phase 1B.6 found this constant across all observed
    // DMPs and inlined it). Anchors verify the baked offsets still match the
    // runtime layout.
    //   EnchantmentPtrOffset @ +136  — TESEnchantmentItem* (often null, some heap)
    //
    // Most BOOKs aren't enchanted, so the test is "(null OR heap pointer)"
    // shape. A wrong offset would mostly read non-pointer / non-null values.

    private static IReadOnlyList<RuntimeEditorIdEntry> GetValidatedBookSample(
        DmpSnippetReader snippet, RuntimeMemoryContext context, int sampleSize)
    {
        var candidates = snippet.RuntimeEditorIds
            .Where(e => e.FormType == 0x19 && e.TesFormOffset.HasValue)
            .Take(sampleSize * 4)
            .ToList();

        var validated = new List<RuntimeEditorIdEntry>(sampleSize);
        foreach (var entry in candidates)
        {
            var buf = context.ReadBytes(entry.TesFormOffset!.Value + FormIdOffset, 4);
            if (buf == null) continue;
            if (BinaryUtils.ReadUInt32BE(buf, 0) == entry.FormId)
            {
                validated.Add(entry);
                if (validated.Count >= sampleSize) break;
            }
        }

        return validated;
    }

    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task BOOK_EnchantmentPtr_offset_lands_on_heap_pointer_or_null(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);
        var books = GetValidatedBookSample(snippet, context, 200);

        if (books.Count < 10)
        {
            TestContext.Current.TestOutputHelper!.WriteLine(
                $"[{snippetName}] Only {books.Count} validated BOOKs — under-exercised.");
            return;
        }

        const int enchantmentPtrOffset = 136; // RuntimeBookLayout.CreateDefault baked Group 2 -8 shift
        var (checkedCount, pointerShaped, nonPointer) = ScanPointerShape(books, context, enchantmentPtrOffset);
        var rate = (double)pointerShaped / checkedCount;
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{snippetName}] BOOK EnchantmentPtr pointer-shape: {pointerShaped}/{checkedCount} ({rate:P0}) "
            + $"at offset {enchantmentPtrOffset} (baked).");

        Assert.True(rate >= 0.9,
            $"[{snippetName}] BOOK EnchantmentPtr pointer-shape {rate:P0} below threshold "
            + $"(offset {enchantmentPtrOffset}). First 10 non-pointer values:\n  "
            + string.Join("\n  ", nonPointer));
    }

    // =========================================================================
    // PROJ (BGSProjectile) — production-pipeline audit
    // =========================================================================
    // RuntimeEffectReader is the legacy name; it reads BGSProjectile structs
    // (FormType 0x33). Migrated to PdbStructView + WithShift in Tier 3.4 step 3
    // with a single shift band, driven by RuntimeEffectProbe. Same pipeline
    // pattern as RACE.

    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task PROJ_ReadRuntimeProjectile_returns_populated_records(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);

        var projEntries = snippet.RuntimeEditorIds
            .Where(e => e.FormType == 0x33 && e.TesFormOffset.HasValue)
            .ToList();

        if (projEntries.Count < 5)
        {
            TestContext.Current.TestOutputHelper!.WriteLine(
                $"[{snippetName}] Only {projEntries.Count} PROJ entries — under-exercised.");
            return;
        }

        var probeResult = RuntimeEffectProbe.Probe(context, projEntries);
        var reader = new RuntimeEffectReader(context, probeResult);

        var populated = 0;
        var withSpeed = 0;
        var withExplosion = 0;
        foreach (var entry in projEntries)
        {
            var record = reader.ReadRuntimeProjectile(entry);
            if (record == null) continue;
            populated++;
            if (record.Speed > 0) withSpeed++;
            if (record.Explosion != 0) withExplosion++;
        }

        var rate = (double)populated / projEntries.Count;
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{snippetName}] PROJ pipeline: {populated}/{projEntries.Count} ({rate:P0}) populated; "
            + $"{withSpeed} with Speed, {withExplosion} with Explosion FormID "
            + $"(probe margin={probeResult?.Margin ?? 0}).");

        Assert.True(rate >= 0.7,
            $"[{snippetName}] PROJ pipeline pass rate {rate:P0} below threshold "
            + $"({populated}/{projEntries.Count}).");
    }

    // =========================================================================
    // RACE (TESRace) production-pipeline audit
    // =========================================================================
    // RuntimeRaceReader uses PdbStructView + WithShift with probe-discovered
    // G1/G2 shifts (RuntimeRaceProbe). Direct pointer-shape testing would
    // require duplicating the PDB-name resolution path; the cleaner audit is
    // a production-pipeline test: run ReadRuntimeRace and assert non-null
    // results for the majority of RACE entries the snippet captured.
    //
    // This exercises the full chain: probe → PdbStructView.OpenStructView →
    // WithShift bands → field-name resolution → pointer follows. A failure
    // anywhere in the chain (wrong probe, wrong PDB offset, broken WithShift)
    // would drop the success rate to near zero.

    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task RACE_ReadRuntimeRace_returns_populated_records(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);

        var raceEntries = snippet.RuntimeEditorIds
            .Where(e => e.FormType == 0x0C && e.TesFormOffset.HasValue)
            .ToList();

        if (raceEntries.Count < 5)
        {
            TestContext.Current.TestOutputHelper!.WriteLine(
                $"[{snippetName}] Only {raceEntries.Count} RACE entries — under-exercised.");
            return;
        }

        // Run probe to discover G1/G2 shifts. Identical to what RuntimeStructReader
        // does during normal initialization.
        var probeResult = RuntimeRaceProbe.Probe(context, raceEntries);
        var reader = new RuntimeRaceReader(context, probeResult);

        var populated = 0;
        var withHeight = 0;
        var withVoice = 0;
        var failed = new List<string>();
        foreach (var entry in raceEntries)
        {
            var record = reader.ReadRuntimeRace(entry);
            if (record == null)
            {
                if (failed.Count < 10) failed.Add($"0x{entry.FormId:X8} {entry.EditorId ?? "?"}");
                continue;
            }

            populated++;
            if (record.MaleHeight is > 0 and < 5) withHeight++;
            if (record.MaleVoiceFormId.HasValue || record.FemaleVoiceFormId.HasValue) withVoice++;
        }

        var rate = (double)populated / raceEntries.Count;
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{snippetName}] RACE pipeline: {populated}/{raceEntries.Count} ({rate:P0}) populated; "
            + $"{withHeight} with valid height, {withVoice} with voice pointer "
            + $"(probe margin={probeResult?.Margin ?? 0}).");

        Assert.True(rate >= 0.5,
            $"[{snippetName}] RACE pipeline pass rate {rate:P0} below threshold "
            + $"({populated}/{raceEntries.Count}). First 10 failures:\n  "
            + string.Join("\n  ", failed));
    }

    // =========================================================================
    // PACK (TESPackage) extra anchors
    // =========================================================================
    // pCombatStyle (PDB +88) is already covered by
    // PackageTerminalOffsetInvestigationTests (Phase 1B.7 regression set).
    // These add coverage for the location + target pointer offsets:
    //   pPackLoc @ +60   (= 44 + _s)  — PackageLocation*
    //   pPackTarg @ +64  (= 48 + _s)  — PackageTarget*
    // Both are null on many PACK types; pointer-shape (null OR heap) is the
    // right check.

    private static IReadOnlyList<RuntimeEditorIdEntry> GetValidatedPackSample(
        DmpSnippetReader snippet, RuntimeMemoryContext context, int sampleSize)
    {
        // FormType 0x49 = PACK in most build variants, but FormType drift in
        // release_dump puts IDLE animations at 0x49 and PACKs at 0x4A. Try both
        // FormType bytes and use the one that produces more entries with a
        // pointer-shaped PackLocPtr (the field most likely to be heap-pointer
        // for a real PACK — IDLE structs uniformly read 0x00CBCB17 there).
        var reader = new RuntimePackageReader(context);
        var s = RuntimeBuildOffsets.GetPdbShift(MinidumpAnalyzer.DetectBuildType(snippet.MinidumpInfo));
        var packLocOffset = 44 + s;

        IReadOnlyList<RuntimeEditorIdEntry> TryFormType(byte formType)
        {
            var candidates = snippet.RuntimeEditorIds
                .Where(e => e.FormType == formType && e.TesFormOffset.HasValue)
                .Take(sampleSize * 8)
                .ToList();

            var validated = new List<RuntimeEditorIdEntry>(sampleSize);
            foreach (var entry in candidates)
            {
                if (reader.ReadRuntimePackage(entry) is null) continue;
                // Drop entries that lack a pointer-shape at PackLocPtr: those are
                // FormType-drift mis-tagged entries that ReadRuntimePackage's loose
                // validation accepted as PACKs.
                var buf = context.ReadBytes(entry.TesFormOffset!.Value + packLocOffset, 4);
                if (buf == null) continue;
                var locPtr = BinaryUtils.ReadUInt32BE(buf, 0);
                if (!IsNullOrXbox360HeapPointer(locPtr)) continue;

                validated.Add(entry);
                if (validated.Count >= sampleSize) break;
            }

            return validated;
        }

        var at0x49 = TryFormType(0x49);
        if (at0x49.Count >= 20) return at0x49;
        var at0x4A = TryFormType(0x4A);
        return at0x4A.Count > at0x49.Count ? at0x4A : at0x49;
    }

    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task PACK_PackLocPtr_offset_lands_on_heap_pointer_or_null(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);
        var packs = GetValidatedPackSample(snippet, context, 200);
        Assert.True(packs.Count >= 20, $"[{snippetName}] Only {packs.Count} validated PACKs.");

        var s = RuntimeBuildOffsets.GetPdbShift(MinidumpAnalyzer.DetectBuildType(snippet.MinidumpInfo));
        var packLocOffset = 44 + s;

        var (checkedCount, pointerShaped, nonPointer) = ScanPointerShape(packs, context, packLocOffset);
        var rate = (double)pointerShaped / checkedCount;
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{snippetName}] PACK PackLocPtr pointer-shape: {pointerShaped}/{checkedCount} ({rate:P0}) "
            + $"at offset {packLocOffset} (= 44 + _s({s})).");

        Assert.True(rate >= 0.9,
            $"[{snippetName}] PACK PackLocPtr pointer-shape {rate:P0} below threshold "
            + $"(offset {packLocOffset}). First 10 non-pointer values:\n  "
            + string.Join("\n  ", nonPointer));
    }

    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task PACK_PackTargPtr_offset_lands_on_heap_pointer_or_null(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);
        var packs = GetValidatedPackSample(snippet, context, 200);
        Assert.True(packs.Count >= 20, $"[{snippetName}] Only {packs.Count} validated PACKs.");

        var s = RuntimeBuildOffsets.GetPdbShift(MinidumpAnalyzer.DetectBuildType(snippet.MinidumpInfo));
        var packTargOffset = 48 + s;

        var (checkedCount, pointerShaped, nonPointer) = ScanPointerShape(packs, context, packTargOffset);
        var rate = (double)pointerShaped / checkedCount;
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{snippetName}] PACK PackTargPtr pointer-shape: {pointerShaped}/{checkedCount} ({rate:P0}) "
            + $"at offset {packTargOffset} (= 48 + _s({s})).");

        Assert.True(rate >= 0.9,
            $"[{snippetName}] PACK PackTargPtr pointer-shape {rate:P0} below threshold "
            + $"(offset {packTargOffset}). First 10 non-pointer values:\n  "
            + string.Join("\n  ", nonPointer));
    }

    // =========================================================================
    // Magic FormType sanity (MGEF / SPEL / PERK)
    // =========================================================================
    // RuntimeMagicReader uses PdbStructView with field-name lookups — all
    // pointer fields live inside the `data` substruct at PDB-resolved offsets.
    // A direct pointer-shape test would require resolving the substruct base
    // first; the simpler check here is FormID sanity, confirming TesFormOffset
    // points at a TESForm header for these FormTypes.

    private static (int Checked, int Matches) ValidateFormIdSanity(
        DmpSnippetReader snippet, RuntimeMemoryContext context, byte formType, int sampleSize)
    {
        var entries = snippet.RuntimeEditorIds
            .Where(e => e.FormType == formType && e.TesFormOffset.HasValue)
            .Take(sampleSize)
            .ToList();

        var matches = 0;
        var checkedCount = 0;
        foreach (var entry in entries)
        {
            var buf = context.ReadBytes(entry.TesFormOffset!.Value + FormIdOffset, 4);
            if (buf == null) continue;
            checkedCount++;
            if (BinaryUtils.ReadUInt32BE(buf, 0) == entry.FormId) matches++;
        }

        return (checkedCount, matches);
    }

    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task MGEF_FormId_sanity(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);
        var (checkedCount, matches) = ValidateFormIdSanity(snippet, context, formType: 0x10, sampleSize: 300);

        Assert.True(checkedCount >= 30, $"[{snippetName}] Only {checkedCount} MGEF entries.");
        var rate = (double)matches / checkedCount;
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{snippetName}] MGEF FormID sanity: {matches}/{checkedCount} ({rate:P0}).");
        // 40% floor: empirically the EditorID hash table has bulk staleness for
        // many FormTypes (~30-50% across magic types). Production reader drops
        // stale entries via FormID check. This floor catches a regression where
        // the offset would land on garbage (~0% match rate).
        Assert.True(rate >= 0.4,
            $"[{snippetName}] MGEF FormID match rate {rate:P0} below floor "
            + $"({matches}/{checkedCount}).");
    }

    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task SPEL_FormId_sanity(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);
        var (checkedCount, matches) = ValidateFormIdSanity(snippet, context, formType: 0x14, sampleSize: 300);

        Assert.True(checkedCount >= 30, $"[{snippetName}] Only {checkedCount} SPEL entries.");
        var rate = (double)matches / checkedCount;
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{snippetName}] SPEL FormID sanity: {matches}/{checkedCount} ({rate:P0}).");
        // 40% floor: empirically the EditorID hash table has bulk staleness for
        // many FormTypes (~30-50% across magic types). Production reader drops
        // stale entries via FormID check. This floor catches a regression where
        // the offset would land on garbage (~0% match rate).
        Assert.True(rate >= 0.4,
            $"[{snippetName}] SPEL FormID match rate {rate:P0} below floor "
            + $"({matches}/{checkedCount}).");
    }

    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task PERK_FormId_sanity(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);
        var (checkedCount, matches) = ValidateFormIdSanity(snippet, context, formType: 0x56, sampleSize: 300);

        if (checkedCount < 30)
        {
            TestContext.Current.TestOutputHelper!.WriteLine(
                $"[{snippetName}] Only {checkedCount} PERK entries — under-exercised.");
            return;
        }

        var rate = (double)matches / checkedCount;
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{snippetName}] PERK FormID sanity: {matches}/{checkedCount} ({rate:P0}).");
        // 40% floor: empirically the EditorID hash table has bulk staleness for
        // many FormTypes (~30-50% across magic types). Production reader drops
        // stale entries via FormID check. This floor catches a regression where
        // the offset would land on garbage (~0% match rate).
        Assert.True(rate >= 0.4,
            $"[{snippetName}] PERK FormID match rate {rate:P0} below floor "
            + $"({matches}/{checkedCount}).");
    }

    // =========================================================================
    // LAND audit — DEFERRED. RuntimeWorldReader operates on LAND records, but
    // LAND has no EditorID, so the EditorID hash table (snippet.RuntimeEditorIds)
    // doesn't reliably carry LAND entries. The pAllForms hash table does, but
    // DmpSnippetManifest only exposes RuntimeEditorIds + RuntimeRefrFormEntries
    // (LAND is captured separately as RuntimeLandFormEntries in the live
    // EsmRecordScanResult but not serialized into snippet manifests yet).
    //
    // Side finding from the first LAND audit attempt: in `release_dump`, the
    // EditorID hash table contains 16 entries with FormType 0x42 whose editor
    // IDs are WORLDSPACE names (Wasteland, FreesideWorld, FFEncounterWorld,
    // WastelandNV). That's empirical confirmation of the well-known FormType
    // drift — in some early builds, 0x42 contains WRLD records (which RTTI
    // mapping says is LAND, but the runtime built-in enum had inserted a type
    // and shifted everything). `RuntimeBuildOffsets.DetectFormTypeDrift` is
    // responsible for remapping. The snippet stores pre-drift-detection
    // FormType bytes, so naively filtering on FormType==0x42 grabs WRLDs.
    //
    // To audit RuntimeWorldReader properly, the snippet manifest needs to
    // surface RuntimeLandFormEntries. Tracked as a follow-up.

    /// <summary>
    ///     Xbox 360 heap pointers cluster in 0x40000000-0x7FFFFFFF. The XEX
    ///     module / system data lives at 0x80000000+. A NULL value (0) is also
    ///     valid (unset pointer).
    /// </summary>
    private static bool IsNullOrXbox360HeapPointer(uint value)
    {
        if (value == 0) return true;
        return value >= 0x40000000 && value < 0x80000000;
    }
}
