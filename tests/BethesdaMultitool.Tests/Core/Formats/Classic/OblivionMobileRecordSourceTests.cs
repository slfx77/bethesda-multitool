using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Vfs;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Synthetic coverage for <see cref="OblivionMobileRecordSource" />: a hand-built install of a
///     tile map, an atlas, three scripts and two lang tables, mounted through
///     <see cref="LooseFileSystem" /> exactly as a JAR mount would be.
///     <para>
///         The interesting behaviour is not the field copying — it is the three joins the record
///         source has to perform because no single file carries them: a map learns its atlas from a
///         script's LOADMAP pair, a script learns its lang overlay from whichever script LOADSCR'd
///         it, and an atlas learns its frames overhang from the PNG's IHDR. Each has its own test,
///         plus the two ways this source is meant to fail loudly (a hashed stem collision and a
///         lang id outside the record key's budget).
///     </para>
/// </summary>
public sealed class OblivionMobileRecordSourceTests : IDisposable
{
    /// <summary>
    ///     Two stems whose <see cref="ClassicNameHash" /> values agree in the low 23 bits — the
    ///     width the geometry domain leaves below its family tag. Found by exhaustive search over
    ///     short names, so this is a real collision rather than a mocked one.
    /// </summary>
    private const string CollidingStemA = "yx";

    private const string CollidingStemB = "d0u";

    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp tree is not worth failing a test over.
            }
        }
    }

    /// <summary>
    ///     The whole install, one record per level stem, atlas, script and lang record: 1 + 1 + 3 +
    ///     4 here, in the three reserved domains and nowhere else.
    /// </summary>
    [Fact]
    public void SyntheticInstall_EmitsOneRecordPerMapAtlasScriptAndLangString()
    {
        var records = Populate(BuildInstall());

        Assert.Equal(1, Count(records, OblivionMobileRecordSource.TileMapRecordType));
        Assert.Equal(1, Count(records, OblivionMobileRecordSource.AtlasRecordType));
        Assert.Equal(3, Count(records, OblivionMobileRecordSource.ScriptRecordType));
        Assert.Equal(4, Count(records, OblivionMobileRecordSource.TextRecordType));
        Assert.Equal(9, records.GenericRecords.Count);
        Assert.All(records.GenericRecords, r => Assert.InRange(
            ClassicFormIdScheme.DomainOf(r.FormId),
            OblivionMobileRecordSource.FirstDomain,
            OblivionMobileRecordSource.LastDomain));
    }

    /// <summary>
    ///     The map record's geometry, and the two facts it can only get from the scripts: which
    ///     atlas draws it and which scripts name it. Every tile id it uses must resolve in that
    ///     atlas — that cross-check is the point of pairing them at all.
    /// </summary>
    [Fact]
    public void TileMapRecord_CarriesGeometryPairingAndNamingScripts()
    {
        var record = Single(Populate(BuildInstall()), OblivionMobileRecordSource.TileMapRecordType);

        Assert.Equal("demo", record.EditorId);
        Assert.Equal("demo.jtm", Field<string>(record, "File"));
        Assert.Equal(2, Field<int>(record, "Width"));
        Assert.Equal(1, Field<int>(record, "Height"));
        Assert.Equal(2, Field<int>(record, "Cells"));
        Assert.Equal(2, Field<int>(record, "Layers"));
        Assert.Equal(1, Field<int>(record, "BlockedCells"));
        Assert.Equal(1, Field<int>(record, "OpenCells"));
        Assert.Equal("0=1, 1=1", Field<string>(record, "Passability"));
        Assert.Equal(1, Field<int>(record, "DistinctTileIds"));
        Assert.Equal("5", Field<string>(record, "TileIds"));
        Assert.Equal(1, Field<int>(record, "PlacedTiles"));
        Assert.Equal("demo.cml", Field<string>(record, "Atlas"));
        Assert.Equal("/sheet.png", Field<string>(record, "PrimarySheet"));
        Assert.Equal(0, Field<int>(record, "UnresolvedTileIds"));
        Assert.Equal("demo (27 B)", Field<string>(record, "Scripts"));
    }

    /// <summary>
    ///     A map no script loads is recorded as unreferenced rather than paired with a guess — the
    ///     retail JAR has exactly one such orphan, <c>l01_r.jtm</c>.
    /// </summary>
    [Fact]
    public void MapNoScriptLoads_IsRecordedAsUnreferenced()
    {
        var root = BuildInstall();
        File.Delete(Path.Combine(root, "demo.scr"));

        var record = Single(Populate(root), OblivionMobileRecordSource.TileMapRecordType);

        Assert.Equal(OblivionMobileRecordSource.Unreferenced, Field<string>(record, "Atlas"));
        Assert.Equal("(none)", Field<string>(record, "Scripts"));
        Assert.False(record.Fields.ContainsKey("PrimarySheet"));
    }

    /// <summary>
    ///     The atlas record's sprite/frame census, and the one number that needs the PNG: a frame
    ///     whose source rectangle leaves its sheet. Five of retail's 612 frames overhang, so this
    ///     must be counted rather than rejected.
    /// </summary>
    [Fact]
    public void AtlasRecord_CountsSpritesFramesAndOverhangingFrames()
    {
        var inside = Single(Populate(BuildInstall()), OblivionMobileRecordSource.AtlasRecordType);

        Assert.Equal("demo", inside.EditorId);
        Assert.Equal(1, Field<int>(inside, "Sheets"));
        Assert.Equal("/sheet.png", Field<string>(inside, "SheetPaths"));
        Assert.Equal(0, Field<int>(inside, "WholeImageSheets"));
        Assert.Equal(1, Field<int>(inside, "Sprites"));
        Assert.Equal(1, Field<int>(inside, "Frames"));
        Assert.Equal(0, Field<int>(inside, "MirroredFrames"));
        Assert.Equal(0, Field<int>(inside, "OutOfBoundsFrames"));
        Assert.Equal("(none)", Field<string>(inside, "MissingSheets"));
        Assert.Equal("demo", Field<string>(inside, "DrawnMaps"));

        // The same atlas over a 16-wide frame on an 8-wide sheet: one overhang, still a record.
        var overhang = BuildInstall(frameWidth: 16);
        var wide = Single(Populate(overhang), OblivionMobileRecordSource.AtlasRecordType);
        Assert.Equal(1, Field<int>(wide, "OutOfBoundsFrames"));
    }

    /// <summary>
    ///     A sheet the install does not ship is listed, not thrown on, and contributes no overhang
    ///     count. Retail's <c>startup.cml</c> names <c>/4.png</c>, which the engine skips too.
    /// </summary>
    [Fact]
    public void AtlasRecord_ListsSheetsTheInstallDoesNotShip()
    {
        var root = BuildInstall();
        File.Delete(Path.Combine(root, "sheet.png"));

        var record = Single(Populate(root), OblivionMobileRecordSource.AtlasRecordType);

        Assert.Equal("/sheet.png", Field<string>(record, "MissingSheets"));
        Assert.Equal(0, Field<int>(record, "OutOfBoundsFrames"));
    }

    /// <summary>
    ///     The overlay in force is a property of the LOADSCR chain, not of the file: <c>child.scr</c>
    ///     carries no LOADLANG and must still resolve id 42 out of <c>lang_3.txt</c>, while
    ///     <c>demo.scr</c> — which nothing loads and which loads no overlay — resolves out of
    ///     lang_0 alone.
    /// </summary>
    [Fact]
    public void ScriptRecord_InheritsTheOverlayInForceAndResolvesEnglishText()
    {
        var records = Populate(BuildInstall());
        var scripts = records.GenericRecords
            .Where(r => r.RecordType == OblivionMobileRecordSource.ScriptRecordType)
            .ToDictionary(r => r.EditorId!, StringComparer.Ordinal);

        var startup = scripts["startup"];
        Assert.Equal("3", Field<string>(startup, "OverlayIndex"));
        Assert.Equal("3", Field<string>(startup, "OverlayInForce"));
        Assert.Equal("child.scr", Field<string>(startup, "LoadedScripts"));

        var child = scripts["child"];
        Assert.Equal("(none)", Field<string>(child, "OverlayIndex"));
        Assert.Equal("3", Field<string>(child, "OverlayInForce"));
        Assert.Equal(1, Field<int>(child, "LangIds"));
        Assert.Equal(0, Field<int>(child, "UnresolvedLangIds"));
        Assert.Equal("Overlay line", Field<string>(child, "Lang42"));

        var demo = scripts["demo"];
        Assert.Equal("(none)", Field<string>(demo, "OverlayInForce"));
        Assert.Equal("demo.jtm + demo.cml", Field<string>(demo, "Maps"));
        Assert.Equal(2, Field<int>(demo, "Instructions"));
        Assert.Equal(1, Field<int>(demo, "Labels"));
        Assert.Equal(0, Field<int>(demo, "Blocks"));
        Assert.Equal("RET=1, LOADMAP=1", Field<string>(demo, "OpcodeCensus"));
    }

    /// <summary>
    ///     Text records key on <c>overlay * 1024 + id</c> — arithmetic, not a hash — and flag the
    ///     ids an overlay repeats verbatim from lang_0 (nine such cases on retail).
    /// </summary>
    [Fact]
    public void TextRecords_KeyOnOverlayAndId_AndFlagBaseTableDuplicates()
    {
        var records = Populate(BuildInstall());
        var text = records.GenericRecords
            .Where(r => r.RecordType == OblivionMobileRecordSource.TextRecordType)
            .ToDictionary(r => r.FormId);

        var baseSeven = text[ClassicFormIdScheme.Compose(OblivionMobileRecordSource.TextDomain, 7)];
        Assert.Equal("Lang0_7", baseSeven.EditorId);
        Assert.Equal("Loading", baseSeven.FullName);
        Assert.Equal(0, Field<int>(baseSeven, "Overlay"));
        Assert.False(Field<bool>(baseSeven, "DuplicatesBaseTable"));

        var overlaySeven = text[ClassicFormIdScheme.Compose(
            OblivionMobileRecordSource.TextDomain, (3 * OblivionMobileRecordSource.TextIdsPerOverlay) + 7)];
        Assert.Equal("lang_3.txt", Field<string>(overlaySeven, "File"));
        Assert.True(Field<bool>(overlaySeven, "DuplicatesBaseTable"));

        var overlayLine = text[ClassicFormIdScheme.Compose(
            OblivionMobileRecordSource.TextDomain, (3 * OblivionMobileRecordSource.TextIdsPerOverlay) + 42)];
        Assert.Equal("Overlay line", Field<string>(overlayLine, "Text"));
        Assert.False(Field<bool>(overlayLine, "DuplicatesBaseTable"));
    }

    /// <summary>
    ///     A map and an atlas sharing a stem (<c>demo.jtm</c> / <c>demo.cml</c>, exactly as retail's
    ///     <c>l01_1</c> pair does) must not collide: the geometry domain's top index bit is the
    ///     family tag, so the two ids differ by that bit and nothing else.
    /// </summary>
    [Fact]
    public void MapAndAtlasSharingAStem_DifferOnlyByTheFamilyTagBit()
    {
        var records = Populate(BuildInstall());

        var map = Single(records, OblivionMobileRecordSource.TileMapRecordType).FormId;
        var atlas = Single(records, OblivionMobileRecordSource.AtlasRecordType).FormId;

        Assert.NotEqual(map, atlas);
        Assert.Equal(1u << OblivionMobileRecordSource.GeometryHashBits, atlas - map);
    }

    /// <summary>Nothing in the install may share a FormID, whatever family it came from.</summary>
    [Fact]
    public void EveryFormId_IsUnique()
    {
        var records = Populate(BuildInstall());

        Assert.Equal(
            records.GenericRecords.Count,
            records.GenericRecords.Select(r => r.FormId).Distinct().Count());
    }

    /// <summary>
    ///     An install with none of these files leaves the collection empty. The analyzer runs on
    ///     whatever ships, and the retail JAR is itself a trimmed repack.
    /// </summary>
    [Fact]
    public void InstallWithNoOblivionMobileFiles_LeavesTheCollectionEmpty()
    {
        var root = NewRoot();
        File.WriteAllText(Path.Combine(root, "readme.md"), "not a MIDlet");

        Assert.Empty(Populate(root).GenericRecords);
    }

    /// <summary>
    ///     Two stems that hash to the same 23-bit index must stop the load with a message naming
    ///     both, never be quietly renumbered — renumbering would defeat the whole point of hashing
    ///     source identity, which is that two installs diff against each other.
    /// </summary>
    [Fact]
    public void CollidingStems_ThrowRatherThanRenumber()
    {
        var root = NewRoot();
        File.WriteAllBytes(Path.Combine(root, CollidingStemA + ".jtm"), TileMapBytes());
        File.WriteAllBytes(Path.Combine(root, CollidingStemB + ".jtm"), TileMapBytes());

        var error = Assert.Throws<InvalidOperationException>(() => Populate(root));

        Assert.Contains(CollidingStemA, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(CollidingStemB, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     A lang id at or beyond the 1,024 the record key reserves per overlay would silently land
    ///     on the next overlay's ids, so it is refused with the file and byte position named.
    /// </summary>
    [Fact]
    public void LangIdBeyondTheOverlayBudget_Throws()
    {
        var root = NewRoot();
        File.WriteAllBytes(Path.Combine(root, "lang_1.txt"), LangBytes(("2000", "Far too high")));

        var error = Assert.Throws<InvalidDataException>(() => Populate(root));

        Assert.Contains("lang_1.txt", error.Message, StringComparison.Ordinal);
        Assert.Contains("2000", error.Message, StringComparison.Ordinal);
    }

    private static int Count(RecordCollection records, string recordType)
    {
        return records.GenericRecords.Count(r => r.RecordType == recordType);
    }

    private static GenericEsmRecord Single(RecordCollection records, string recordType)
    {
        return records.GenericRecords.Single(r => r.RecordType == recordType);
    }

    private static T Field<T>(GenericEsmRecord record, string key)
    {
        Assert.True(record.Fields.ContainsKey(key), $"{record.EditorId}: no field '{key}'.");
        return Assert.IsType<T>(record.Fields[key]);
    }

    private static RecordCollection Populate(string root)
    {
        var records = new RecordCollection();
        using var install = new LooseFileSystem(root);
        OblivionMobileRecordSource.Populate(install, records);
        return records;
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "obmobile-records-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    /// <summary>
    ///     A miniature install: one 2x1 map, one atlas over one 8x8 sheet, three scripts (a root
    ///     that loads the overlay and a child, the child that references text, and one that loads
    ///     the map) and the base plus overlay lang tables.
    /// </summary>
    private string BuildInstall(byte frameWidth = 4)
    {
        var root = NewRoot();
        File.WriteAllBytes(Path.Combine(root, "demo.jtm"), TileMapBytes());
        File.WriteAllBytes(Path.Combine(root, "demo.cml"), AtlasBytes(frameWidth));
        File.WriteAllBytes(Path.Combine(root, "sheet.png"), PngHeaderBytes(8, 8));

        File.WriteAllBytes(
            Path.Combine(root, "startup.scr"),
            ScriptBytes([.. LoadLang(3), .. LoadScript("/child.scr"), .. Return()]));
        File.WriteAllBytes(Path.Combine(root, "child.scr"), ScriptBytes([.. Message(42), .. Return()]));
        File.WriteAllBytes(
            Path.Combine(root, "demo.scr"),
            ScriptBytes([.. LoadMap("demo.jtm", "demo.cml"), .. Return()]));

        File.WriteAllBytes(Path.Combine(root, "lang_0.txt"), LangBytes(("7", "Loading"), ("9", "Exit")));
        File.WriteAllBytes(Path.Combine(root, "lang_3.txt"), LangBytes(("7", "Loading"), ("42", "Overlay line")));

        // Not lang tables, and deliberately present: they must not become records.
        File.WriteAllBytes(Path.Combine(root, "start.txt"), Encoding.ASCII.GetBytes("Loading|"));
        File.WriteAllText(Path.Combine(root, "eso.ver"), "2.424\r\n");
        return root;
    }

    /// <summary>
    ///     <c>demo.jtm</c>: W=2, H=1, two literal-encoded layers — passability {open, blocked} and
    ///     one tile layer {5, none}.
    /// </summary>
    private static byte[] TileMapBytes()
    {
        return [0x02, 0x01, 0x00, 0x01, 0x05, 0x00];
    }

    /// <summary>
    ///     <c>demo.cml</c>: empty prefix, one sheet <c>/sheet.png</c> with an empty attribute mask,
    ///     no pair table, one sprite (mask 0x204 = id + loop) carrying one frame (mask 0x1E0 = sx,
    ///     sy, w, h) at the sheet origin.
    /// </summary>
    private static byte[] AtlasBytes(byte frameWidth)
    {
        var name = Encoding.ASCII.GetBytes("/sheet.png");
        var bytes = new List<byte> { 0x00, 0x01, (byte)name.Length };
        bytes.AddRange(name);
        bytes.AddRange([0x00, 0x00, 0x00, 0x01]);
        bytes.AddRange([0x02, 0x04, 0x05, 0x00]);
        bytes.Add(0x01);
        bytes.AddRange([0x01, 0xE0, 0x00, 0x00, 0x00, 0x00, frameWidth, 0x04]);
        return [.. bytes];
    }

    /// <summary>The 8-byte signature plus the IHDR length/tag/width/height the record source reads.</summary>
    private static byte[] PngHeaderBytes(int width, int height)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };
        bytes.AddRange(Encoding.ASCII.GetBytes("IHDR"));
        bytes.AddRange([(byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width]);
        bytes.AddRange([(byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height]);
        return [.. bytes];
    }

    /// <summary>
    ///     One label whose chunk holds <paramref name="code" />: u8 label count, the 3-byte table
    ///     entry {id 1, absolute offset 7}, no definition blocks, then the marker <c>00 01 01</c>.
    /// </summary>
    private static byte[] ScriptBytes(byte[] code)
    {
        var bytes = new List<byte> { 0x01, 0x01, 0x00, 0x07, 0x00, 0x01, 0x01 };
        bytes.AddRange(code);
        return [.. bytes];
    }

    private static byte[] LoadMap(string tileMap, string spriteSet)
    {
        var bytes = new List<byte> { 0x08 };
        AppendString8(bytes, tileMap);
        AppendString8(bytes, spriteSet);
        return [.. bytes];
    }

    private static byte[] LoadScript(string path)
    {
        var bytes = new List<byte> { 0x1D };
        AppendString8(bytes, path);
        return [.. bytes];
    }

    /// <summary>Opcode 56: a discarded string operand then the overlay index.</summary>
    private static byte[] LoadLang(byte overlay)
    {
        return [0x38, 0x00, overlay];
    }

    /// <summary>Opcode 39: a 0xFxxx text reference (the low 12 bits are the lang id) then three flags.</summary>
    private static byte[] Message(ushort langId)
    {
        return [0x27, (byte)(0xF0 | (langId >> 8)), (byte)langId, 0x00, 0x00, 0x00];
    }

    private static byte[] Return()
    {
        return [0x02];
    }

    private static void AppendString8(List<byte> bytes, string value)
    {
        var encoded = Encoding.ASCII.GetBytes(value);
        bytes.Add((byte)encoded.Length);
        bytes.AddRange(encoded);
    }

    /// <summary>Records <c>"&lt;id&gt; &lt;text&gt;|"</c> joined by CRLF, with no trailing CRLF.</summary>
    private static byte[] LangBytes(params (string Id, string Text)[] entries)
    {
        return Encoding.ASCII.GetBytes(string.Join("\r\n", entries.Select(e => $"{e.Id} {e.Text}|")));
    }
}

/// <summary>
///     Opt-in checks of <see cref="OblivionMobileRecordSource" /> against the retail Oblivion mobile
///     MIDlet JAR (<c>RUN_BUCKET_B=1</c>). The JAR is a fixed fixture, so every count here is exact:
///     17 <c>OMAP</c>, 21 <c>OATL</c>, 32 <c>OMSC</c> and 546 <c>OMTX</c>, 616 records in all.
///     <para>
///         The counts are what make this worth having. A record source over a game with no plugin
///         file is only as good as the joins it performs, and each of these numbers moves if one
///         breaks: the 16 pairings come from the scripts, the 5 overhanging frames come from the
///         PNGs, the 12 inherited overlays come from the LOADSCR chain, and the 0 unresolved lang
///         ids come from all three at once.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class OblivionMobileRecordSourceRetailTests
{
    /// <summary>Per level: stem, W, H, layers, blocked cells, distinct ids, placed tiles, atlas.</summary>
    private static readonly (string Stem, int Width, int Height, int Layers, int Blocked, int Ids, int Placed, string Atlas)[]
        LevelCensus =
        [
            ("l01_1", 44, 59, 5, 420, 26, 2435, "l01_1.cml"),
            ("l01_r", 1, 1, 2, 0, 1, 1, "(unreferenced)"),
            ("l02_1", 33, 27, 5, 210, 18, 612, "l02_l2.cml"),
            ("l03_1", 51, 53, 5, 375, 31, 2174, "l03_l3.cml"),
            ("l04_1", 40, 39, 5, 280, 23, 1629, "l01_1.cml"),
            ("l05_1", 35, 35, 5, 414, 17, 1290, "l05_l5.cml"),
            ("l06_1", 52, 53, 5, 350, 29, 2180, "l03_l3.cml"),
            ("l06_a", 1, 1, 2, 0, 1, 1, "l11_l11.cml"),
            ("l06_b", 1, 1, 2, 0, 1, 1, "l02_l2.cml"),
            ("l07_1", 48, 34, 5, 399, 17, 1216, "l05_l5.cml"),
            ("l08_1", 53, 50, 4, 1040, 12, 2077, "l08_l8.cml"),
            ("l09_1", 57, 52, 5, 986, 26, 3132, "l09_l9.cml"),
            ("l10_1", 42, 32, 4, 318, 17, 712, "l02_l2.cml"),
            ("l11_1", 46, 40, 5, 705, 24, 2397, "l11_l11.cml"),
            ("l12_1", 43, 52, 5, 873, 17, 2367, "l12_l12.cml"),
            ("l13_clrl", 17, 22, 4, 61, 6, 402, "l13_clrl.cml"),
            ("l14_1", 15, 15, 3, 55, 6, 202, "l14_l14.cml"),
        ];

    /// <summary>
    ///     The overlay in force for the four scripts that show the inheritance working: two that
    ///     load their own, one that inherits along the chain, and <c>startup.scr</c>, the one script
    ///     with no overlay at all.
    /// </summary>
    private static readonly (string Script, string Overlay)[] OverlaySpotChecks =
    [
        ("startup", "(none)"),
        ("startup2", "0"),
        ("l06_6_ba", "6"),
        ("end_15", "7"),
    ];

    private static RecordCollection Populate()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var jar = RealAssetPaths.Travels.OblivionMobileJar();
        Assert.SkipWhen(jar is null, RealAssetPaths.SkipMessage("the Oblivion mobile JAR"));

        var records = new RecordCollection();
        using var install = new ArchiveFileSystem(jar!);
        OblivionMobileRecordSource.Populate(install, records);
        return records;
    }

    private static List<GenericEsmRecord> OfType(RecordCollection records, string recordType)
    {
        return [.. records.GenericRecords.Where(r => r.RecordType == recordType)];
    }

    private static T Field<T>(GenericEsmRecord record, string key)
    {
        Assert.True(record.Fields.ContainsKey(key), $"{record.EditorId}: no field '{key}'.");
        return Assert.IsType<T>(record.Fields[key]);
    }

    /// <summary>
    ///     A per-id resolved-text field (<c>Lang43</c>), as opposed to the <c>LangIds</c> count that
    ///     shares the prefix.
    /// </summary>
    private static bool IsResolvedTextField(string key)
    {
        return key.StartsWith("Lang", StringComparison.Ordinal)
               && key.Length > 4
               && key.Skip(4).All(char.IsAsciiDigit);
    }

    /// <summary>17 maps, 21 atlases, 32 scripts, 546 lang records — and nothing else.</summary>
    [Fact]
    public void RetailJar_EmitsTheFullRecordCensus()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var records = Populate();

        Assert.Equal(17, OfType(records, OblivionMobileRecordSource.TileMapRecordType).Count);
        Assert.Equal(21, OfType(records, OblivionMobileRecordSource.AtlasRecordType).Count);
        Assert.Equal(32, OfType(records, OblivionMobileRecordSource.ScriptRecordType).Count);
        Assert.Equal(546, OfType(records, OblivionMobileRecordSource.TextRecordType).Count);
        Assert.Equal(616, records.GenericRecords.Count);
    }

    /// <summary>
    ///     No two of the 616 records share a FormID, and every one sits in 0x4C..0x4E. The hash is a
    ///     23/24-bit fold, so this is the check that says the fold is wide enough for this corpus.
    /// </summary>
    [Fact]
    public void EveryFormId_IsUniqueAndInTheReservedDomains()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var records = Populate();

        var ids = records.GenericRecords.Select(r => r.FormId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(records.GenericRecords, r => Assert.InRange(
            ClassicFormIdScheme.DomainOf(r.FormId),
            OblivionMobileRecordSource.FirstDomain,
            OblivionMobileRecordSource.LastDomain));
    }

    /// <summary>
    ///     Every level's geometry and its paired atlas. The pairing is not in the <c>.jtm</c>: l04_1
    ///     drawing with l01's atlas, l06_1 with l03's and l10_1 with l02's is the LOADMAP join
    ///     working, and l01_r staying unreferenced is it declining to guess.
    /// </summary>
    [Fact]
    public void LevelRecords_MatchTheMeasuredCensusAndTheScriptRecoveredPairing()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var records = Populate();
        var maps = OfType(records, OblivionMobileRecordSource.TileMapRecordType)
            .ToDictionary(r => r.EditorId!, StringComparer.Ordinal);

        Assert.Equal(LevelCensus.Length, maps.Count);
        foreach (var (stem, width, height, layers, blocked, ids, placed, atlas) in LevelCensus)
        {
            var record = maps[stem];
            Assert.Equal(width, Field<int>(record, "Width"));
            Assert.Equal(height, Field<int>(record, "Height"));
            Assert.Equal(width * height, Field<int>(record, "Cells"));
            Assert.Equal(layers, Field<int>(record, "Layers"));
            Assert.Equal(blocked, Field<int>(record, "BlockedCells"));
            Assert.Equal(ids, Field<int>(record, "DistinctTileIds"));
            Assert.Equal(placed, Field<int>(record, "PlacedTiles"));
            Assert.Equal(atlas, Field<string>(record, "Atlas"));
        }

        Assert.Equal(24999, maps.Values.Sum(r => Field<int>(r, "Cells")));
        Assert.Equal(22828, maps.Values.Sum(r => Field<int>(r, "PlacedTiles")));
        Assert.Equal(71, maps.Values.Sum(r => Field<int>(r, "Layers")));
        Assert.Equal("/ts_lvl9.png", Field<string>(maps["l11_1"], "PrimarySheet"));
    }

    /// <summary>
    ///     Every tile id every paired map uses resolves in the atlas the scripts paired it with —
    ///     16 of the 17 maps, since l01_r has no pairing to check. A wrong pairing would show up
    ///     here before anywhere else.
    /// </summary>
    [Fact]
    public void EveryPairedMap_ResolvesAllItsTileIdsInItsAtlas()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var maps = OfType(Populate(), OblivionMobileRecordSource.TileMapRecordType);

        var paired = maps.Where(r => r.Fields.ContainsKey("UnresolvedTileIds")).ToList();
        Assert.Equal(16, paired.Count);
        Assert.All(paired, r => Assert.Equal(0, Field<int>(r, "UnresolvedTileIds")));
    }

    /// <summary>
    ///     The atlas census: 48 sheets, 513 sprites, 612 frames, 8 whole-image entries, 99 mirrored
    ///     frames and the 5 frames that overhang their PNG. <c>/4.png</c> is the only referenced
    ///     sheet this trimmed repack does not ship.
    /// </summary>
    [Fact]
    public void AtlasRecords_MatchTheMeasuredSpriteFrameAndOverhangCensus()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var atlases = OfType(Populate(), OblivionMobileRecordSource.AtlasRecordType);

        Assert.Equal(48, atlases.Sum(r => Field<int>(r, "Sheets")));
        Assert.Equal(513, atlases.Sum(r => Field<int>(r, "Sprites")));
        Assert.Equal(612, atlases.Sum(r => Field<int>(r, "Frames")));
        Assert.Equal(8, atlases.Sum(r => Field<int>(r, "WholeImageSheets")));
        Assert.Equal(99, atlases.Sum(r => Field<int>(r, "MirroredFrames")));
        Assert.Equal(5, atlases.Sum(r => Field<int>(r, "OutOfBoundsFrames")));

        var startup = atlases.Single(r => r.EditorId == "startup");
        Assert.Equal("/4.png", Field<string>(startup, "MissingSheets"));
        Assert.Equal(OblivionMobileRecordSource.Unreferenced, Field<string>(startup, "DrawnMaps"));

        var withMissing = atlases.Count(r => Field<string>(r, "MissingSheets") != "(none)");
        Assert.Equal(1, withMissing);
    }

    /// <summary>
    ///     The script corpus totals — 394 labels, 570 definition blocks, 3,844 instructions — plus
    ///     the single file with dead bytes, and the overlay inheritance that lets every referenced
    ///     lang id resolve.
    /// </summary>
    [Fact]
    public void ScriptRecords_MatchTheCorpusTotalsAndResolveEveryLangId()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var scripts = OfType(Populate(), OblivionMobileRecordSource.ScriptRecordType);
        var byName = scripts.ToDictionary(r => r.EditorId!, StringComparer.Ordinal);

        Assert.Equal(394, scripts.Sum(r => Field<int>(r, "Labels")));
        Assert.Equal(570, scripts.Sum(r => Field<int>(r, "Blocks")));
        Assert.Equal(3844, scripts.Sum(r => Field<int>(r, "Instructions")));
        Assert.All(scripts, r => Assert.Equal(0, Field<int>(r, "UnresolvedLangIds")));

        var withDeadBytes = scripts.Where(r => Field<int>(r, "DeadBytes") > 0).ToList();
        var only = Assert.Single(withDeadBytes);
        Assert.Equal("l04_4b", only.EditorId);
        Assert.Equal(5, Field<int>(only, "DeadBytes"));

        foreach (var (script, overlay) in OverlaySpotChecks)
        {
            Assert.Equal(overlay, Field<string>(byName[script], "OverlayInForce"));
        }

        // 20 of the 32 carry LOADLANG; the other 12 inherit along the LOADSCR chain, and only
        // startup.scr ends up with no overlay at all.
        Assert.Equal(20, scripts.Count(r => Field<string>(r, "OverlayIndex") != "(none)"));
        Assert.Equal(31, scripts.Count(r => Field<string>(r, "OverlayInForce") != "(none)"));

        // The English text itself landed on the records: one field per referenced id, 331 distinct
        // ids across the corpus, and not one of them left as "(unresolved)".
        var textFields = scripts
            .SelectMany(r => r.Fields.Where(f => IsResolvedTextField(f.Key)))
            .ToList();
        Assert.Equal(331, textFields.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(textFields, f => Assert.NotEqual("(unresolved)", (string?)f.Value));
        Assert.Contains(textFields, f => (f.Value as string)?.Length > 0);
    }

    /// <summary>
    ///     546 lang records across 13 tables with the measured per-table counts, keyed by
    ///     <c>overlay * 1024 + id</c>, and the 9 ids an overlay repeats verbatim from lang_0.
    /// </summary>
    [Fact]
    public void TextRecords_CoverThirteenTablesAndFlagTheNineBaseDuplicates()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var text = OfType(Populate(), OblivionMobileRecordSource.TextRecordType);

        Assert.Equal(546, text.Count);
        Assert.Equal(13, text.Select(r => Field<int>(r, "Overlay")).Distinct().Count());
        Assert.Equal(305, text.Count(r => Field<int>(r, "Overlay") == 0));
        Assert.Equal(9, text.Count(r => Field<bool>(r, "DuplicatesBaseTable")));

        foreach (var record in text)
        {
            var expected = ClassicFormIdScheme.Compose(
                OblivionMobileRecordSource.TextDomain,
                (uint)((Field<int>(record, "Overlay") * OblivionMobileRecordSource.TextIdsPerOverlay)
                       + Field<int>(record, "Id")));
            Assert.Equal(expected, record.FormId);
        }
    }
}
