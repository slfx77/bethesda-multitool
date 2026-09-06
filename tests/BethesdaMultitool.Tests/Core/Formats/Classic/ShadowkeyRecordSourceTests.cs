using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Vfs;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Synthetic checks on the Shadowkey record source: a hand-built application directory of a
///     handful of bytes per file, mounted exactly the way the analyzer mounts a real one.
///     <para>
///         The properties worth pinning here are the ones a retail census cannot prove. That a
///         record's id comes from SOURCE IDENTITY rather than enumeration order is shown by
///         building the same zone twice — once alone, once behind another zone — and demanding the
///         same ids; that the source fails loudly rather than renumbering is shown by overflowing
///         the placement index; and that an absent file leaves the collection short rather than
///         throwing is shown by shipping a zone with nothing but its <c>.zon</c>.
///     </para>
/// </summary>
public sealed class ShadowkeyRecordSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "shadowkey-records-" + Guid.NewGuid().ToString("N"));

    public ShadowkeyRecordSourceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
    }

    [Fact]
    public void AZoneBecomesOneRecordPerZoneTriggerSurfaceAndPlacement()
    {
        WriteFullZone("alpha");

        var records = Populate();

        Assert.Equal(1, Count(records, ShadowkeyRecordSource.ZoneRecordType));
        Assert.Equal(2, Count(records, ShadowkeyRecordSource.TriggerRecordType));
        Assert.Equal(3, Count(records, ShadowkeyRecordSource.SurfaceRecordType));
        Assert.Equal(4, Count(records, ShadowkeyRecordSource.EntityRecordType));
        Assert.Equal(records.Count, records.Select(r => r.FormId).Distinct().Count());

        // Each family lands in its declared domain; the two per-zone sub-tables share 0x4A.
        Assert.All(records, r => Assert.InRange(
            ClassicFormIdScheme.DomainOf(r.FormId),
            ShadowkeyRecordSource.FirstDomain,
            ShadowkeyRecordSource.LastDomain));
        Assert.Equal(
            ShadowkeyRecordSource.ZoneDomain,
            ClassicFormIdScheme.DomainOf(Single(records, ShadowkeyRecordSource.ZoneRecordType).FormId));
        Assert.All(
            records.Where(r => r.RecordType is ShadowkeyRecordSource.TriggerRecordType
                or ShadowkeyRecordSource.SurfaceRecordType),
            r => Assert.Equal(ShadowkeyRecordSource.ZoneTableDomain, ClassicFormIdScheme.DomainOf(r.FormId)));
    }

    [Fact]
    public void TheZoneRecordCarriesItsCountsMapSizeTextureCountPaletteFactsLocksAndPaths()
    {
        WriteFullZone("alpha");

        var zone = Single(Populate(), ShadowkeyRecordSource.ZoneRecordType);

        Assert.Equal("alpha", zone.Fields["Stem"]);
        Assert.Equal(2, zone.Fields["TriggerCount"]);
        Assert.Equal(1, zone.Fields["LockCount"]);
        Assert.Equal(1, zone.Fields["PathCount"]);
        Assert.Equal(2, zone.Fields["PathPointCount"]);
        Assert.Equal(3, zone.Fields["SurfaceCount"]);
        Assert.Equal(4, zone.Fields["EntityCount"]);

        // Map geometry comes from the zlib envelope, texture count from the .ztx's first byte.
        Assert.Equal(4, zone.Fields["MapWidth"]);
        Assert.Equal(2, zone.Fields["MapHeight"]);
        Assert.Equal(8, zone.Fields["MapCells"]);
        Assert.Equal("Alpha Zone", zone.Fields["MapName"]);
        Assert.Equal(2, zone.Fields["TextureCount"]);
        Assert.Equal(1, zone.Fields["MaxSurfaceTextureIndex"]);

        // Palette facts are measured, not assumed: a component over 63 is what says these are
        // RGB888 rather than 6-bit VGA, and the magenta key is present at the fixture's entry 6.
        Assert.Equal(true, zone.Fields["PaletteIs8Bit"]);
        Assert.Equal(255, zone.Fields["PaletteMaxComponent"]);
        Assert.Equal(true, zone.Fields["PaletteHasColourKey"]);
        Assert.Equal(6, zone.Fields["PaletteColourKeyIndex"]);

        // The two tables too small to deserve a family ride inline on the zone.
        Assert.Equal("chest1=resistDisarm[15]", zone.Fields["Locks"]);
        Assert.Equal("Patrol (2 points)", zone.Fields["Paths"]);
        Assert.Equal("Patrol: 1,2 3,4.5", zone.Fields["PathPoints"]);

        Assert.False(zone.Fields.ContainsKey("MissingFiles"));
    }

    [Fact]
    public void APlacementCarriesItsTilePositionAndResolvesThroughEntitiesTxtToAModelFile()
    {
        WriteFullZone("alpha");

        var placement = Populate()
            .Single(r => r.RecordType == ShadowkeyRecordSource.EntityRecordType
                         && (string?)r.Fields["InstanceName"] == "door12");

        // 24.8 fixed point: the fixture writes 3.5, 4.25 and -1 tiles.
        Assert.Equal(3.5f, placement.Fields["X"]);
        Assert.Equal(4.25f, placement.Fields["Y"]);
        Assert.Equal(-1f, placement.Fields["Z"]);
        Assert.Equal(1f, placement.Fields["Scale"]);
        Assert.Equal(16384, placement.Fields["Angle2"]);

        Assert.Equal(true, placement.Fields["EntityResolved"]);
        Assert.Equal("door.s", placement.Fields["EntityName"]);
        Assert.Equal(11, placement.Fields["EntityKind"]);
        Assert.Equal(1, placement.Fields["ModelIndex"]);
        Assert.Equal("door.bin", placement.Fields["ModelFile"]);
        Assert.Equal(true, placement.Fields["ModelResident"]);

        // Its script field restates the entity's own, so it is not a per-instance override.
        Assert.Equal(false, placement.Fields["ScriptOverride"]);
    }

    [Fact]
    public void APerInstanceScriptIsReportedAsAnOverrideAndAModelTheZoneDoesNotLoadIsNotResident()
    {
        WriteFullZone("alpha");

        var records = Populate();
        var overridden = records.Single(r => r.RecordType == ShadowkeyRecordSource.EntityRecordType
                                             && (string?)r.Fields["InstanceName"] == "skelos2");
        Assert.Equal(@"monsters\Skelos_Azra.s", overridden.FullName);
        Assert.Equal(true, overridden.Fields["ScriptOverride"]);

        // Entity 40 points at model slot 2, which this zone's residency list blanks to NULL.bin.
        var absent = records.Single(r => r.RecordType == ShadowkeyRecordSource.EntityRecordType
                                         && (string?)r.Fields["InstanceName"] == "ghost");
        Assert.Equal(2, absent.Fields["ModelIndex"]);
        Assert.Equal(false, absent.Fields["ModelResident"]);
    }

    [Fact]
    public void ALockJoinsByInstanceNameToEveryPlacementCarryingIt()
    {
        // Two placements share the editor's default name and the .stn row names it, so the join is
        // one-to-many: the record has to report the ambiguity rather than pretend it picked one.
        WriteZoneFiles(
            "beta",
            zon: Zon(("gate", 0, 0, 1, 1)),
            stn: Stn(("resistDisarm[4]", "noname")),
            pth: Pth(),
            sur: Sur((7, 7, 0, 0, 0, 0)),
            ent: Ent(
                Placement(0, 0, 0, 0, 10, "noname", "chest_loot"),
                Placement(0, 0, 0, 0, 10, "noname", "chest_loot")));

        var placements = Populate()
            .Where(r => r.RecordType == ShadowkeyRecordSource.EntityRecordType)
            .ToList();

        Assert.Equal(2, placements.Count);
        Assert.All(placements, p => Assert.Equal("resistDisarm[4]", p.Fields["LockConditions"]));
        Assert.All(placements, p => Assert.Equal(2, p.Fields["InstanceNameShared"]));
    }

    [Fact]
    public void AZoneWithOnlyItsZonFileStillYieldsARecordNamingWhatIsMissing()
    {
        File.WriteAllBytes(Path.Combine(_root, "lonely.zon"), Zon(("only", 2, 3, 4, 5)));

        var records = Populate();

        var zone = Single(records, ShadowkeyRecordSource.ZoneRecordType);
        Assert.Equal(1, zone.Fields["TriggerCount"]);
        Assert.Equal(0, zone.Fields["EntityCount"]);
        Assert.False(zone.Fields.ContainsKey("TextureCount"));
        var missing = Assert.IsType<string>(zone.Fields["MissingFiles"]);
        Assert.Contains("lonely.ent", missing, StringComparison.Ordinal);
        Assert.Contains("lonely.pal", missing, StringComparison.Ordinal);

        // The trigger family still lands; the families whose files are absent are simply short.
        Assert.Equal(1, Count(records, ShadowkeyRecordSource.TriggerRecordType));
        Assert.Equal(0, Count(records, ShadowkeyRecordSource.EntityRecordType));
        Assert.Equal(0, Count(records, ShadowkeyRecordSource.SurfaceRecordType));
    }

    [Fact]
    public void AnInstallWithNoZoneAndNoPackYieldsNothingRatherThanThrowing()
    {
        File.WriteAllText(Path.Combine(_root, "readme.txt"), "not a Shadowkey tree");

        Assert.Empty(Populate());
    }

    [Fact]
    public void AZoneKeepsItsIdsWhenAnotherZoneIsAddedBeforeIt()
    {
        WriteFullZone("zulu");
        var alone = Populate().ToDictionary(r => r.EditorId!, r => r.FormId, StringComparer.Ordinal);

        // "alpha" sorts first, so every "zulu" record moves in enumeration order. If the index came
        // from that order rather than from the stem and the row ordinal, these ids would shift.
        WriteFullZone("alpha");
        var together = Populate().ToDictionary(r => r.EditorId!, r => r.FormId, StringComparer.Ordinal);

        foreach (var (editorId, formId) in alone)
        {
            Assert.Equal(formId, together[editorId]);
        }

        Assert.Equal(together.Count, together.Values.Distinct().Count());
    }

    [Fact]
    public void TriggerAndSurfaceRowsShareADomainWithoutColliding()
    {
        WriteFullZone("alpha");

        var records = Populate();
        var triggers = records.Where(r => r.RecordType == ShadowkeyRecordSource.TriggerRecordType).ToList();
        var surfaces = records.Where(r => r.RecordType == ShadowkeyRecordSource.SurfaceRecordType).ToList();

        // Same domain, same stem hash, disjoint halves: the family bit is bit 11 of the index.
        Assert.All(triggers.Concat(surfaces),
            r => Assert.Equal(ShadowkeyRecordSource.ZoneTableDomain, ClassicFormIdScheme.DomainOf(r.FormId)));
        Assert.All(triggers, r => Assert.Equal(0u, ClassicFormIdScheme.IndexOf(r.FormId) & (1u << 11)));
        Assert.All(surfaces, r => Assert.Equal(1u << 11, ClassicFormIdScheme.IndexOf(r.FormId) & (1u << 11)));
        Assert.Equal(
            triggers.Count + surfaces.Count,
            triggers.Concat(surfaces).Select(r => r.FormId).Distinct().Count());
    }

    [Fact]
    public void APlacementIndexBeyondTheFormIdBudgetFailsLoudlyRatherThanRenumbering()
    {
        // 4,096 placements is one past the 12 bits the split leaves a zone. Retail's largest zone
        // holds 1,072, so this can only happen to a file this reader has never seen — and the
        // contract is to say so, not to quietly reuse an id.
        var placements = new byte[4097][];
        for (var i = 0; i < placements.Length; i++)
        {
            placements[i] = Placement(0, 0, 0, 0, 10, "e" + i, "door.s");
        }

        WriteZoneFiles("huge", Zon(), Stn(), Pth(), Sur(), Ent(placements));

        var error = Assert.Throws<InvalidDataException>(() => { Populate(); });
        Assert.Contains("huge.ent", error.Message, StringComparison.Ordinal);
        Assert.Contains("renumbered", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGlobalPacksBecomeSlotKeyedRecordsCountingTheZonesThatLoadThem()
    {
        WriteFullZone("alpha");
        WriteGlobalPacks();

        var records = Populate();

        // models.txt holds three slots, two of them zero-size placeholders: only the packed one is
        // a record, because an empty slot has no geometry and an offset that repeats its neighbour.
        var mesh = Single(records, ShadowkeyRecordSource.MeshRecordType);
        Assert.Equal(0, mesh.Fields["Slot"]);
        Assert.Equal("tri.bin", mesh.FullName);
        Assert.Equal(1, mesh.Fields["Frames"]);
        Assert.Equal(3, mesh.Fields["Vertices"]);
        Assert.Equal(1, mesh.Fields["Faces"]);
        Assert.Equal(1, mesh.Fields["Skins"]);
        Assert.Equal(1, mesh.Fields["Sequences"]);
        Assert.Equal(1, mesh.Fields["ZonesLoading"]);

        var sprite = Single(records, ShadowkeyRecordSource.SpriteRecordType);
        Assert.Equal(0, sprite.Fields["Slot"]);
        Assert.Equal(2, sprite.Fields["Width"]);
        Assert.Equal(2, sprite.Fields["Height"]);
        Assert.Equal(2, sprite.Fields["SpanPixels"]);
        Assert.Equal(1, sprite.Fields["EmptyRows"]);
        Assert.Equal(1, sprite.Fields["ZonesListing"]);

        // Both packs share domain 0x4B; sprites sit above the mesh slots.
        Assert.Equal(ShadowkeyRecordSource.PackDomain, ClassicFormIdScheme.DomainOf(mesh.FormId));
        Assert.Equal(ShadowkeyRecordSource.PackDomain, ClassicFormIdScheme.DomainOf(sprite.FormId));
        Assert.NotEqual(mesh.FormId, sprite.FormId);
    }

    private List<GenericEsmRecord> Populate()
    {
        var records = new RecordCollection();
        using var install = new LooseFileSystem(_root);
        ShadowkeyRecordSource.Populate(install, records);
        return records.GenericRecords;
    }

    private static int Count(IEnumerable<GenericEsmRecord> records, string recordType)
    {
        return records.Count(r => r.RecordType == recordType);
    }

    private static GenericEsmRecord Single(IEnumerable<GenericEsmRecord> records, string recordType)
    {
        return records.Single(r => r.RecordType == recordType);
    }

    /// <summary>
    ///     A zone with one of every file: 2 rectangles, 1 lock, 1 path of 2 points, 3 surfaces,
    ///     4 placements, a 4x2 map, a 2-texture bank and a palette carrying the magenta key.
    /// </summary>
    private void WriteFullZone(string stem)
    {
        WriteZoneFiles(
            stem,
            zon: Zon(("temple", 0, 0, 3, 1), ("queue", 2, 0, 2, 0)),
            stn: Stn(("resistDisarm[15]", "chest1")),
            pth: Pth(("Patrol", [(256u, 512u), (768u, 1152u)])),
            sur: Sur((7, 7, 0, 0, 0, 0), (6, 6, 765, -150, 1, 1), (5, 5, -720, 830, 32, 1)),
            ent: Ent(
                Placement(896, 1088, -256, 16384, 10, "door12", "door.s"),
                Placement(0, 0, 0, 0, 30, "skelos2", @"monsters\Skelos_Azra.s"),
                Placement(0, 0, 0, 0, 40, "ghost", @"monsters\ghost.s"),
                Placement(0, 0, 0, 0, 10, "chest1", "chest_loot")));

        File.WriteAllBytes(Path.Combine(_root, stem + ".pal"), Pal());
        File.WriteAllBytes(Path.Combine(_root, stem + ".zmp"), Envelope(Zmp("Alpha Zone", 4, 2)));
        File.WriteAllBytes(Path.Combine(_root, stem + ".ztx"), Envelope(Ztx(2)));

        // The zone's residency mask: slot 2 is blanked, so the ghost's model is not loaded here.
        File.WriteAllText(
            Path.Combine(_root, stem + "_models.txt"),
            "0 0 0 0 tri.bin\r\n1 2 64 64 door.bin\r\n2 0 0 0 NULL.bin\r\n");
        File.WriteAllText(Path.Combine(_root, stem + "_sprites.txt"), "0\r\n0\r\n");

        // Install-wide tables, written once; a second zone simply overwrites them identically.
        File.WriteAllText(
            Path.Combine(_root, "entities.txt"),
            "10 1 11 door.s\r\n20 0 2 monsters\\Skelos.s\r\n30 0 2 monsters\\Skelos.s\r\n40 2 2 monsters\\ghost.s\r\n");
        File.WriteAllText(
            Path.Combine(_root, "models.txt"),
            "0 0 0 0 tri.bin\r\n1 2 64 64 door.bin\r\n2 0 0 0 ghost.bin\r\n");
    }

    private void WriteZoneFiles(string stem, byte[] zon, byte[] stn, byte[] pth, byte[] sur, byte[] ent)
    {
        File.WriteAllBytes(Path.Combine(_root, stem + ".zon"), zon);
        File.WriteAllBytes(Path.Combine(_root, stem + ".stn"), stn);
        File.WriteAllBytes(Path.Combine(_root, stem + ".pth"), pth);
        File.WriteAllBytes(Path.Combine(_root, stem + ".sur"), sur);
        File.WriteAllBytes(Path.Combine(_root, stem + ".ent"), ent);
    }

    /// <summary>A three-slot mesh pack (one packed, two empty) and a two-slot sprite pack.</summary>
    private void WriteGlobalPacks()
    {
        // Three slots, matching the three lines of the models.txt the zone fixture writes: the
        // packed triangle plus the two placeholders that carry no data.
        var mesh = Mesh();
        var index = new byte[4 + (3 * 8)];
        BinaryPrimitives.WriteUInt32LittleEndian(index, 3);
        BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(8), (uint)mesh.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(12), (uint)mesh.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(20), (uint)mesh.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(24), 0);
        File.WriteAllBytes(Path.Combine(_root, "models.idx"), index);
        File.WriteAllBytes(Path.Combine(_root, "models.huge"), mesh);

        var sprite = Sprite();
        var pack = new byte[8 + sprite.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(pack, (uint)sprite.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(pack.AsSpan(4), 0);
        sprite.CopyTo(pack.AsSpan(8));
        File.WriteAllBytes(Path.Combine(_root, "global.spr"), pack);
    }

    private static byte[] Zon(params (string Name, ushort X0, ushort Y0, ushort X1, ushort Y1)[] rectangles)
    {
        var bytes = new byte[2 + (72 * rectangles.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)rectangles.Length);
        for (var i = 0; i < rectangles.Length; i++)
        {
            var record = bytes.AsSpan(2 + (i * 72));
            BinaryPrimitives.WriteUInt16LittleEndian(record, rectangles[i].X0);
            BinaryPrimitives.WriteUInt16LittleEndian(record[2..], rectangles[i].Y0);
            BinaryPrimitives.WriteUInt16LittleEndian(record[4..], rectangles[i].X1);
            BinaryPrimitives.WriteUInt16LittleEndian(record[6..], rectangles[i].Y1);
            WriteFixedText(record.Slice(8, 64), rectangles[i].Name);
        }

        return bytes;
    }

    private static byte[] Stn(params (string Condition, string EntityName)[] rows)
    {
        var buffer = new List<byte> { (byte)rows.Length, 0 };
        foreach (var (condition, entityName) in rows)
        {
            AppendLengthPrefixed(buffer, condition);
            AppendLengthPrefixed(buffer, entityName);
        }

        return [.. buffer];
    }

    private static byte[] Pth(params (string Name, (uint X, uint Y)[] Points)[] paths)
    {
        var buffer = new List<byte> { (byte)paths.Length, 0 };
        foreach (var (name, points) in paths)
        {
            var header = new byte[68];
            WriteFixedText(header.AsSpan(0, 64), name);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(64), (ushort)points.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(66), 0xCCCC);
            buffer.AddRange(header);
            foreach (var (x, y) in points)
            {
                var point = new byte[8];
                BinaryPrimitives.WriteUInt32LittleEndian(point, x);
                BinaryPrimitives.WriteUInt32LittleEndian(point.AsSpan(4), y);
                buffer.AddRange(point);
            }
        }

        return [.. buffer];
    }

    private static byte[] Sur(params (byte A, byte B, short P, short Q, byte Flags, byte Texture)[] rows)
    {
        var bytes = new byte[1 + (8 * rows.Length)];
        bytes[0] = (byte)rows.Length;
        for (var i = 0; i < rows.Length; i++)
        {
            var record = bytes.AsSpan(1 + (i * 8));
            record[0] = rows[i].A;
            record[1] = rows[i].B;
            BinaryPrimitives.WriteInt16LittleEndian(record[2..], rows[i].P);
            BinaryPrimitives.WriteInt16LittleEndian(record[4..], rows[i].Q);
            record[6] = rows[i].Flags;
            record[7] = rows[i].Texture;
        }

        return bytes;
    }

    private static byte[] Ent(params byte[][] placements)
    {
        var bytes = new byte[4 + (72 * placements.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)placements.Length);
        for (var i = 0; i < placements.Length; i++)
        {
            placements[i].CopyTo(bytes.AsSpan(4 + (i * 72)));
        }

        return bytes;
    }

    private static byte[] Placement(int x, int y, int z, int yaw, uint entityId, string name, string script)
    {
        var record = new byte[72];
        BinaryPrimitives.WriteInt32LittleEndian(record, x);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), y);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8), z);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(20), yaw);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(24), 256);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(26), 0xCCCC);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(28), entityId);
        WriteFixedText(record.AsSpan(32, 8), name);
        WriteFixedText(record.AsSpan(40, 32), script);
        return record;
    }

    /// <summary>256 RGB triplets with the magenta colour key at entry 6, as 13 of the 21 zones have.</summary>
    private static byte[] Pal()
    {
        var bytes = new byte[768];
        for (var i = 0; i < 256; i++)
        {
            bytes[(i * 3) + 0] = (byte)i;
            bytes[(i * 3) + 1] = (byte)(255 - i);
            bytes[(i * 3) + 2] = 128;
        }

        bytes[18] = 0xFF;
        bytes[19] = 0x00;
        bytes[20] = 0xFF;
        return bytes;
    }

    private static byte[] Zmp(string zoneName, int width, int height)
    {
        var bytes = new byte[132 + (width * height * 6)];
        WriteFixedText(bytes.AsSpan(0, 32), zoneName);
        WriteFixedText(bytes.AsSpan(32, 32), "tester");
        WriteFixedText(bytes.AsSpan(64, 64), "synthetic");
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(128), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(130), (ushort)height);
        return bytes;
    }

    private static byte[] Ztx(int count)
    {
        var bytes = new byte[1 + (count * 128 * 128)];
        bytes[0] = (byte)count;
        return bytes;
    }

    /// <summary>The size-prefixed zlib envelope every compressed zone file is wrapped in.</summary>
    private static byte[] Envelope(byte[] payload)
    {
        using var buffer = new MemoryStream();
        using (var deflater = new ZLibStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflater.Write(payload, 0, payload.Length);
        }

        var compressed = buffer.ToArray();
        var file = new byte[4 + compressed.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(file, (uint)payload.Length);
        compressed.CopyTo(file.AsSpan(4));
        return file;
    }

    /// <summary>One triangle, one frame, one 2x2 skin and one sequence — the smallest legal record.</summary>
    private static byte[] Mesh()
    {
        var buffer = new List<byte>();
        AppendUInt16(buffer, 7);
        AppendUInt16(buffer, 1);
        AppendUInt16(buffer, 3);
        AppendUInt16(buffer, 3);
        AppendUInt16(buffer, 1);
        AppendUInt16(buffer, 9);
        AppendUInt16(buffer, 1);

        for (var v = 0; v < 3; v++)
        {
            AppendUInt16(buffer, (ushort)v);
            AppendUInt16(buffer, (ushort)(v * 2));
            AppendUInt16(buffer, 0);
        }

        for (var t = 0; t < 3; t++)
        {
            AppendUInt16(buffer, (ushort)(t * 256));
            AppendUInt16(buffer, 0);
        }

        AppendUInt16(buffer, 0);
        AppendUInt16(buffer, 1);
        AppendUInt16(buffer, 2);
        AppendUInt16(buffer, 0);
        AppendUInt16(buffer, 1);
        AppendUInt16(buffer, 2);

        AppendUInt16(buffer, 1);
        AppendUInt16(buffer, 2);
        AppendUInt16(buffer, 2);
        for (var texel = 0; texel < 4; texel++)
        {
            AppendUInt16(buffer, 0x0123);
        }

        AppendUInt16(buffer, 1);
        AppendUInt16(buffer, 0);
        AppendUInt16(buffer, 1);
        AppendUInt16(buffer, 1);

        return [.. buffer];
    }

    /// <summary>A 2x2 sprite: one painted row and one fully transparent one.</summary>
    private static byte[] Sprite()
    {
        var buffer = new List<byte>();
        AppendUInt16(buffer, 2);
        AppendUInt16(buffer, 2);
        for (var i = 0; i < 256; i++)
        {
            AppendUInt16(buffer, i < 4 ? (ushort)(0x0100 * i) : (ushort)0xCCCC);
        }

        AppendUInt16(buffer, 0);
        AppendUInt16(buffer, 2);
        buffer.Add(1);
        buffer.Add(2);

        AppendUInt16(buffer, 0xFFFF);
        AppendUInt16(buffer, 0xFFFF);
        return [.. buffer];
    }

    private static void AppendUInt16(List<byte> buffer, ushort value)
    {
        buffer.Add((byte)(value & 0xFF));
        buffer.Add((byte)(value >> 8));
    }

    private static void AppendLengthPrefixed(List<byte> buffer, string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        AppendUInt16(buffer, (ushort)bytes.Length);
        buffer.AddRange(bytes);
    }

    /// <summary>
    ///     Writes a fixed-width text field the way the game's editor did: the text, a NUL, then
    ///     0xCC stack fill — so a reader that forgets to cut at the NUL fails the tests.
    /// </summary>
    private static void WriteFixedText(Span<byte> field, string text)
    {
        field.Fill(0xCC);
        var bytes = Encoding.Latin1.GetBytes(text);
        bytes.AsSpan(0, Math.Min(bytes.Length, field.Length)).CopyTo(field);
        if (bytes.Length < field.Length)
        {
            field[bytes.Length] = 0;
        }
    }
}
