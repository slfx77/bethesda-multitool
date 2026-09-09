using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Travels.OblivionMobile;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Opt-in checks of the Oblivion mobile MIDlet JAR (<c>RUN_BUCKET_B=1</c>). The <c>.scr</c>
///     format has no magic, no length fields and no padding: the ONLY evidence that the operand
///     widths are right is that decoding every chunk lands exactly on the next label marker, and
///     that the label table, the definition blocks, the markers and the instructions together
///     account for every byte of every file. That is what the counts pinned here mean — they are
///     exact because the JAR is a fixed fixture, and a single wrong width moves several of them.
///     <para>
///         Retail: 32 scripts, 394 labels, 570 definition blocks, 3,844 instructions, 179 CALLs,
///         13 lang tables with 546 records.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class OblivionMobileScriptRetailTests
{
    /// <summary>
    ///     Every instruction the corpus contains, by opcode. Reproduced from the 2026-09-05 survey;
    ///     61 of the 79 interpreter cases occur, the other 18 never do.
    /// </summary>
    private static readonly Dictionary<byte, int> ExpectedOpcodeCensus = new()
    {
        [0] = 1, [2] = 395, [3] = 39, [7] = 3, [8] = 28, [9] = 4, [10] = 5, [11] = 152,
        [12] = 30, [15] = 233, [16] = 5, [17] = 117, [18] = 121, [19] = 162, [20] = 5,
        [21] = 77, [22] = 92, [23] = 179, [24] = 3, [25] = 19, [26] = 95, [27] = 14,
        [29] = 36, [32] = 24, [34] = 240, [35] = 1, [36] = 31, [37] = 9, [39] = 85,
        [40] = 63, [41] = 37, [42] = 53, [43] = 2, [44] = 1, [45] = 11, [46] = 9, [47] = 7,
        [49] = 40, [50] = 105, [51] = 44, [52] = 15, [53] = 162, [56] = 20, [58] = 47,
        [59] = 8, [60] = 1, [61] = 1, [64] = 18, [66] = 1, [67] = 44, [68] = 6, [69] = 25,
        [70] = 17, [71] = 39, [72] = 684, [73] = 30, [74] = 30, [75] = 17, [76] = 89,
        [77] = 1, [78] = 12
    };

    /// <summary>Definition blocks by kind. Kind 3 is absent because the engine has no reader for it.</summary>
    private static readonly Dictionary<OblivionMobileBlockKind, int> ExpectedBlockCensus = new()
    {
        [OblivionMobileBlockKind.ActorTemplate] = 20,
        [OblivionMobileBlockKind.Armor] = 41,
        [OblivionMobileBlockKind.Potion] = 10,
        [OblivionMobileBlockKind.Weapon] = 36,
        [OblivionMobileBlockKind.PlayerClass] = 8,
        [OblivionMobileBlockKind.TileAttribute] = 248,
        [OblivionMobileBlockKind.RawByteList] = 13,
        [OblivionMobileBlockKind.Spell] = 9,
        [OblivionMobileBlockKind.LevelParameters] = 13,
        [OblivionMobileBlockKind.LootEntry] = 172
    };

    /// <summary>
    ///     Opcode 34's sub-commands. 11 of the 17 that carry an argument occur; the distribution is
    ///     the direct evidence for the width rule, since reading 14 (a u16) as a byte would leave
    ///     69 chunks short by one byte each.
    /// </summary>
    private static readonly Dictionary<int, int> ExpectedSubCommandCensus = new()
    {
        [3] = 18, [4] = 3, [7] = 32, [8] = 10, [10] = 24, [13] = 9,
        [14] = 69, [15] = 29, [18] = 24, [19] = 10, [20] = 12
    };

    /// <summary>Per lang file: record count, lowest id, highest id.</summary>
    private static readonly (int Index, int Count, int MinId, int MaxId)[] ExpectedLangTables =
    [
        (0, 305, 1, 574), (1, 34, 43, 550), (2, 46, 57, 399), (3, 9, 98, 345),
        (4, 16, 106, 362), (5, 6, 217, 565), (6, 42, 220, 494), (7, 20, 235, 546),
        (8, 16, 245, 495), (9, 30, 255, 495), (10, 9, 264, 495), (11, 7, 270, 409),
        (12, 6, 275, 547)
    ];

    /// <summary>
    ///     The overlay in force for each script once the LOADSCR chain is walked. The 20 scripts
    ///     that call LOADLANG themselves come from the spec's table of which file loads which
    ///     overlay; the other 12 inherit along the chain
    ///     <c>
    ///         l01_1 -> l01_1r -> {l01_1b, l01_1c} ->
    ///         l02_2_1 -> l02_2 -> ... -> l12_12 -> end_15
    ///     </c>
    ///     and <c>startup -> startup2</c>.
    ///     <c>startup.scr</c> is the one script with no overlay at all: it resolves out of lang_0.
    /// </summary>
    private static readonly Dictionary<string, int?> ExpectedOverlayInForce = new(StringComparer.OrdinalIgnoreCase)
    {
        ["startup.scr"] = null,
        ["startup2.scr"] = 0,
        ["l01_1.scr"] = 1,
        ["l01_1r.scr"] = 1,
        ["l01_1b.scr"] = 1,
        ["l01_1c.scr"] = 1,
        ["l02_2_1.scr"] = 2,
        ["l02_2.scr"] = 2,
        ["l03_3.scr"] = 3,
        ["l04_4.scr"] = 4,
        ["l04_4r.scr"] = 4,
        ["l04_4b.scr"] = 4,
        ["l05_5.scr"] = 5,
        ["l06_6_cr.scr"] = 6,
        ["l06_6.scr"] = 6,
        ["l06_a.scr"] = 6,
        ["l06_6a.scr"] = 6,
        ["l06_6b.scr"] = 6,
        ["l06_6_ba.scr"] = 6,
        ["l06_b.scr"] = 6,
        ["l07_7_cr.scr"] = 7,
        ["l07_7.scr"] = 7,
        ["l08_8_cr.scr"] = 8,
        ["l08_8.scr"] = 8,
        ["l09_9_cr.scr"] = 9,
        ["l09_9.scr"] = 9,
        ["l10_10_cr.scr"] = 10,
        ["l10_10.scr"] = 10,
        ["l11_11_cr.scr"] = 11,
        ["l11_11.scr"] = 11,
        ["l12_12.scr"] = 12,
        ["end_15.scr"] = 7
    };

    /// <summary>Every <c>.scr</c> in the JAR, paired with the bytes it was decoded from.</summary>
    private static List<(string Name, byte[] Bytes, OblivionMobileScript Script)> LoadEveryScript()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var jar = RealAssetPaths.Travels.OblivionMobileJar();
        Assert.SkipWhen(jar is null, RealAssetPaths.SkipMessage("the Oblivion mobile JAR"));

        using var reader = ArchiveReader.Open(jar);
        var loaded = new List<(string Name, byte[] Bytes, OblivionMobileScript Script)>();
        foreach (var entry in reader.ListFiles()
                     .Where(e => e.FullPath.EndsWith(".scr", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var bytes = reader.Extract(entry);
            loaded.Add((entry.Name, bytes, OblivionMobileScript.Parse(bytes, entry.Name)));
        }

        return loaded;
    }

    private static List<OblivionMobileLang> LoadEveryLangTable()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var jar = RealAssetPaths.Travels.OblivionMobileJar();
        Assert.SkipWhen(jar is null, RealAssetPaths.SkipMessage("the Oblivion mobile JAR"));

        using var reader = ArchiveReader.Open(jar);
        var tables = new List<OblivionMobileLang>();
        foreach (var (index, _, _, _) in ExpectedLangTables)
        {
            var name = $"lang_{index}.txt";
            var bytes = reader.ReadFile(name);
            Assert.SkipWhen(bytes is null, $"{name} is missing from the Oblivion mobile JAR.");
            tables.Add(OblivionMobileLang.Parse(bytes, name));
        }

        return tables;
    }

    /// <summary>
    ///     All 32 scripts decode, and every byte of every one is accounted for by the header, the
    ///     label table, the definition blocks, one marker per label and the decoded instructions.
    ///     A remainder here would mean a width is wrong somewhere.
    /// </summary>
    [Fact]
    public void EveryScript_TilesItsFileExactly()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var loaded = LoadEveryScript();

        Assert.Equal(32, loaded.Count);
        foreach (var (name, bytes, script) in loaded)
        {
            var covered = 1
                          + OblivionMobileScript.LabelEntryLength * script.Labels.Count
                          + script.Blocks.Sum(b => b.Length)
                          + script.Chunks.Sum(c => OblivionMobileScript.MarkerLength + c.Length);
            Assert.Equal(bytes.Length, covered);
            Assert.Equal(bytes.Length, script.Size);
            Assert.Equal(script.Labels.Count, script.Chunks.Count);
            Assert.Equal(script.Labels.Count, script.Labels.Select(l => l.Id).Distinct().Count());
            Assert.True(
                script.Labels.Zip(script.Labels.Skip(1)).All(p => p.First.Id > p.Second.Id),
                $"{name}: the label table is not in descending id order.");
        }
    }

    /// <summary>The corpus totals. 394 labels and 3,844 instructions across the 32 files.</summary>
    [Fact]
    public void CorpusTotals_MatchTheSurvey()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var loaded = LoadEveryScript();

        Assert.Equal(394, loaded.Sum(l => l.Script.Labels.Count));
        Assert.Equal(570, loaded.Sum(l => l.Script.Blocks.Count));
        Assert.Equal(3844, loaded.Sum(l => l.Script.InstructionCount));
        Assert.Equal(30, loaded.SelectMany(l => l.Script.LoadedScripts).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(36, loaded.SelectMany(l => l.Script.Images).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(16, loaded.SelectMany(l => l.Script.Maps).Distinct().Count());
    }

    /// <summary>
    ///     Every chunk's last instruction is RET, and the only bytes that are not reached by
    ///     execution are the five <c>l04_4b.scr</c> orphans — surfaced as a count, not thrown on.
    /// </summary>
    [Fact]
    public void EveryChunkEndsInReturn_AndOnlyOneFileHasDeadBytes()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var loaded = LoadEveryScript();

        foreach (var (name, _, script) in loaded)
        {
            foreach (var chunk in script.Chunks)
            {
                Assert.NotEmpty(chunk.Instructions);
                Assert.Equal(
                    OblivionMobileScriptOpcodes.Return,
                    chunk.Instructions[^1].Opcode);
                Assert.True(
                    chunk.Instructions.Sum(i => i.Length) == chunk.Length,
                    $"{name}: label {chunk.Label}'s instructions do not fill its chunk.");
            }
        }

        var dead = loaded.Where(l => l.Script.DeadByteCount > 0).ToList();
        var only = Assert.Single(dead);
        Assert.Equal("l04_4b.scr", only.Name, true);
        Assert.Equal(5, only.Script.DeadByteCount);
        Assert.Equal(5, only.Script.FindChunk(6)!.DeadByteCount);

        // The same file is the reason id 8 is missing from its 17-entry table.
        Assert.Equal(17, only.Script.Labels.Count);
        Assert.DoesNotContain(only.Script.Labels, l => l.Id == 8);
    }

    /// <summary>All 179 CALLs name a label that exists in their own script's table.</summary>
    [Fact]
    public void EveryCallTarget_ResolvesInItsOwnScript()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var loaded = LoadEveryScript();

        var calls = 0;
        foreach (var (name, _, script) in loaded)
        {
            var ids = script.Labels.Select(l => l.Id).ToHashSet();
            foreach (var instruction in script.Chunks
                         .SelectMany(c => c.Instructions)
                         .Where(i => i.Opcode == OblivionMobileScriptOpcodes.Call))
            {
                calls++;
                var target = (byte)instruction.Operands[0].Value;
                Assert.True(ids.Contains(target), $"{name}: CALL {target} at byte {instruction.Offset} has no label.");
            }
        }

        Assert.Equal(179, calls);
    }

    /// <summary>
    ///     The opcode, block-kind and sub-command censuses. These are the numbers a wrong operand
    ///     width would move first, so they are pinned in full rather than sampled.
    /// </summary>
    [Fact]
    public void Censuses_MatchTheSurvey()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var loaded = LoadEveryScript();

        var opcodes = new Dictionary<byte, int>();
        var blocks = new Dictionary<OblivionMobileBlockKind, int>();
        var subCommands = new Dictionary<int, int>();
        foreach (var (_, _, script) in loaded)
        {
            foreach (var (opcode, count) in script.OpcodeCensus)
            {
                opcodes[opcode] = opcodes.GetValueOrDefault(opcode) + count;
            }

            foreach (var (kind, count) in script.BlockCensus)
            {
                blocks[kind] = blocks.GetValueOrDefault(kind) + count;
            }

            foreach (var operand in script.Chunks
                         .SelectMany(c => c.Instructions)
                         .Where(i => i.Opcode == OblivionMobileScriptOpcodes.SpriteCommand)
                         .Select(i => i.Operands[1]))
            {
                subCommands[operand.Value] = subCommands.GetValueOrDefault(operand.Value) + 1;
            }
        }

        Assert.Equal(ExpectedOpcodeCensus.OrderBy(p => p.Key), opcodes.OrderBy(p => p.Key));
        Assert.Equal(ExpectedBlockCensus.OrderBy(p => p.Key), blocks.OrderBy(p => p.Key));
        Assert.Equal(ExpectedSubCommandCensus.OrderBy(p => p.Key), subCommands.OrderBy(p => p.Key));
        Assert.Equal(3844, opcodes.Values.Sum());
        Assert.Equal(240, subCommands.Values.Sum());
    }

    /// <summary>
    ///     All 13 lang tables parse to 546 records with the per-file counts and id ranges the
    ///     survey measured, ascending and unique within each file.
    /// </summary>
    [Fact]
    public void EveryLangTable_MatchesItsRecordCountAndIdRange()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var tables = LoadEveryLangTable();

        Assert.Equal(13, tables.Count);
        Assert.Equal(546, tables.Sum(t => t.Strings.Count));

        for (var i = 0; i < tables.Count; i++)
        {
            var (index, count, minId, maxId) = ExpectedLangTables[i];
            var table = tables[i];
            Assert.Equal(index, table.Index);
            Assert.Equal(count, table.Strings.Count);
            Assert.Equal(minId, table.Strings[0].Id);
            Assert.Equal(maxId, table.MaxId);
            Assert.Equal(count, table.Strings.Select(s => s.Id).Distinct().Count());
            Assert.True(
                table.Strings.Zip(table.Strings.Skip(1)).All(p => p.First.Id < p.Second.Id),
                $"lang_{index}.txt: ids are not ascending.");
            Assert.All(table.Strings, s => Assert.False(s.Text.Contains('|', StringComparison.Ordinal)));
        }
    }

    /// <summary>
    ///     The 12 overlays are not translations: they share only 9 ids with lang_0, and all 9 carry
    ///     byte-identical text. That is the evidence for reading them as per-chapter dialogue
    ///     rather than as languages.
    /// </summary>
    [Fact]
    public void Overlays_DuplicateRatherThanTranslateTheNineIdsTheyShareWithTheBaseTable()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var tables = LoadEveryLangTable();
        var baseTable = tables[0];

        var shared = 0;
        foreach (var overlay in tables.Skip(1))
        {
            foreach (var entry in overlay.Strings.Where(s => baseTable.Find(s.Id) is not null))
            {
                shared++;
                Assert.Equal(baseTable.Find(entry.Id), entry.Text);
            }
        }

        Assert.Equal(9, shared);
    }

    /// <summary>
    ///     Every lang id a script references resolves — through lang_0 first and then the overlay
    ///     in force, which a script inherits from whichever script LOADSCR'd it when it carries no
    ///     LOADLANG of its own. The walk starts at the two roots and must reach all 32 files.
    /// </summary>
    [Fact]
    public void EveryReferencedLangId_ResolvesThroughTheOverlayInForce()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var loaded = LoadEveryScript();
        var tables = LoadEveryLangTable();
        var byIndex = tables.ToDictionary(t => t.Index);
        var scripts = loaded.ToDictionary(l => l.Name, l => l.Script, StringComparer.OrdinalIgnoreCase);

        var inForce = WalkLoadScriptGraph(scripts);
        Assert.Equal(32, inForce.Count);
        foreach (var (name, expected) in ExpectedOverlayInForce)
        {
            Assert.Equal(expected, inForce[name]);
        }

        var referenced = 0;
        foreach (var (name, script) in scripts)
        {
            var overlay = inForce[name] is { } index ? byIndex[index] : null;
            foreach (var id in script.LangIds)
            {
                referenced++;
                var text = byIndex[0].Find(id) ?? overlay?.Find(id);
                Assert.True(text is not null, $"{name}: lang id {id} resolves in neither lang_0 nor its overlay.");
            }
        }

        Assert.Equal(331, scripts.Values.SelectMany(s => s.LangIds).Distinct().Count());
        Assert.True(referenced > 0);
    }

    /// <summary>
    ///     <c>start.txt</c>: five terminated entries, no ids and no line breaks, indexed by ordinal.
    /// </summary>
    [Fact]
    public void StartText_HasTheFiveBootStrings()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var jar = RealAssetPaths.Travels.OblivionMobileJar();
        Assert.SkipWhen(jar is null, RealAssetPaths.SkipMessage("the Oblivion mobile JAR"));

        using var reader = ArchiveReader.Open(jar);
        var bytes = reader.ReadFile("start.txt");
        Assert.SkipWhen(bytes is null, "start.txt is missing from the Oblivion mobile JAR.");

        var entries = OblivionMobileLang.ParseStartText(bytes, "start.txt");

        Assert.Equal(new[] { "Loading", "Press any key", "Resume game?", "Yes", "Exit" }, entries);
    }

    /// <summary>
    ///     Propagates the overlay along the LOADSCR graph from the two roots. A script's own
    ///     LOADLANG wins; otherwise it keeps whatever its parent had loaded.
    /// </summary>
    private static Dictionary<string, int?> WalkLoadScriptGraph(Dictionary<string, OblivionMobileScript> scripts)
    {
        var inForce = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { "startup.scr", "l01_1.scr" })
        {
            var pending = new Stack<(string Name, int? Inherited)>();
            pending.Push((root, null));
            while (pending.Count > 0)
            {
                var (name, inherited) = pending.Pop();
                if (inForce.ContainsKey(name) || !scripts.TryGetValue(name, out var script))
                {
                    continue;
                }

                var effective = script.OverlayIndex ?? inherited;
                inForce[name] = effective;
                foreach (var child in script.LoadedScripts)
                {
                    pending.Push((child.TrimStart('/'), effective));
                }
            }
        }

        return inForce;
    }
}