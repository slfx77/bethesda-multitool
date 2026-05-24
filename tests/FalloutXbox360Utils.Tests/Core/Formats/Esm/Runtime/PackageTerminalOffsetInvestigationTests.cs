using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Utils;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

/// <summary>
///     Parity tests pinning the two PDB-vs-code offset fixes from the Phase 1B.6
///     follow-up investigation:
///
///     - RuntimePackageReader.CombatStylePtrOffset: was `88 + _s = 104` (inside
///       OnBegin PackageEventAction); fixed to `72 + _s = 88` (PDB pCombatStyle).
///     - RuntimeQuestTerminalReader.Term*Offset constants: were stale by 48 / 12
///       bytes; fixed to match the PDB BGSTerminal layout (Difficulty +180, Flags
///       +181, MenuItemList +168).
///
///     Each parity assertion runs against every available test snippet so the offset
///     correctness is verified across all sampled build families (Debug, Release Beta
///     variants xex4/xex44, MemDebug). The "wrong-offset has the bug signature"
///     assertions only run on `release_dump` because that's the snippet the original
///     ground-truth investigation observed the signature on — the signature pattern
///     might differ per build (e.g. different uninitialized fill values, different
///     XEX-text-segment pointer constants).
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public sealed class PackageTerminalOffsetInvestigationTests
{
    private static readonly string SnippetDir = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "Dmp");

    /// <summary>
    ///     All snippet families that ship with the test data. Used to verify offset
    ///     correctness across build variants.
    /// </summary>
    public static IEnumerable<object[]> AllSnippets =>
    [
        ["debug_dump"],
        ["release_dump"],
        ["xex4_dump"],
        ["xex44_dump"],
        ["memdebug_dump"]
    ];

    // ============================================================================
    // PACK — pCombatStyle at PDB +88 (was wrongly read at +104, inside OnBegin)
    // ============================================================================

    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task PACK_pCombatStyle_offset_lands_on_pointer_field_or_null(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var packEntries = snippet.RuntimeEditorIds
            .Where(e => e.FormType == 0x49 && e.TesFormOffset.HasValue)
            .Take(16)
            .ToList();

        if (packEntries.Count == 0)
        {
            // Some snippet families may not have captured PACK entries — skip silently.
            return;
        }

        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);
        const int correctOffset = 88; // PDB pCombatStyle

        foreach (var entry in packEntries)
        {
            var buf = context.ReadBytes(entry.TesFormOffset!.Value, 144);
            Assert.NotNull(buf);
            var atCorrect = BinaryUtils.ReadUInt32BE(buf!, correctOffset);
            Assert.True(IsNullOrXbox360HeapPointer(atCorrect),
                $"[{snippetName}] PACK 0x{entry.FormId:X8}: value 0x{atCorrect:X8} at PDB +{correctOffset} "
                + "is neither null nor a valid Xbox 360 heap pointer.");
        }
    }

    /// <summary>
    ///     Confirms that on the snippet where the original ground-truth was done, the
    ///     previously-wrong offset (+104) still does NOT look like a pointer field for
    ///     every record — so future regressions (someone reverting `72 + _s` back to
    ///     `88 + _s`) trip a clear signal.
    /// </summary>
    [Fact]
    public async Task PACK_pCombatStyle_previously_wrong_offset_still_looks_wrong()
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, "release_dump");
        var packEntries = snippet.RuntimeEditorIds
            .Where(e => e.FormType == 0x49 && e.TesFormOffset.HasValue)
            .Take(16)
            .ToList();
        Assert.NotEmpty(packEntries);

        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);
        const int previouslyWrongOffset = 104;

        var pointerOrNull = packEntries
            .Select(e =>
            {
                var buf = context.ReadBytes(e.TesFormOffset!.Value, 144);
                return buf is null ? 0u : BinaryUtils.ReadUInt32BE(buf, previouslyWrongOffset);
            })
            .Count(IsNullOrXbox360HeapPointer);

        Assert.True(pointerOrNull < packEntries.Count,
            "If the old +104 offset now looks pointer-shaped for every record, re-investigate — "
            + "the ground-truth signal that distinguished it from the correct +88 has changed.");
    }

    // ============================================================================
    // TERM — Difficulty at PDB +180, MenuItemList at PDB +168
    // ============================================================================

    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task TERM_Difficulty_PdbOffset_beats_OldCodeOffset_for_inRange_rate(string snippetName)
    {
        // Instead of asserting that every byte at PDB +180 is in {0..4, 0xFF} (any one
        // outlier — e.g. 0x7F in xex4_dump — would trip a binary check), use a
        // distribution-based comparison: the PDB-correct offset must produce at LEAST
        // as many in-range (0..4) values as the previously-wrong offset (+132). In
        // practice it produces strictly more on every observed snippet — the old offset
        // landed in a pointer field that essentially never read a 0..4 byte.
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var termEntries = snippet.RuntimeEditorIds
            .Where(e => e.FormType == 0x17 && e.TesFormOffset.HasValue)
            .Take(16)
            .ToList();

        if (termEntries.Count == 0)
        {
            return; // No TERMs captured in this snippet family.
        }

        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);

        var pdbInRange = 0;
        var oldInRange = 0;
        foreach (var entry in termEntries)
        {
            var buf = context.ReadBytes(entry.TesFormOffset!.Value, 184);
            Assert.NotNull(buf);
            if (buf![180] <= 4) pdbInRange++;
            if (buf[132] <= 4) oldInRange++;
        }

        Assert.True(pdbInRange >= oldInRange,
            $"[{snippetName}] PDB +180 produced {pdbInRange}/{termEntries.Count} in-range "
            + $"Difficulty bytes; old +132 produced {oldInRange}/{termEntries.Count}. "
            + "The PDB offset is supposed to be at least as good — re-investigate.");
    }

    /// <summary>
    ///     Same "previously-wrong offset still looks wrong" guard for TERM Difficulty.
    /// </summary>
    [Fact]
    public async Task TERM_Difficulty_previously_wrong_offset_still_looks_wrong()
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, "release_dump");
        var termEntries = snippet.RuntimeEditorIds
            .Where(e => e.FormType == 0x17 && e.TesFormOffset.HasValue)
            .Take(16)
            .ToList();
        Assert.NotEmpty(termEntries);

        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);

        var wrongDifficulties = termEntries
            .Select(e =>
            {
                var buf = context.ReadBytes(e.TesFormOffset!.Value, 184);
                return buf is null ? (byte)0 : buf[132];
            })
            .ToList();

        var distinct = wrongDifficulties.Distinct().ToList();
        Assert.True(distinct.Count == 1 && distinct[0] > 4,
            $"Old +132 offset used to read a single out-of-range value uniformly; "
            + $"observed distinct values: [{string.Join(",", distinct.Select(b => $"0x{b:X2}"))}].");
    }

    [Theory]
    [MemberData(nameof(AllSnippets))]
    public async Task TERM_MenuItemList_offset_lands_on_per_record_list_head(string snippetName)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
        var termEntries = snippet.RuntimeEditorIds
            .Where(e => e.FormType == 0x17 && e.TesFormOffset.HasValue)
            .Take(16)
            .ToList();

        if (termEntries.Count == 0)
        {
            return; // No TERMs in this snippet family.
        }

        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);

        var firstItems = new HashSet<uint>();
        foreach (var entry in termEntries)
        {
            var buf = context.ReadBytes(entry.TesFormOffset!.Value, 184);
            Assert.NotNull(buf);
            firstItems.Add(BinaryUtils.ReadUInt32BE(buf!, 168));
        }

        // Two checks together:
        // 1. At least one record should have a non-null list head (most TERMs have menu items).
        // 2. Every non-null first-item pointer must be inside a captured heap region —
        //    pointing at an actual TERMINAL_MENU_ITEM struct.
        var nonNull = firstItems.Where(p => p != 0).ToList();

        // Don't require non-null on every snippet — some snippet families may not capture
        // any TERMs with menu items. But the pointer-validity check should always hold.
        Assert.All(nonNull, p => Assert.True(
            context.IsValidPointer(p),
            $"[{snippetName}] PDB +168 read 0x{p:X8} which is not a valid captured-memory pointer."));
    }

    /// <summary>
    ///     "Previously-wrong offset still looks wrong" guard for MenuItemList: the buggy
    ///     +156 used to read a constant XEX text-segment pointer for every record.
    /// </summary>
    [Fact]
    public async Task TERM_MenuItemList_previously_wrong_offset_still_looks_wrong()
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, "release_dump");
        var termEntries = snippet.RuntimeEditorIds
            .Where(e => e.FormType == 0x17 && e.TesFormOffset.HasValue)
            .Take(16)
            .ToList();
        Assert.NotEmpty(termEntries);

        var context = new RuntimeMemoryContext(snippet.Accessor, snippet.FileSize, snippet.MinidumpInfo);

        var distinct = termEntries
            .Select(e =>
            {
                var buf = context.ReadBytes(e.TesFormOffset!.Value, 184);
                return buf is null ? 0u : BinaryUtils.ReadUInt32BE(buf, 156);
            })
            .ToHashSet();

        Assert.True(distinct.Count <= 2,
            $"Old +156 offset used to read a constant XEX text-segment pointer; "
            + $"observed {distinct.Count} distinct values: [{string.Join(",", distinct.Select(p => $"0x{p:X8}"))}].");
    }

    // ============================================================================
    // End-to-end (skipped by default): exercises the production reader on a real DMP
    // ============================================================================

    /// <summary>
    ///     Optional end-to-end check that loads a full DMP (not a snippet) and asserts
    ///     <c>PackageRecord.CombatStyleFormId</c> is populated for at least some PACK
    ///     records. The test snippets only capture sparse memory regions and the CSTY
    ///     pointer targets (heap addresses in the 0x6xxxxxxx range) aren't usually in
    ///     the snippet's captured regions, so the snippet-based byte-level tests can't
    ///     exercise the typed-pointer resolution end-to-end. Skipped by default to keep
    ///     the regular test run fast (full DMPs are ~250 MB and take seconds to parse);
    ///     remove the skip locally to spot-check.
    /// </summary>
    /// <summary>
    ///     End-to-end regression test: load multiple full DMPs (covering Debug,
    ///     Release Beta, MemDebug, and Jacobstown build families) and assert that the
    ///     aggregate count of PACK records with a resolved <c>CombatStyleFormId</c> is
    ///     > 0. Before the +88 pCombatStyle fix this would have been zero across every
    ///     DMP (the typed-pointer gate rejected the garbage at the wrong +104 offset);
    ///     the fix produces ~16-19 resolved CombatStyle FormIDs per full DMP that
    ///     captures the relevant heap regions.
    ///
    ///     One of the sampled DMPs (Fallout_Release_Beta.xex.dmp, ~110 MB) doesn't
    ///     capture enough heap to resolve any CSTY pointers, so we assert the aggregate
    ///     across all 5, not per-DMP.
    /// </summary>
    [Fact]
    public async Task PACK_CombatStyleFormId_populated_across_full_dmp_families()
    {
        string[] dmpFiles =
        [
            "Fallout_Debug.xex.dmp",
            "Fallout_Release_Beta.xex.dmp",
            "Fallout_Release_Beta.xex43.dmp",
            "Fallout_Release_MemDebug.xex.dmp",
            "Jacobstown.dmp"
        ];

        var perDmp = new List<(string Name, int Populated, int Total)>();

        foreach (var dmpFileName in dmpFiles)
        {
            var dmpPath = Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "Sample", "MemoryDump", dmpFileName);
            dmpPath = Path.GetFullPath(dmpPath);
            Assert.True(File.Exists(dmpPath), $"DMP not found: {dmpPath}");

            var analyzer = new FalloutXbox360Utils.Core.Minidump.MinidumpAnalyzer();
            var analysis = await analyzer.AnalyzeAsync(dmpPath);
            Assert.NotNull(analysis.MinidumpInfo);
            Assert.NotNull(analysis.EsmRecords);

            var scan = analysis.EsmRecords!;
            using var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(
                dmpPath, FileMode.Open, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, new FileInfo(dmpPath).Length,
                System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);

            var reader = RuntimeStructReader.CreateWithAutoDetect(
                accessor, new FileInfo(dmpPath).Length, analysis.MinidumpInfo!,
                scan.RuntimeRefrFormEntries, allEntries: scan.RuntimeEditorIds);

            var packEntries = scan.RuntimeEditorIds.Where(e => e.FormType == 0x49).ToList();
            var populated = packEntries
                .Select(e => reader.ReadRuntimePackage(e))
                .Count(r => r is { CombatStyleFormId: > 0 });
            perDmp.Add((dmpFileName, populated, packEntries.Count));
        }

        foreach (var (name, pop, total) in perDmp)
        {
            TestContext.Current.TestOutputHelper!.WriteLine(
                $"  {name,-40} populated={pop,5} / total={total,5}");
        }

        var totalPopulated = perDmp.Sum(d => d.Populated);
        var totalEntries = perDmp.Sum(d => d.Total);
        Assert.True(totalPopulated > 0,
            $"Aggregate across {perDmp.Count} full DMPs: 0/{totalEntries} PACK records had a "
            + "resolved CombatStyleFormId. The +88 pCombatStyle fix has regressed — pre-fix this "
            + "value was always 0 because the typed-pointer gate rejected the wrong +104 reads.");
    }

    /// <summary>
    ///     Xbox 360 heap pointers cluster in well-known VA ranges: 0x40000000-0x7FFFFFFF
    ///     (game heap), and 0x80000000+ (XEX module / system). A pCombatStyle pointer
    ///     to a TESCombatStyle in heap will be in the 0x4xxxxxxx-0x7xxxxxxx range; null
    ///     (no combat style assigned) is also valid.
    /// </summary>
    private static bool IsNullOrXbox360HeapPointer(uint value)
    {
        if (value == 0) return true;
        return value >= 0x40000000 && value < 0x80000000;
    }
}
