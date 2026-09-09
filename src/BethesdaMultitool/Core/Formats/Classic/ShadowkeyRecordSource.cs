using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Core.Imaging;
using BethesdaMultitool.Core.Vfs;

namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>
///     Synthesizes browsable records from a Shadowkey (2004, N-Gage) application directory — the
///     21 zones' little-endian per-zone files plus the two global packs — read through the mounted
///     <see cref="IGameFileSystem" /> so the unpacked Symbian tree and any container it is served
///     from behave identically. Reserved <see cref="ClassicFormIdScheme" /> domains
///     <c>0x48–0x4B</c>.
///     <para>
///         <b>Retail census</b> (<c>system/apps/6R51/</c>, measured 2026-09-05): 21 <c>SZON</c>,
///         8,258 <c>SENT</c>, 256 <c>STRG</c>, 351 <c>SSUR</c>, 226 <c>SMSH</c> and 253
///         <c>SSPR</c> — 9,365 records. The zone totals behind them: 59 lock rows, 7 paths holding
///         86 points, 326 zone textures and 331,776 map cells.
///     </para>
///     <para>
///         <b>Domain budget — the deliberate choice.</b> Four domains are reserved and six families
///         want one. The zone-scoped families take <c>0x48–0x4A</c> and the two global packs share
///         <c>0x4B</c>:
///     </para>
///     <list type="bullet">
///         <item><c>0x48 SZON</c> — one per zone, index = <c>ClassicNameHash.Of(stem, 24)</c>.</item>
///         <item>
///             <c>0x49 SENT</c> — one per <c>.ent</c> placement, index =
///             <c>hash12(stem) &lt;&lt; 12 | recordIndex</c>. Retail's largest zone (raiders) holds
///             1,072 placements, comfortably inside the 4,096-slot half, and the 21 stems are
///             distinct at 12 bits (they are NOT at 8 — two collide there).
///         </item>
///         <item>
///             <c>0x4A STRG + SSUR</c> — the two small per-zone sub-tables SHARE a domain, split by
///             bit 11 of the index: <c>hash12(stem) &lt;&lt; 12 | family &lt;&lt; 11 | index</c>,
///             family 0 = trigger rectangle, 1 = surface. The largest retail zone tables are 34
///             rectangles and 27 surfaces, so 2,048 slots each is an enormous margin.
///         </item>
///         <item>
///             <c>0x4B SMSH + SSPR</c> — the global packs, keyed by SLOT (a
///             <c>&lt;zone&gt;_models.txt</c> is a residency mask over the same 236 slots, so the
///             slot is install-wide identity), meshes at <c>slot</c> and sprites at
///             <c>0x1000 + slot</c>.
///         </item>
///     </list>
///     <para>
///         The alternative the spec floats — folding surfaces into the zone record — was rejected:
///         a <c>.sur</c> row is the only cross-file reference in the whole per-zone set that
///         resolves (its last byte indexes the zone's <c>.ztx</c>, and
///         <c>max(index) == textureCount - 1</c> in 21/21), so it is exactly the thing worth
///         diffing row by row. Squashing 351 rows into 21 summary strings would hide a texture
///         re-point behind an unchanged count. Sharing a domain costs nothing here because the
///         index space is partitioned by construction, and every emitted id is checked for
///         uniqueness before it is added.
///     </para>
///     <para>
///         <b>Identity, never enumeration order.</b> A zone's identity is its <c>.zon</c> STEM,
///         hashed — the on-disk subdirectories spell three of them differently (<c>CryptSH3</c>,
///         <c>ghstpass</c>, <c>GlcrCrwl</c>), so a directory name cannot key a zone, and the stems
///         themselves are mixed case, which the hash normalizes away. Everything under a zone keys
///         off its own table row ordinal, which the engine treats as identity: <c>.stn</c> joins by
///         instance NAME, and the entity chain
///         (<c>.ent</c> id → <c>entities.txt</c> → model slot) never uses a position. Pack records
///         key off the slot index the whole install indexes them by.
///     </para>
///     <para>
///         A file the install does not ship leaves the collection short rather than throwing: a
///         zone with no <c>.ent</c> still yields its <c>SZON</c>, with the missing kinds named in
///         the record's <c>MissingFiles</c> field. A file that IS present but malformed throws from
///         the reader that owns it, naming the file and byte — that is corruption, not absence.
///     </para>
/// </summary>
internal static class ShadowkeyRecordSource
{
    /// <summary>First reserved domain byte for Shadowkey records.</summary>
    public const byte FirstDomain = 0x48;

    /// <summary>Last reserved domain byte for Shadowkey records.</summary>
    public const byte LastDomain = 0x4B;

    /// <summary>Domain byte for <c>SZON</c> (zone) records; the index is the 24-bit stem hash.</summary>
    public const byte ZoneDomain = 0x48;

    /// <summary>Domain byte for <c>SENT</c> (placement) records.</summary>
    public const byte EntityDomain = 0x49;

    /// <summary>Domain byte shared by <c>STRG</c> and <c>SSUR</c>, the two per-zone sub-tables.</summary>
    public const byte ZoneTableDomain = 0x4A;

    /// <summary>Domain byte shared by <c>SMSH</c> and <c>SSPR</c>, the two global packs.</summary>
    public const byte PackDomain = 0x4B;

    /// <summary>One zone: its counts, palette facts and its lock/path tables inline.</summary>
    public const string ZoneRecordType = "SZON";

    /// <summary>One placed entity from a zone's <c>.ent</c>.</summary>
    public const string EntityRecordType = "SENT";

    /// <summary>One trigger rectangle from a zone's <c>.zon</c>.</summary>
    public const string TriggerRecordType = "STRG";

    /// <summary>One surface (texture plus UV window) from a zone's <c>.sur</c>.</summary>
    public const string SurfaceRecordType = "SSUR";

    /// <summary>One non-empty <c>models.huge</c> slot.</summary>
    public const string MeshRecordType = "SMSH";

    /// <summary>One non-empty <c>global.spr</c> slot.</summary>
    public const string SpriteRecordType = "SSPR";

    /// <summary>Bits of the 24-bit index a zone-scoped record spends on its zone's stem hash.</summary>
    public const int StemHashBits = 12;

    /// <summary>Bits left for a placement's record index under <see cref="EntityDomain" />.</summary>
    public const int EntityIndexBits = 12;

    /// <summary>Bits left for a row index under <see cref="ZoneTableDomain" /> once the family bit is taken.</summary>
    public const int ZoneTableIndexBits = 11;

    /// <summary>Bits of the 24-bit index an <c>SZON</c> spends on its stem hash.</summary>
    private const int ZoneHashBits = 24;

    /// <summary>Family bit inside a <see cref="ZoneTableDomain" /> index: 0 = trigger, 1 = surface.</summary>
    private const uint SurfaceFamilyBit = 1u << ZoneTableIndexBits;

    /// <summary>Sprite slots sit above the mesh slots inside <see cref="PackDomain" />.</summary>
    private const uint SpriteSlotBias = 0x1000;

    private const string EntitiesTableName = "entities.txt";
    private const string ModelsTableName = "models.txt";
    private const string ModelIndexName = "models.idx";
    private const string ModelPackName = "models.huge";
    private const string SpritePackName = "global.spr";
    private const string ZoneExtension = ".zon";

    /// <summary>
    ///     Reads the mounted install and appends every synthesized record. Zones are visited in
    ///     stem order so the emitted sequence is deterministic; the ids themselves do not depend on
    ///     that order.
    /// </summary>
    public static void Populate(
        IGameFileSystem install, RecordCollection records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();

        var zones = new List<string>();
        var root = (string?)null;
        foreach (var entry in install.EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = FileNameOf(entry.Path);
            if (fileName.EndsWith(ZoneExtension, StringComparison.OrdinalIgnoreCase))
            {
                zones.Add(fileName[..^ZoneExtension.Length]);
                root ??= DirectoryOf(entry.Path);
            }
            else if (root is null && fileName.Equals(ModelIndexName, StringComparison.OrdinalIgnoreCase))
            {
                root = DirectoryOf(entry.Path);
            }
        }

        if (root is null)
        {
            // Neither a zone nor the mesh index: nothing here is a Shadowkey application directory.
            return;
        }

        zones.Sort(StringComparer.OrdinalIgnoreCase);

        var tables = new InstallTables
        {
            Entities = ReadEntityTable(install, root),
            Models = ReadModelTable(install, root, ModelsTableName)
        };

        var seen = new Dictionary<uint, string>();
        foreach (var stem in zones)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PopulateZone(install, root, stem, tables, records, seen, cancellationToken);
        }

        PopulatePacks(install, root, tables, records, seen, cancellationToken);
    }

    /// <summary>
    ///     Emits one zone's four families. Every file is optional; what is missing is named on the
    ///     <c>SZON</c> rather than aborting the zone, because a staged or partial tree is a normal
    ///     input for this analyzer.
    /// </summary>
    private static void PopulateZone(
        IGameFileSystem install,
        string root,
        string stem,
        InstallTables tables,
        RecordCollection records,
        Dictionary<uint, string> seen,
        CancellationToken cancellationToken)
    {
        var missing = new List<string>();
        var triggers = ReadZoneFile(install, root, stem, ".zon", missing) is { } zon
            ? ShadowkeyZoneFiles.ParseZon(zon, stem + ".zon")
            : [];
        var locks = ReadZoneFile(install, root, stem, ".stn", missing) is { } stn
            ? ShadowkeyZoneFiles.ParseStn(stn, stem + ".stn")
            : [];
        var paths = ReadZoneFile(install, root, stem, ".pth", missing) is { } pth
            ? ShadowkeyZoneFiles.ParsePth(pth, stem + ".pth")
            : [];
        var surfaces = ReadZoneFile(install, root, stem, ".sur", missing) is { } sur
            ? ShadowkeyZoneFiles.ParseSur(sur, stem + ".sur")
            : [];
        var placements = ReadZoneFile(install, root, stem, ".ent", missing) is { } ent
            ? ShadowkeyZoneFiles.ParseEnt(ent, stem + ".ent").Entities
            : [];

        var map = ReadZoneFile(install, root, stem, ".zmp", missing) is { } zmp
            ? ShadowkeyZoneMap.Parse(ShadowkeyCompressedFile.Inflate(zmp, stem + ".zmp"), stem + ".zmp")
            : null;
        var textures = ReadZoneFile(install, root, stem, ".ztx", missing) is { } ztx
            ? ShadowkeyTextureBank.Parse(ShadowkeyCompressedFile.Inflate(ztx, stem + ".ztx"), stem + ".ztx")
            : null;
        var paletteBytes = ReadZoneFile(install, root, stem, ".pal", missing);
        var zoneModels = ReadModelTable(install, root, stem + "_models.txt");
        var spriteSlots = ReadSpriteList(install, root, stem + "_sprites.txt");

        // .sta exists for azra alone and is deliberately NOT counted as missing anywhere else.
        var stateRecords = install.TryReadAllBytes(Combine(root, stem + ".sta")) is { } sta
            ? ShadowkeyZoneFiles.ParseSta(sta, stem + ".sta").Entities.Count
            : (int?)null;

        tables.CountResidency(zoneModels, spriteSlots);

        Add(records, seen, BuildZoneRecord(
            stem, triggers, locks, paths, surfaces, placements.Count, map, textures, paletteBytes,
            zoneModels, spriteSlots, stateRecords, missing));

        for (var i = 0; i < triggers.Count; i++)
        {
            Add(records, seen, BuildTriggerRecord(stem, i, triggers[i]));
        }

        for (var i = 0; i < surfaces.Count; i++)
        {
            Add(records, seen, BuildSurfaceRecord(stem, i, surfaces[i], textures?.Count));
        }

        if (placements.Count == 0)
        {
            return;
        }

        var lockConditions = GroupLocks(locks);
        var nameCounts = CountInstanceNames(placements);
        for (var i = 0; i < placements.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(records, seen, BuildEntityRecord(
                stem, i, placements[i], tables, zoneModels, lockConditions, nameCounts));
        }
    }

    /// <summary>
    ///     The zone record: every count the per-zone set yields, the map header and palette facts,
    ///     and the two tables too small to deserve a family of their own — 59 lock rows and 7 paths
    ///     across the whole retail install — carried inline.
    /// </summary>
    private static GenericEsmRecord BuildZoneRecord(
        string stem,
        IReadOnlyList<ShadowkeyTriggerZone> triggers,
        IReadOnlyList<ShadowkeyLockEntry> locks,
        IReadOnlyList<ShadowkeyPath> paths,
        IReadOnlyList<ShadowkeySurface> surfaces,
        int entityCount,
        ShadowkeyZoneMap? map,
        ShadowkeyTextureBank? textures,
        byte[]? paletteBytes,
        ShadowkeyModelTable? zoneModels,
        List<int>? spriteSlots,
        int? stateRecords,
        List<string> missing)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Stem"] = stem,
            ["TriggerCount"] = triggers.Count,
            ["LockCount"] = locks.Count,
            ["PathCount"] = paths.Count,
            ["PathPointCount"] = paths.Sum(p => p.Points.Count),
            ["SurfaceCount"] = surfaces.Count,
            ["EntityCount"] = entityCount
        };

        if (map is not null)
        {
            fields["MapWidth"] = map.Width;
            fields["MapHeight"] = map.Height;
            fields["MapCells"] = map.Cells.Count;
            fields["MapBlockedCells"] = map.BlockedCellCount;
            fields["MapMaxPrototypeIndex"] = map.MaxPrototypeIndex;
            AddIfPresent(fields, "MapName", map.ZoneName);
            AddIfPresent(fields, "MapAuthor", map.Author);
            AddIfPresent(fields, "MapDescription", map.Description);
        }

        if (textures is not null)
        {
            fields["TextureCount"] = textures.Count;

            // The .sur index is bounded by this count in 21/21 zones; surfacing the pair makes a
            // mis-paired zone obvious at a glance rather than only inside the per-surface records.
            fields["MaxSurfaceTextureIndex"] = surfaces.Count == 0 ? -1 : surfaces.Max(s => (int)s.TextureIndex);
        }

        if (paletteBytes is not null)
        {
            AddPaletteFields(fields, stem, paletteBytes);
        }

        if (zoneModels is not null)
        {
            fields["ModelSlots"] = zoneModels.Models.Count;
            fields["ModelSlotsResident"] = zoneModels.ResidentCount;
        }

        if (spriteSlots is not null)
        {
            fields["SpriteSlotsListed"] = spriteSlots.Count;
        }

        if (locks.Count > 0)
        {
            fields["Locks"] = string.Join(", ", locks.Select(l => $"{l.EntityName}={l.Condition}"));
        }

        if (paths.Count > 0)
        {
            fields["Paths"] = string.Join(", ", paths.Select(p => $"{p.Name} ({p.Points.Count} points)"));
            fields["PathPoints"] = string.Join("; ", paths
                .Where(p => p.Points.Count > 0)
                .Select(p =>
                    $"{p.Name}: {string.Join(" ", p.Points.Select(pt => Tiles(pt.TileX) + "," + Tiles(pt.TileY)))}"));
        }

        if (stateRecords is { } state)
        {
            // azra alone ships a .sta, two months older than its .ent and stopping before the two
            // name fields. Counted, never merged: it is a leftover revision, not live placement data.
            fields["StatePlacementRecords"] = state;
        }

        if (missing.Count > 0)
        {
            fields["MissingFiles"] = string.Join(", ", missing);
        }

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(ZoneDomain, ClassicNameHash.Of(stem, ZoneHashBits)),
            RecordType = ZoneRecordType,
            EditorId = ClassicRecordNaming.ToEditorId(stem),
            FullName = map is not null && map.ZoneName.Length > 0 ? map.ZoneName : null,
            Fields = fields
        };
    }

    /// <summary>
    ///     Palette facts, measured rather than assumed: the components really do exceed 63 in all
    ///     21 retail files, which is why the repo's shared 6-bit range sniff correctly leaves these
    ///     unpromoted, and the magenta colour key sits at entry 6 in 13 zones and is absent in 8.
    /// </summary>
    private static void AddPaletteFields(Dictionary<string, object?> fields, string stem, byte[] paletteBytes)
    {
        var palette = ShadowkeyZonePalette.Parse(paletteBytes, stem + ".pal");
        var maximum = 0;
        var distinct = new HashSet<uint>();
        for (var i = 0; i < Palette.EntryCount; i++)
        {
            var (r, g, b, _) = palette.GetEntry(i);
            maximum = Math.Max(maximum, Math.Max(r, Math.Max(g, b)));
            distinct.Add(((uint)r << 16) | ((uint)g << 8) | b);
        }

        var first = palette.GetEntry(0);
        fields["PaletteIs8Bit"] = maximum > 63;
        fields["PaletteMaxComponent"] = maximum;
        fields["PaletteDistinctColours"] = distinct.Count;
        fields["PaletteFirstEntry"] = $"{first.R:X2}{first.G:X2}{first.B:X2}";

        var colourKey = ShadowkeyZonePalette.FindColourKeyIndex(paletteBytes);
        fields["PaletteHasColourKey"] = colourKey.HasValue;
        if (colourKey is { } keyIndex)
        {
            fields["PaletteColourKeyIndex"] = keyIndex;
        }
    }

    /// <summary>One trigger rectangle: whole-tile bounds plus the script label that reacts to it.</summary>
    private static GenericEsmRecord BuildTriggerRecord(string stem, int index, ShadowkeyTriggerZone trigger)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Zone"] = stem,
            ["Index"] = index,
            ["Name"] = trigger.Name,
            ["X0"] = trigger.X0,
            ["Y0"] = trigger.Y0,
            ["X1"] = trigger.X1,
            ["Y1"] = trigger.Y1,
            ["Width"] = trigger.Width,
            ["Height"] = trigger.Height
        };

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(
                ZoneTableDomain, ZoneTableIndex(stem, index, false, "trigger rectangle")),
            RecordType = TriggerRecordType,
            EditorId = ClassicRecordNaming.ToEditorId($"{stem}_trg{index:D3}_{trigger.Name}"),
            FullName = trigger.Name.Length > 0 ? trigger.Name : null,
            Fields = fields
        };
    }

    /// <summary>
    ///     One surface. <c>TextureIndexInRange</c> is the cross-file check that fixed this record's
    ///     field order in the first place: the index is the LAST byte and it is bounded by the
    ///     zone's <c>.ztx</c> texture count in 21/21.
    /// </summary>
    private static GenericEsmRecord BuildSurfaceRecord(
        string stem, int index, ShadowkeySurface surface, int? textureCount)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Zone"] = stem,
            ["Index"] = index,
            ["TextureIndex"] = surface.TextureIndex,
            ["Log2U"] = surface.Log2U,
            ["Log2V"] = surface.Log2V,
            ["OffsetU"] = surface.OffsetU,
            ["OffsetV"] = surface.OffsetV,
            ["Flags"] = surface.Flags
        };

        if (textureCount is { } count)
        {
            fields["TextureIndexInRange"] = surface.TextureIndex < count;
        }

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(ZoneTableDomain, ZoneTableIndex(stem, index, true, "surface")),
            RecordType = SurfaceRecordType,
            EditorId = ClassicRecordNaming.ToEditorId($"{stem}_sur{index:D3}"),
            FullName = null,
            Fields = fields
        };
    }

    /// <summary>
    ///     One placement, with its <c>entities.txt</c> row and model slot resolved and its
    ///     <c>.stn</c> lock joined by instance name.
    /// </summary>
    private static GenericEsmRecord BuildEntityRecord(
        string stem,
        int index,
        ShadowkeyEntity entity,
        InstallTables tables,
        ShadowkeyModelTable? zoneModels,
        Dictionary<string, string> lockConditions,
        Dictionary<string, int> nameCounts)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Zone"] = stem,
            ["Index"] = index,
            ["X"] = entity.TileX,
            ["Y"] = entity.TileY,
            ["Z"] = entity.TileZ,
            ["Angle0"] = entity.Angle0,
            ["Angle1"] = entity.Angle1,
            ["Angle2"] = entity.Angle2,
            ["Scale"] = entity.Scale,
            ["EntityId"] = entity.EntityId,
            ["InstanceName"] = entity.Name,
            ["Script"] = entity.Script
        };

        AddResolvedEntity(fields, entity, tables, zoneModels);

        if (nameCounts.TryGetValue(entity.Name, out var shared) && shared > 1)
        {
            // "noname" is the editor default and 41 records hold the truncated "Containe", so an
            // instance name is NOT unique: a .stn row joins to every placement carrying the name.
            fields["InstanceNameShared"] = shared;
        }

        if (lockConditions.TryGetValue(entity.Name, out var conditions))
        {
            fields["LockConditions"] = conditions;
        }

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(EntityDomain, EntityIndex(stem, index)),
            RecordType = EntityRecordType,
            EditorId = ClassicRecordNaming.ToEditorId($"{stem}_{index:D4}_{entity.Name}"),
            FullName = entity.Script.Length > 0 ? entity.Script : null,
            Fields = fields
        };
    }

    /// <summary>
    ///     Walks the chain the binaries rely on — placement id → <c>entities.txt</c> → model slot →
    ///     the zone's residency list. A miss becomes a field rather than an exception: it would be
    ///     content drift to surface, and on retail it never happens (8,258 of 8,258 resolve).
    /// </summary>
    private static void AddResolvedEntity(
        Dictionary<string, object?> fields,
        ShadowkeyEntity entity,
        InstallTables tables,
        ShadowkeyModelTable? zoneModels)
    {
        if (tables.Entities is not { } entities)
        {
            return;
        }

        var resolution = ShadowkeyTextTables.Resolve(entity.EntityId, entities, zoneModels ?? tables.Models);
        fields["EntityResolved"] = resolution.EntityFound;
        if (resolution.Entity is not { } definition)
        {
            return;
        }

        fields["EntityName"] = definition.Name;
        fields["EntityKind"] = definition.Kind;
        fields["ModelIndex"] = definition.ModelIndex;

        // The placement's script field usually restates the entity's name; where it does not, the
        // instance overrides the shared script (monsters\Skelos_Undriel.s -> ..._Azra.s).
        var declared = definition.ScriptPath ?? definition.Name.TrimStart('!');
        fields["ScriptOverride"] = entity.Script.Length > 0
                                   && !entity.Script.Equals(declared, StringComparison.OrdinalIgnoreCase);

        if (resolution.Model is { } model)
        {
            fields["ModelFile"] = model.File;
            fields["ModelResident"] = resolution.IsResident;
        }
    }

    /// <summary>
    ///     Emits the two global packs. Empty slots are skipped — 11 of 237 mesh slots and 131 of
    ///     384 sprite slots hold nothing, and an empty slot has no geometry, no size and an offset
    ///     that merely repeats its neighbour's.
    /// </summary>
    private static void PopulatePacks(
        IGameFileSystem install,
        string root,
        InstallTables tables,
        RecordCollection records,
        Dictionary<uint, string> seen,
        CancellationToken cancellationToken)
    {
        var index = install.TryReadAllBytes(Combine(root, ModelIndexName));
        var pack = install.TryReadAllBytes(Combine(root, ModelPackName));
        if (index is not null && pack is not null)
        {
            var namesBytes = install.TryReadAllBytes(Combine(root, ModelsTableName));
            var names = namesBytes is null ? null : Encoding.Latin1.GetString(namesBytes);
            var meshes = ShadowkeyModelPack.Parse(index, pack, names, ModelPackName);
            foreach (var entry in meshes.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.IsEmpty)
                {
                    Add(records, seen, BuildMeshRecord(entry, meshes.GetMesh(entry.Index), tables));
                }
            }
        }

        var spriteBytes = install.TryReadAllBytes(Combine(root, SpritePackName));
        if (spriteBytes is null)
        {
            return;
        }

        var sprites = ShadowkeySpritePack.Parse(spriteBytes, SpritePackName);
        for (var slot = 0; slot < sprites.Count; slot++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sprites.GetSprite(slot) is { } sprite)
            {
                Add(records, seen, BuildSpriteRecord(sprite, sprites.Sizes[slot], tables));
            }
        }
    }

    /// <summary>One mesh slot: the <c>models.txt</c> line, the record's own geometry census, and how many zones load it.</summary>
    private static GenericEsmRecord BuildMeshRecord(
        ShadowkeyModelPackEntry entry, ShadowkeyMesh? mesh, InstallTables tables)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Slot"] = entry.Index,
            ["Offset"] = entry.Offset,
            ["Size"] = entry.Size,

            // The three models.txt numeric columns are not decodable from the pack — they match
            // neither the texture size nor the vertex extents — so they are carried verbatim.
            ["DeclaredFlag"] = entry.Flag,
            ["DeclaredWidth"] = entry.Width,
            ["DeclaredHeight"] = entry.Height,
            ["ZonesLoading"] = tables.MeshZoneCounts.GetValueOrDefault(entry.Index)
        };

        AddIfPresent(fields, "File", entry.FileName);

        if (mesh is not null)
        {
            fields["Frames"] = mesh.FrameCount;
            fields["Vertices"] = mesh.VertexCount;
            fields["Faces"] = mesh.Faces.Count;
            fields["Uvs"] = mesh.Uvs.Count;
            fields["TextureWidth"] = mesh.Textures.Width;
            fields["TextureHeight"] = mesh.Textures.Height;

            // Faces carry no texture index, so extra textures are alternative whole-mesh skins.
            fields["Skins"] = mesh.Textures.Skins.Count;
            fields["Sequences"] = mesh.Sequences.Count;
        }

        var label = entry.FileName is { Length: > 0 } file
            ? Path.GetFileNameWithoutExtension(file)
            : "model" + entry.Index.ToString(CultureInfo.InvariantCulture);

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(PackDomain, (uint)entry.Index),
            RecordType = MeshRecordType,
            EditorId = ClassicRecordNaming.ToEditorId(label),
            FullName = entry.FileName,
            Fields = fields
        };
    }

    /// <summary>One sprite slot. Sprites are self-contained — palette embedded — so nothing here refers to a zone palette.</summary>
    private static GenericEsmRecord BuildSpriteRecord(ShadowkeySprite sprite, uint size, InstallTables tables)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Slot"] = sprite.Index,
            ["Width"] = sprite.Width,
            ["Height"] = sprite.Height,
            ["Size"] = size,
            ["PaletteEntriesUsed"] = sprite.UsedPaletteLength,
            ["SpanPixels"] = sprite.SpanPixelCount,
            ["EmptyRows"] = sprite.EmptyRowCount,
            ["ZonesListing"] = tables.SpriteZoneCounts.GetValueOrDefault(sprite.Index)
        };

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(PackDomain, SpriteSlotBias + (uint)sprite.Index),
            RecordType = SpriteRecordType,
            EditorId = ClassicRecordNaming.ToEditorId($"sprite{sprite.Index:D3}"),
            FullName = null,
            Fields = fields
        };
    }

    /// <summary>
    ///     <c>hash12(stem) &lt;&lt; 12 | recordIndex</c>. Throws rather than renumbering when a zone
    ///     outgrows its half of the index — retail's largest is 1,072 placements against 4,096
    ///     slots, so this is a contract check, not an expected path.
    /// </summary>
    private static uint EntityIndex(string stem, int index)
    {
        const int limit = 1 << EntityIndexBits;
        if (index >= limit)
        {
            throw new InvalidDataException(
                $"'{stem}.ent': placement {index} exceeds the {limit}-slot FormID budget; the index split must be widened rather than renumbered.");
        }

        return (ClassicNameHash.Of(stem, StemHashBits) << EntityIndexBits) | (uint)index;
    }

    /// <summary>
    ///     <c>hash12(stem) &lt;&lt; 12 | family &lt;&lt; 11 | rowIndex</c> — the shared trigger and
    ///     surface index space of <see cref="ZoneTableDomain" />.
    /// </summary>
    private static uint ZoneTableIndex(string stem, int index, bool isSurface, string what)
    {
        const int limit = 1 << ZoneTableIndexBits;
        if (index >= limit)
        {
            throw new InvalidDataException(
                $"'{stem}': {what} {index} exceeds the {limit}-slot FormID budget; the index split must be widened rather than renumbered.");
        }

        var family = isSurface ? SurfaceFamilyBit : 0u;
        return (ClassicNameHash.Of(stem, StemHashBits) << EntityIndexBits) | family | (uint)index;
    }

    /// <summary>
    ///     Adds a record, refusing to emit two with the same id. A name hash can collide and the
    ///     contract is to fail loudly rather than silently renumber, which would break exactly the
    ///     cross-install diff the synthetic ids exist for.
    /// </summary>
    private static void Add(RecordCollection records, Dictionary<uint, string> seen, GenericEsmRecord record)
    {
        var label = record.EditorId ?? record.RecordType;
        if (!seen.TryAdd(record.FormId, label))
        {
            throw new InvalidDataException(
                $"Shadowkey records: '{label}' and '{seen[record.FormId]}' both map to 0x{record.FormId:X8}; " +
                "the index split must be widened rather than renumbered.");
        }

        records.GenericRecords.Add(record);
    }

    /// <summary>Instance name to the conditions the zone's <c>.stn</c> attaches to it.</summary>
    private static Dictionary<string, string> GroupLocks(IReadOnlyList<ShadowkeyLockEntry> locks)
    {
        var grouped = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in locks)
        {
            if (!grouped.TryGetValue(entry.EntityName, out var list))
            {
                list = [];
                grouped[entry.EntityName] = list;
            }

            list.Add(entry.Condition);
        }

        return grouped.ToDictionary(pair => pair.Key, pair => string.Join(", ", pair.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>How many placements of the zone carry each instance name.</summary>
    private static Dictionary<string, int> CountInstanceNames(IReadOnlyList<ShadowkeyEntity> placements)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var placement in placements)
        {
            counts[placement.Name] = counts.GetValueOrDefault(placement.Name) + 1;
        }

        return counts;
    }

    private static ShadowkeyEntityTable? ReadEntityTable(IGameFileSystem install, string root)
    {
        var bytes = install.TryReadAllBytes(Combine(root, EntitiesTableName));
        return bytes is null ? null : ShadowkeyTextTables.ParseEntities(bytes, EntitiesTableName);
    }

    private static ShadowkeyModelTable? ReadModelTable(IGameFileSystem install, string root, string fileName)
    {
        var bytes = install.TryReadAllBytes(Combine(root, fileName));
        return bytes is null ? null : ShadowkeyTextTables.ParseModels(bytes, fileName);
    }

    /// <summary>
    ///     Reads a <c>&lt;zone&gt;_sprites.txt</c>: one <c>global.spr</c> slot per line and nothing
    ///     else. No binary field of the zone set indexes this manifest — it is reached from the
    ///     scripts — so there is no grammar to enforce and a line that is not a number is skipped
    ///     rather than treated as corruption.
    /// </summary>
    private static List<int>? ReadSpriteList(IGameFileSystem install, string root, string fileName)
    {
        var bytes = install.TryReadAllBytes(Combine(root, fileName));
        if (bytes is null)
        {
            return null;
        }

        var slots = new List<int>();
        foreach (var line in Encoding.Latin1.GetString(bytes).Split('\n'))
        {
            if (int.TryParse(line.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var slot))
            {
                slots.Add(slot);
            }
        }

        return slots;
    }

    private static byte[]? ReadZoneFile(
        IGameFileSystem install, string root, string stem, string extension, List<string> missing)
    {
        var bytes = install.TryReadAllBytes(Combine(root, stem + extension));
        if (bytes is null)
        {
            missing.Add(stem + extension);
        }

        return bytes;
    }

    private static void AddIfPresent(Dictionary<string, object?> fields, string key, string? value)
    {
        if (value is { Length: > 0 })
        {
            fields[key] = value;
        }
    }

    private static string Tiles(float value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string Combine(string root, string fileName)
    {
        return root.Length == 0 ? fileName : root + "\\" + fileName;
    }

    private static string FileNameOf(string virtualPath)
    {
        var slash = virtualPath.LastIndexOf('\\');
        return slash < 0 ? virtualPath : virtualPath[(slash + 1)..];
    }

    private static string DirectoryOf(string virtualPath)
    {
        var slash = virtualPath.LastIndexOf('\\');
        return slash < 0 ? string.Empty : virtualPath[..slash];
    }

    /// <summary>
    ///     The install-wide text tables plus the cross-reference counters the per-zone pass fills
    ///     in for the global pack records: how many zones load each mesh slot, and how many list
    ///     each sprite slot. Both are residency masks over one shared slot space, which is why the
    ///     slot is the pack records' identity.
    /// </summary>
    private sealed class InstallTables
    {
        /// <summary><c>entities.txt</c>, or null when the install does not ship it.</summary>
        public ShadowkeyEntityTable? Entities { get; init; }

        /// <summary>The global <c>models.txt</c>, used when a zone ships no residency list of its own.</summary>
        public ShadowkeyModelTable? Models { get; init; }

        /// <summary>Mesh slot to the number of zones whose model list marks it resident.</summary>
        public Dictionary<int, int> MeshZoneCounts { get; } = new();

        /// <summary>Sprite slot to the number of zone sprite manifests naming it.</summary>
        public Dictionary<int, int> SpriteZoneCounts { get; } = new();

        /// <summary>Folds one zone's two manifests into the counters.</summary>
        public void CountResidency(ShadowkeyModelTable? zoneModels, IReadOnlyList<int>? spriteSlots)
        {
            if (zoneModels is not null)
            {
                foreach (var model in zoneModels.Models.Where(m => !m.IsUnused))
                {
                    MeshZoneCounts[model.Index] = MeshZoneCounts.GetValueOrDefault(model.Index) + 1;
                }
            }

            if (spriteSlots is null)
            {
                return;
            }

            foreach (var slot in spriteSlots.Distinct())
            {
                SpriteZoneCounts[slot] = SpriteZoneCounts.GetValueOrDefault(slot) + 1;
            }
        }
    }
}
