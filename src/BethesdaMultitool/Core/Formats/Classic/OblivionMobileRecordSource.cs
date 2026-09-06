using System.Buffers.Binary;
using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Travels.OblivionMobile;
using BethesdaMultitool.Core.Vfs;

namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>
///     Synthesizes browsable records from an Oblivion mobile (2006, J2ME, Vir2L) install — the
///     MIDlet JAR itself, or the directory it was unpacked into, reached through the mounted
///     <see cref="IGameFileSystem" />. Four families over the three reserved
///     <see cref="ClassicFormIdScheme" /> domains <c>0x4C-0x4E</c>:
///     <list type="table">
///         <item>
///             <term><c>OMAP</c> (0x4C)</term>
///             <description>one per <c>.jtm</c> level stem — 17 on retail.</description>
///         </item>
///         <item>
///             <term><c>OATL</c> (0x4C)</term>
///             <description>one per <c>.cml</c> tile atlas — 21 on retail.</description>
///         </item>
///         <item>
///             <term><c>OMSC</c> (0x4D)</term>
///             <description>one per <c>.scr</c> byte-code script — 32 on retail.</description>
///         </item>
///         <item>
///             <term><c>OMTX</c> (0x4E)</term>
///             <description>one per <c>lang_N.txt</c> record — 546 on retail.</description>
///         </item>
///     </list>
///     <para>
///         <b>Why maps and atlases share a domain.</b> Four families, three domains: geometry is the
///         pair that browses together, because an <c>OMAP</c> names the <c>OATL</c> it draws with
///         and every tile id it uses resolves there, so <c>show</c> walks map to atlas without
///         crossing a domain. Scripts take their own domain (their identity is a whole file name,
///         not a stem) and text takes its own (its index is arithmetic, not a hash). Inside 0x4C the
///         top index bit is the family tag — 0 map, 1 atlas — above a 23-bit name hash:
///         <c>l01_1.jtm</c> and <c>l01_1.cml</c> share a stem, so without the tag those two records
///         would collide by construction rather than by bad luck.
///     </para>
///     <para>
///         <b>Identity.</b> Stems and file names are all these files have — nothing in the JAR
///         numbers them — so <see cref="ClassicNameHash" /> over the name is the stable index, and a
///         collision is refused loudly instead of renumbered. <c>OMTX</c> is the exception: a lang
///         record is already keyed by (overlay, id), which packs directly as
///         <c>overlay * 1024 + id</c> (retail maximum 12 * 1024 + 574 = 12,862).
///     </para>
///     <para>
///         <b>The pairing a level record needs is not in the level files.</b> A <c>.jtm</c> never
///         names its atlas; the <c>.scr</c> scripts do, as a <c>LOADMAP</c> pair. So the scripts are
///         parsed first and the map records read their atlas, primary sheet and naming scripts out
///         of that index — <c>l04_1</c> draws with <c>l01_1.cml</c>, <c>l06_1</c> with
///         <c>l03_l3.cml</c>, <c>l10_1</c> with <c>l02_l2.cml</c>. <c>l01_r.jtm</c> is an orphan no
///         script mentions and is recorded as <see cref="Unreferenced" /> rather than guessed at.
///     </para>
///     <para>
///         <b>English text.</b> A script's lang ids resolve through <c>lang_0.txt</c> first and then
///         the overlay in force, which is a property of the LOADSCR chain and not of the file: 12 of
///         the 32 scripts carry no <c>LOADLANG</c> and inherit their parent's overlay. The walk from
///         the two roots reaches all 32 and the answer is unique.
///     </para>
/// </summary>
internal static class OblivionMobileRecordSource
{
    /// <summary>Domain byte for the geometry families, <c>OMAP</c> and <c>OATL</c>.</summary>
    public const byte GeometryDomain = 0x4C;

    /// <summary>Domain byte for <c>OMSC</c> script records.</summary>
    public const byte ScriptDomain = 0x4D;

    /// <summary>Domain byte for <c>OMTX</c> lang-string records.</summary>
    public const byte TextDomain = 0x4E;

    /// <summary>First reserved domain byte for Oblivion mobile records.</summary>
    public const byte FirstDomain = GeometryDomain;

    /// <summary>Last reserved domain byte for Oblivion mobile records.</summary>
    public const byte LastDomain = TextDomain;

    /// <summary>The record signature used for a <c>.jtm</c> tile map.</summary>
    public const string TileMapRecordType = "OMAP";

    /// <summary>The record signature used for a <c>.cml</c> tile atlas.</summary>
    public const string AtlasRecordType = "OATL";

    /// <summary>The record signature used for a <c>.scr</c> script.</summary>
    public const string ScriptRecordType = "OMSC";

    /// <summary>The record signature used for one <c>lang_N.txt</c> record.</summary>
    public const string TextRecordType = "OMTX";

    /// <summary>Placeholder for a map no script draws (retail: <c>l01_r.jtm</c> alone).</summary>
    public const string Unreferenced = "(unreferenced)";

    /// <summary>Name-hash width inside the geometry domain, below the family tag bit.</summary>
    public const int GeometryHashBits = 23;

    /// <summary>Name-hash width for script records, which have their whole domain to themselves.</summary>
    public const int ScriptHashBits = 24;

    /// <summary>Lang ids reserved per overlay in the <c>OMTX</c> index (retail maximum id is 574).</summary>
    public const int TextIdsPerOverlay = 1024;

    /// <summary>Set in an <c>OATL</c> index so an atlas never collides with a map sharing its stem.</summary>
    private const uint AtlasFamilyBit = 1u << GeometryHashBits;

    /// <summary>PNG signature (8) + length (4) + "IHDR" (4) + width (4) + height (4).</summary>
    private const int PngDimensionsLength = 24;

    /// <summary>Byte offset of the IHDR width; height follows it.</summary>
    private const int PngWidthOffset = 16;

    /// <summary>Read cap for one sheet: the largest retail PNG is <c>ts5.png</c> at 6,633 bytes.</summary>
    private const long MaximumSheetBytes = 1 << 20;

    /// <summary>The two scripts nothing LOADSCRs — where the overlay walk starts.</summary>
    private static readonly string[] RootScripts = ["startup.scr", "l01_1.scr"];

    private static readonly char[] PathSeparators = ['/', '\\'];

    /// <summary>
    ///     Reads the mounted install and appends every synthesized record. A family whose files are
    ///     absent simply contributes nothing — the analyzer runs on whatever the install ships, and
    ///     this JAR is itself a trimmed repack (it is missing <c>/4.png</c>, <c>/lang.cml</c>,
    ///     <c>/oh_font.cml</c> and <c>/finale.png</c>), so a short collection must still browse.
    /// </summary>
    public static void Populate(IGameFileSystem install, RecordCollection records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();

        var assets = IndexAssets(install);
        var scripts = ReadScripts(install, assets, cancellationToken);
        var atlases = ReadAtlases(install, assets, cancellationToken);
        var maps = ReadTileMaps(install, assets, cancellationToken);
        var langs = ReadLangTables(install, assets, cancellationToken);

        var built = new List<GenericEsmRecord>();
        built.AddRange(BuildTileMapRecords(maps, atlases, scripts));
        built.AddRange(BuildAtlasRecords(install, assets, atlases, scripts));
        built.AddRange(BuildScriptRecords(scripts, langs));
        built.AddRange(BuildTextRecords(langs));

        var seen = new Dictionary<uint, string>();
        foreach (var record in built)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var label = record.RecordType + " " + (record.EditorId ?? "?");
            if (seen.TryGetValue(record.FormId, out var clash))
            {
                throw new InvalidOperationException(
                    $"Oblivion mobile records '{clash}' and '{label}' both hash to 0x{record.FormId:X8}; "
                    + "the name hash must be widened rather than the records renumbered.");
            }

            seen[record.FormId] = label;
            records.GenericRecords.Add(record);
        }
    }

    /// <summary>
    ///     Every file in the mount, keyed by its bare name. The JAR is flat for data (only the
    ///     MIDlet classes sit in folders) and an unpacked copy is the same tree, so the bare name is
    ///     the identity both mounts agree on; the value keeps the full path to read back through.
    /// </summary>
    private static Dictionary<string, string> IndexAssets(IGameFileSystem install)
    {
        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in install.EnumerateFiles().OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            assets.TryAdd(FileNameOf(entry.Path), entry.Path);
        }

        return assets;
    }

    private static List<(string Name, OblivionMobileTileMap Map)> ReadTileMaps(
        IGameFileSystem install, Dictionary<string, string> assets, CancellationToken cancellationToken)
    {
        var maps = new List<(string, OblivionMobileTileMap)>();
        foreach (var (name, bytes) in ReadFamily(install, assets, ".jtm", cancellationToken))
        {
            maps.Add((name, OblivionMobileTileMap.Parse(bytes, name)));
        }

        return maps;
    }

    private static List<(string Name, OblivionMobileAtlas Atlas)> ReadAtlases(
        IGameFileSystem install, Dictionary<string, string> assets, CancellationToken cancellationToken)
    {
        var atlases = new List<(string, OblivionMobileAtlas)>();
        foreach (var (name, bytes) in ReadFamily(install, assets, ".cml", cancellationToken))
        {
            atlases.Add((name, OblivionMobileAtlas.Parse(bytes, name)));
        }

        return atlases;
    }

    private static List<(string Name, int Size, OblivionMobileScript Script)> ReadScripts(
        IGameFileSystem install, Dictionary<string, string> assets, CancellationToken cancellationToken)
    {
        var scripts = new List<(string, int, OblivionMobileScript)>();
        foreach (var (name, bytes) in ReadFamily(install, assets, ".scr", cancellationToken))
        {
            scripts.Add((name, bytes.Length, OblivionMobileScript.Parse(bytes, name)));
        }

        return scripts;
    }

    /// <summary>
    ///     The <c>lang_N.txt</c> tables, ascending by index. A <c>.txt</c> whose name carries no
    ///     index (<c>start.txt</c>, <c>copywrite.txt</c>) is not an overlay and is skipped: it has
    ///     no place in the <c>overlay * 1024 + id</c> key.
    /// </summary>
    private static List<OblivionMobileLang> ReadLangTables(
        IGameFileSystem install, Dictionary<string, string> assets, CancellationToken cancellationToken)
    {
        var tables = new List<OblivionMobileLang>();
        foreach (var (name, bytes) in ReadFamily(install, assets, ".txt", cancellationToken))
        {
            if (!name.StartsWith("lang_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var table = OblivionMobileLang.Parse(bytes, name);
            if (table.Index >= 0)
            {
                tables.Add(table);
            }
        }

        return [.. tables.OrderBy(t => t.Index)];
    }

    /// <summary>Reads every asset whose bare name ends in <paramref name="extension" />, name-ordered.</summary>
    private static IEnumerable<(string Name, byte[] Bytes)> ReadFamily(
        IGameFileSystem install, Dictionary<string, string> assets, string extension, CancellationToken cancellationToken)
    {
        foreach (var name in assets.Keys
                     .Where(n => n.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (install.TryReadAllBytes(assets[name]) is { } bytes)
            {
                yield return (name, bytes);
            }
        }
    }

    private static IEnumerable<GenericEsmRecord> BuildTileMapRecords(
        List<(string Name, OblivionMobileTileMap Map)> maps,
        List<(string Name, OblivionMobileAtlas Atlas)> atlases,
        List<(string Name, int Size, OblivionMobileScript Script)> scripts)
    {
        var atlasByName = atlases.ToDictionary(a => a.Name, a => a.Atlas, StringComparer.OrdinalIgnoreCase);
        var pairing = BuildPairing(scripts);
        var namedBy = BuildMapScriptIndex(scripts);

        foreach (var (name, map) in maps)
        {
            var atlasName = pairing.TryGetValue(name, out var paired) ? string.Join(", ", paired) : null;
            var atlas = atlasName is not null && atlasByName.TryGetValue(atlasName, out var found) ? found : null;
            namedBy.TryGetValue(name, out var users);
            yield return BuildTileMapRecord(name, map, atlasName, atlas, users);
        }
    }

    private static GenericEsmRecord BuildTileMapRecord(
        string fileName,
        OblivionMobileTileMap map,
        string? atlasName,
        OblivionMobileAtlas? atlas,
        List<string>? namedBy)
    {
        var stem = StemOf(fileName);
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["File"] = fileName,
            ["Width"] = (int)map.Width,
            ["Height"] = (int)map.Height,
            ["Cells"] = map.CellCount,
            ["Layers"] = map.LayerCount,
            ["TileLayers"] = map.TileLayers.Count,
            ["BlockedCells"] = map.BlockedCellCount,
            ["OpenCells"] = map.CellCount - map.BlockedCellCount,
            ["Passability"] = DescribePassability(map),
            ["DistinctTileIds"] = map.DistinctTileIds.Count,
            ["TileIds"] = map.DistinctTileIds.Count == 0 ? "(none)" : string.Join(", ", map.DistinctTileIds),
            ["PlacedTiles"] = map.PlacedTileCount,
            ["Atlas"] = atlasName ?? Unreferenced
        };

        if (atlas is not null)
        {
            fields["PrimarySheet"] = atlas.Sheets.Count > 0 ? atlas.Sheets[0].Path : "(no sheets)";
            fields["UnresolvedTileIds"] = map.DistinctTileIds.Count(id => !atlas.TryGetTile(id, out _, out _));
        }

        fields["Scripts"] = namedBy is { Count: > 0 } ? string.Join(", ", namedBy) : "(none)";

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(GeometryDomain, ClassicNameHash.Of(stem, GeometryHashBits)),
            RecordType = TileMapRecordType,
            EditorId = ClassicRecordNaming.ToEditorId(stem),
            FullName = null,
            Fields = fields
        };
    }

    /// <summary>
    ///     The passability values present and their cell counts, e.g. <c>0=2176, 1=420</c>. Values
    ///     2..5 are the engine's diagonal half-cell blockers, so the breakdown is worth keeping
    ///     rather than folding into one blocked count.
    /// </summary>
    private static string DescribePassability(OblivionMobileTileMap map)
    {
        var census = map.PassabilityCensus();
        var parts = new List<string>();
        for (var value = 0; value < census.Count; value++)
        {
            if (census[value] > 0)
            {
                parts.Add($"{value}={census[value]}");
            }
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    ///     Which atlas each map is drawn with, from the scripts' <c>LOADMAP</c> pairs. A set rather
    ///     than one name: retail is 16 maps with 0 conflicts, but a build that paired one map two
    ///     ways must show both rather than silently keep whichever was read first.
    /// </summary>
    private static Dictionary<string, SortedSet<string>> BuildPairing(
        List<(string Name, int Size, OblivionMobileScript Script)> scripts)
    {
        var pairing = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, _, script) in scripts)
        {
            foreach (var reference in script.Maps)
            {
                var tileMap = FileNameOf(reference.TileMap);
                if (!pairing.TryGetValue(tileMap, out var set))
                {
                    set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    pairing[tileMap] = set;
                }

                set.Add(FileNameOf(reference.SpriteSet));
            }
        }

        return pairing;
    }

    /// <summary>The scripts that load each map, with their byte sizes, in name order.</summary>
    private static Dictionary<string, List<string>> BuildMapScriptIndex(
        List<(string Name, int Size, OblivionMobileScript Script)> scripts)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, size, script) in scripts)
        {
            var entry = $"{StemOf(name)} ({size} B)";
            foreach (var reference in script.Maps)
            {
                var tileMap = FileNameOf(reference.TileMap);
                if (!index.TryGetValue(tileMap, out var users))
                {
                    users = [];
                    index[tileMap] = users;
                }

                if (!users.Contains(entry, StringComparer.Ordinal))
                {
                    users.Add(entry);
                }
            }
        }

        return index;
    }

    private static IEnumerable<GenericEsmRecord> BuildAtlasRecords(
        IGameFileSystem install,
        Dictionary<string, string> assets,
        List<(string Name, OblivionMobileAtlas Atlas)> atlases,
        List<(string Name, int Size, OblivionMobileScript Script)> scripts)
    {
        var drawnMaps = InvertPairing(BuildPairing(scripts));
        var sheetSizes = new Dictionary<string, (int Width, int Height)?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, atlas) in atlases)
        {
            drawnMaps.TryGetValue(name, out var drawn);
            yield return BuildAtlasRecord(install, assets, name, atlas, drawn, sheetSizes);
        }
    }

    /// <summary>Atlas file name to the map stems drawn with it — the reverse of the LOADMAP index.</summary>
    private static Dictionary<string, SortedSet<string>> InvertPairing(Dictionary<string, SortedSet<string>> pairing)
    {
        var inverted = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tileMap, sheets) in pairing)
        {
            foreach (var sheet in sheets)
            {
                if (!inverted.TryGetValue(sheet, out var set))
                {
                    set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    inverted[sheet] = set;
                }

                set.Add(StemOf(tileMap));
            }
        }

        return inverted;
    }

    private static GenericEsmRecord BuildAtlasRecord(
        IGameFileSystem install,
        Dictionary<string, string> assets,
        string fileName,
        OblivionMobileAtlas atlas,
        SortedSet<string>? drawnMaps,
        Dictionary<string, (int Width, int Height)?> sheetSizes)
    {
        var stem = StemOf(fileName);
        var missing = new List<string>();
        var outOfBounds = 0;
        foreach (var sheet in atlas.Sheets)
        {
            var size = SheetSize(install, assets, sheet.Path, sheetSizes);
            if (size is null)
            {
                if (!missing.Contains(sheet.Path, StringComparer.OrdinalIgnoreCase))
                {
                    missing.Add(sheet.Path);
                }

                continue;
            }

            outOfBounds += CountOverhangingFrames(sheet, size.Value);
        }

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["File"] = fileName,
            ["Prefix"] = atlas.Prefix.Length == 0 ? "(none)" : atlas.Prefix,
            ["Sheets"] = atlas.Sheets.Count,
            ["SheetPaths"] = Join(atlas.Sheets.Select(s => s.Path)),
            ["WholeImageSheets"] = atlas.Sheets.Count(s => s.IsWholeImage),
            ["Sprites"] = atlas.SpriteCount,
            ["Frames"] = atlas.FrameCount,
            ["DistinctTileIds"] = atlas.TileIds.Count,
            ["TileIds"] = atlas.TileIds.Count == 0 ? "(none)" : string.Join(", ", atlas.TileIds.Order()),
            ["MirroredFrames"] = atlas.Sheets.Sum(s => s.Sprites.Sum(sp => sp.Frames.Count(f => f.Mirror))),
            ["OutOfBoundsFrames"] = outOfBounds,
            ["MissingSheets"] = missing.Count == 0 ? "(none)" : string.Join(", ", missing),
            ["DrawnMaps"] = drawnMaps is { Count: > 0 } ? string.Join(", ", drawnMaps) : Unreferenced
        };

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(
                GeometryDomain, AtlasFamilyBit | ClassicNameHash.Of(stem, GeometryHashBits)),
            RecordType = AtlasRecordType,
            EditorId = ClassicRecordNaming.ToEditorId(stem),
            FullName = null,
            Fields = fields
        };
    }

    /// <summary>
    ///     Frames whose source rectangle leaves its PNG. Five of retail's 612 do — the engine clips
    ///     them, so they are a content oddity to surface, never a reason to reject the atlas.
    /// </summary>
    private static int CountOverhangingFrames(OblivionMobileAtlasSheet sheet, (int Width, int Height) size)
    {
        var count = 0;
        foreach (var sprite in sheet.Sprites)
        {
            foreach (var frame in sprite.Frames)
            {
                if (frame.SourceX + frame.Width > size.Width || frame.SourceY + frame.Height > size.Height)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>
    ///     A sheet's pixel size from its PNG IHDR, memoized because ten atlases share
    ///     <c>ts5.png</c>. Null when the install does not ship the sheet (retail: <c>/4.png</c>,
    ///     which the engine special-cases and skips) or the bytes are not a PNG — an absent sheet
    ///     leaves the overhang count short, it does not throw.
    /// </summary>
    private static (int Width, int Height)? SheetSize(
        IGameFileSystem install,
        Dictionary<string, string> assets,
        string sheetPath,
        Dictionary<string, (int Width, int Height)?> cache)
    {
        if (cache.TryGetValue(sheetPath, out var cached))
        {
            return cached;
        }

        (int Width, int Height)? size = null;
        if (assets.TryGetValue(FileNameOf(sheetPath), out var path)
            && install.TryReadAllBytesBounded(path, MaximumSheetBytes) is { } read
            && read.Data.Length >= PngDimensionsLength)
        {
            var width = BinaryPrimitives.ReadUInt32BigEndian(read.Data.AsSpan(PngWidthOffset, 4));
            var height = BinaryPrimitives.ReadUInt32BigEndian(read.Data.AsSpan(PngWidthOffset + 4, 4));
            if (width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue)
            {
                size = ((int)width, (int)height);
            }
        }

        cache[sheetPath] = size;
        return size;
    }

    private static IEnumerable<GenericEsmRecord> BuildScriptRecords(
        List<(string Name, int Size, OblivionMobileScript Script)> scripts,
        List<OblivionMobileLang> langs)
    {
        var overlays = ResolveOverlays(scripts);
        var byIndex = langs.ToDictionary(t => t.Index);
        byIndex.TryGetValue(0, out var baseTable);

        foreach (var (name, size, script) in scripts)
        {
            overlays.TryGetValue(name, out var inForce);
            var overlay = inForce is { } index && byIndex.TryGetValue(index, out var table) ? table : null;
            yield return BuildScriptRecord(name, size, script, inForce, baseTable, overlay);
        }
    }

    private static GenericEsmRecord BuildScriptRecord(
        string fileName,
        int size,
        OblivionMobileScript script,
        int? overlayInForce,
        OblivionMobileLang? baseTable,
        OblivionMobileLang? overlay)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["File"] = fileName,
            ["Bytes"] = size,
            ["Labels"] = script.Labels.Count,
            ["Blocks"] = script.Blocks.Count,
            ["BlockCensus"] = script.Blocks.Count == 0
                ? "(none)"
                : string.Join(", ", script.BlockCensus.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}")),
            ["Instructions"] = script.InstructionCount,
            ["DistinctOpcodes"] = script.OpcodeCensus.Count,
            ["OpcodeCensus"] = DescribeOpcodes(script),
            ["DeadBytes"] = script.DeadByteCount,
            ["OverlayIndex"] = Describe(script.OverlayIndex),
            ["OverlayInForce"] = Describe(overlayInForce),
            ["LoadedScripts"] = Join(script.LoadedScripts.Select(FileNameOf)),
            ["Maps"] = Join(script.Maps.Select(m => $"{FileNameOf(m.TileMap)} + {FileNameOf(m.SpriteSet)}")),
            ["Images"] = Join(script.Images.Select(FileNameOf)),
            ["LangIds"] = script.LangIds.Count
        };

        AddResolvedText(fields, script, baseTable, overlay);

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(ScriptDomain, ClassicNameHash.Of(fileName, ScriptHashBits)),
            RecordType = ScriptRecordType,
            EditorId = ClassicRecordNaming.ToEditorId(StemOf(fileName)),
            FullName = null,
            Fields = fields
        };
    }

    /// <summary>
    ///     One field per referenced lang id, carrying the English text the game would show. Lookup
    ///     is lang_0 first and then the overlay in force, which is the engine's own order.
    /// </summary>
    private static void AddResolvedText(
        Dictionary<string, object?> fields,
        OblivionMobileScript script,
        OblivionMobileLang? baseTable,
        OblivionMobileLang? overlay)
    {
        var unresolved = 0;
        foreach (var id in script.LangIds)
        {
            var text = baseTable?.Find(id) ?? overlay?.Find(id);
            if (text is null)
            {
                unresolved++;
            }

            fields[$"Lang{id}"] = text ?? "(unresolved)";
        }

        fields["UnresolvedLangIds"] = unresolved;
    }

    private static string DescribeOpcodes(OblivionMobileScript script)
    {
        return Join(script.OpcodeCensus
            .OrderBy(p => p.Key)
            .Select(p => $"{OblivionMobileScriptOpcodes.Find(p.Key)?.Mnemonic ?? $"OP{p.Key}"}={p.Value}"));
    }

    /// <summary>
    ///     The overlay in force for each script. A script's own <c>LOADLANG</c> wins; otherwise it
    ///     keeps whatever the script that <c>LOADSCR</c>-ed it had loaded. Anything the walk does not
    ///     reach from the two roots falls back to its own <c>LOADLANG</c> — retail reaches all 32,
    ///     but a partial install must still answer for the files it has.
    /// </summary>
    private static Dictionary<string, int?> ResolveOverlays(
        List<(string Name, int Size, OblivionMobileScript Script)> scripts)
    {
        var byName = scripts.ToDictionary(s => s.Name, s => s.Script, StringComparer.OrdinalIgnoreCase);
        var inForce = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in RootScripts)
        {
            var pending = new Stack<(string Name, int? Inherited)>();
            pending.Push((root, null));
            while (pending.Count > 0)
            {
                var (name, inherited) = pending.Pop();
                if (inForce.ContainsKey(name) || !byName.TryGetValue(name, out var script))
                {
                    continue;
                }

                var effective = script.OverlayIndex ?? inherited;
                inForce[name] = effective;
                foreach (var child in script.LoadedScripts)
                {
                    pending.Push((FileNameOf(child), effective));
                }
            }
        }

        foreach (var (name, script) in byName)
        {
            if (!inForce.ContainsKey(name))
            {
                inForce[name] = script.OverlayIndex;
            }
        }

        return inForce;
    }

    private static IEnumerable<GenericEsmRecord> BuildTextRecords(List<OblivionMobileLang> langs)
    {
        var baseTable = langs.Find(t => t.Index == 0);
        foreach (var table in langs)
        {
            foreach (var entry in table.Strings)
            {
                yield return BuildTextRecord(table, entry, baseTable);
            }
        }
    }

    private static GenericEsmRecord BuildTextRecord(
        OblivionMobileLang table, OblivionMobileLangString entry, OblivionMobileLang? baseTable)
    {
        if (entry.Id is < 0 or >= TextIdsPerOverlay)
        {
            throw new InvalidDataException(
                $"'{table.Name}': lang id {entry.Id} at byte {entry.Offset} is outside the "
                + $"{TextIdsPerOverlay} ids the overlay * {TextIdsPerOverlay} + id record key reserves per overlay.");
        }

        // The 9 ids an overlay shares with lang_0 carry byte-identical text, so this is a flag on a
        // duplicate rather than a conflict between two readings of the same id.
        var duplicate = table.Index != 0
                        && baseTable?.Find(entry.Id) is { } shared
                        && string.Equals(shared, entry.Text, StringComparison.Ordinal);

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["File"] = table.Name,
            ["Overlay"] = table.Index,
            ["Id"] = entry.Id,
            ["Text"] = entry.Text,
            ["Length"] = entry.Text.Length,
            ["Offset"] = entry.Offset,
            ["DuplicatesBaseTable"] = duplicate
        };

        var index = (uint)((table.Index * TextIdsPerOverlay) + entry.Id);
        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(TextDomain, index),
            RecordType = TextRecordType,
            EditorId = ClassicRecordNaming.ToEditorId($"Lang{table.Index}_{entry.Id}"),
            FullName = ClassicRecordNaming.Summarize(entry.Text),
            Fields = fields
        };
    }

    /// <summary>The last path segment; the scripts write resource paths as <c>/name.ext</c>.</summary>
    private static string FileNameOf(string path)
    {
        var slash = path.LastIndexOfAny(PathSeparators);
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>The file name without its extension.</summary>
    private static string StemOf(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot <= 0 ? fileName : fileName[..dot];
    }

    private static string Describe(int? value)
    {
        return value?.ToString(CultureInfo.InvariantCulture) ?? "(none)";
    }

    private static string Join(IEnumerable<string> values)
    {
        var joined = string.Join(", ", values);
        return joined.Length == 0 ? "(none)" : joined;
    }
}
