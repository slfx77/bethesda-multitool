using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Synthetic vectors for <see cref="ShadowkeyZoneFiles" /> and
///     <see cref="ShadowkeyTextTables" />, shaped after the 21 retail zones surveyed 2026-09-05.
///     <para>
///         Shadowkey is Symbian/ARM and therefore LITTLE-endian, so every vector here is written
///         little-endian and the counts are the ones a big-endian reader would blow up on. The
///         traps that cost real bytes on retail each get their own case: a name field with stale
///         text after the NUL, an 8-byte instance name with no NUL at all, a 0xFF byte inside a
///         script field, and the 0xCCCC alignment holes.
///     </para>
/// </summary>
public sealed class ShadowkeyZoneFileTests
{
    private static byte[] Zon(params (ushort X0, ushort Y0, ushort X1, ushort Y1, byte[] Name)[] rectangles)
    {
        var bytes = new byte[2 + (rectangles.Length * ShadowkeyZoneFiles.TriggerRecordLength)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)rectangles.Length);
        for (var i = 0; i < rectangles.Length; i++)
        {
            var at = 2 + (i * ShadowkeyZoneFiles.TriggerRecordLength);
            var (x0, y0, x1, y1, name) = rectangles[i];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), x0);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 2), y0);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 4), x1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 6), y1);
            name.CopyTo(bytes.AsSpan(at + 8));
        }

        return bytes;
    }

    /// <summary>A 64-byte name field: the text, a NUL, then the tail the writer left behind.</summary>
    private static byte[] NameField(string text, byte[] tail)
    {
        var field = new byte[ShadowkeyZoneFiles.NameFieldLength];
        Array.Fill(field, (byte)0xCC);
        var written = Encoding.Latin1.GetBytes(text, field);
        field[written] = 0;
        tail.CopyTo(field.AsSpan(written + 1));
        return field;
    }

    private static byte[] LengthPrefixed(string text)
    {
        var payload = Encoding.Latin1.GetBytes(text);
        var bytes = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)payload.Length);
        payload.CopyTo(bytes.AsSpan(2));
        return bytes;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = 0;
        foreach (var part in parts)
        {
            total += part.Length;
        }

        var bytes = new byte[total];
        var at = 0;
        foreach (var part in parts)
        {
            part.CopyTo(bytes.AsSpan(at));
            at += part.Length;
        }

        return bytes;
    }

    private static byte[] EntityRecord(
        int x,
        int y,
        int z,
        int angle2,
        ushort scale,
        uint entityId,
        byte[]? name = null,
        byte[]? script = null)
    {
        var length = name is null && script is null
            ? ShadowkeyZoneFiles.EntityCoreLength
            : ShadowkeyZoneFiles.EntityRecordLength;
        var bytes = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, x);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), y);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), z);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), angle2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(24), scale);

        // The alignment hole before the id is 0xCCCC in 8,258 of 8,258 retail records.
        bytes[26] = 0xCC;
        bytes[27] = 0xCC;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), entityId);
        name?.CopyTo(bytes.AsSpan(32));
        script?.CopyTo(bytes.AsSpan(40));
        return bytes;
    }

    private static byte[] EntityFile(uint count, params byte[][] records)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, count);
        return Concat([header, .. records]);
    }

    [Fact]
    public void ParseZon_ReadsRectanglesAndCutsNameAtNul()
    {
        var bytes = Zon(
            (3, 4, 60, 70, NameField("FearFrost_Exit", [])),
            (0, 0, 127, 127, NameField("ghasts", [])));

        var zones = ShadowkeyZoneFiles.ParseZon(bytes, "azra.zon");

        Assert.Equal(2, zones.Count);
        Assert.Equal(3, zones[0].X0);
        Assert.Equal(4, zones[0].Y0);
        Assert.Equal(60, zones[0].X1);
        Assert.Equal(70, zones[0].Y1);
        Assert.Equal("FearFrost_Exit", zones[0].Name);
        Assert.Equal(58, zones[0].Width);
        Assert.Equal(67, zones[0].Height);
        Assert.Equal("ghasts", zones[1].Name);
    }

    /// <summary>
    ///     The writer serialises one reused stack struct, so a shorter name leaves the tail of the
    ///     previous one in place: ffarena record 1 is literally "Crystal\0t_Exit" over record 0's
    ///     "FearFrost_Exit". Everything past the first NUL must be discarded.
    /// </summary>
    [Fact]
    public void ParseZon_DiscardsStaleTextAfterTheNul()
    {
        var stale = Encoding.Latin1.GetBytes("t_Exit\0");
        var bytes = Zon((1, 1, 2, 2, NameField("Crystal", stale)));

        var zones = ShadowkeyZoneFiles.ParseZon(bytes, "ffarena.zon");

        Assert.Equal("Crystal", zones[0].Name);
    }

    [Fact]
    public void ParseZon_EmptyFileIsAnEmptyList()
    {
        var zones = ShadowkeyZoneFiles.ParseZon([0x00, 0x00], "empty.zon");

        Assert.Empty(zones);
    }

    [Fact]
    public void ParseZon_RejectsShortFile()
    {
        var error = Assert.Throws<InvalidDataException>(() => ShadowkeyZoneFiles.ParseZon([0x01], "azra.zon"));

        Assert.Contains("azra.zon", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseZon_RejectsTilingMismatch()
    {
        var bytes = Zon((0, 0, 1, 1, NameField("x", [])));
        var truncated = bytes[..^1];

        var error = Assert.Throws<InvalidDataException>(() => ShadowkeyZoneFiles.ParseZon(truncated, "azra.zon"));

        Assert.Contains("azra.zon", error.Message, StringComparison.Ordinal);
        Assert.Contains("73", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A big-endian reader would see 256 rectangles in a 2-record file — hence this case.</summary>
    [Fact]
    public void ParseZon_CountIsLittleEndian()
    {
        var bytes = Zon((0, 0, 1, 1, NameField("only", [])));

        Assert.Equal(0x01, bytes[0]);
        Assert.Equal(0x00, bytes[1]);
        Assert.Single(ShadowkeyZoneFiles.ParseZon(bytes, "azra.zon"));
    }

    [Fact]
    public void ParseStn_ReadsConditionEntityPairs()
    {
        var bytes = Concat(
            [0x02, 0x00],
            LengthPrefixed("resistDisarm[15]"),
            LengthPrefixed("z6"),
            LengthPrefixed("resistDisarm[28]"),
            LengthPrefixed("door12"));

        var entries = ShadowkeyZoneFiles.ParseStn(bytes, "broken1.stn");

        Assert.Equal(2, entries.Count);
        Assert.Equal("resistDisarm[15]", entries[0].Condition);
        Assert.Equal("z6", entries[0].EntityName);
        Assert.Equal(15, entries[0].ResistDisarm);
        Assert.Equal(28, entries[1].ResistDisarm);
        Assert.Equal("door12", entries[1].EntityName);
    }

    [Fact]
    public void ParseStn_UnknownConditionShapeReportsNullRatherThanThrowing()
    {
        var bytes = Concat([0x01, 0x00], LengthPrefixed("resistPick[7"), LengthPrefixed("chest"));

        var entries = ShadowkeyZoneFiles.ParseStn(bytes, "synthetic.stn");

        Assert.Null(entries[0].ResistDisarm);
        Assert.Equal("resistPick[7", entries[0].Condition);
    }

    [Fact]
    public void ParseStn_RejectsLengthPastEndOfFile()
    {
        var bytes = Concat([0x01, 0x00], [0x40, 0x00], Encoding.Latin1.GetBytes("short"));

        var error = Assert.Throws<InvalidDataException>(() => ShadowkeyZoneFiles.ParseStn(bytes, "crypt1.stn"));

        Assert.Contains("crypt1.stn", error.Message, StringComparison.Ordinal);
        Assert.Contains("row 0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseStn_RejectsMissingSecondStringOfAPair()
    {
        var bytes = Concat([0x01, 0x00], LengthPrefixed("resistDisarm[3]"));

        var error = Assert.Throws<InvalidDataException>(() => ShadowkeyZoneFiles.ParseStn(bytes, "crypt1.stn"));

        Assert.Contains("entity name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseStn_RejectsTrailingBytes()
    {
        var bytes = Concat([0x01, 0x00], LengthPrefixed("resistDisarm[3]"), LengthPrefixed("z6"), [0x00]);

        var error = Assert.Throws<InvalidDataException>(() => ShadowkeyZoneFiles.ParseStn(bytes, "crypt1.stn"));

        Assert.Contains("leaving 1 unread", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseStn_RejectsShortFile()
    {
        Assert.Throws<InvalidDataException>(() => ShadowkeyZoneFiles.ParseStn([0x00], "crypt1.stn"));
    }

    [Fact]
    public void ParsePth_ReadsNamedPathsWithEightBytePoints()
    {
        var points = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(points, 0x00001000);
        BinaryPrimitives.WriteUInt32LittleEndian(points.AsSpan(4), 0x00000200);
        BinaryPrimitives.WriteUInt32LittleEndian(points.AsSpan(8), 0x00000100);
        BinaryPrimitives.WriteUInt32LittleEndian(points.AsSpan(12), 0x00000080);

        var header = new byte[ShadowkeyZoneFiles.PathHeaderLength];
        NameField("UmbraKeth", []).CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(ShadowkeyZoneFiles.NameFieldLength), 2);
        header[66] = 0xCC;
        header[67] = 0xCC;

        var paths = ShadowkeyZoneFiles.ParsePth(Concat([0x01, 0x00], header, points), "crypt1.pth");

        var path = Assert.Single(paths);
        Assert.Equal("UmbraKeth", path.Name);
        Assert.Equal(2, path.Points.Count);
        Assert.Equal(16f, path.Points[0].TileX);
        Assert.Equal(2f, path.Points[0].TileY);
        Assert.Equal(1f, path.Points[1].TileX);
        Assert.Equal(0.5f, path.Points[1].TileY);
    }

    /// <summary>Four retail paths are the "New Path" placeholder with zero points.</summary>
    [Fact]
    public void ParsePth_AcceptsAPathWithNoPoints()
    {
        var header = new byte[ShadowkeyZoneFiles.PathHeaderLength];
        NameField("New Path", []).CopyTo(header, 0);

        var paths = ShadowkeyZoneFiles.ParsePth(Concat([0x01, 0x00], header), "delfhide.pth");

        Assert.Equal("New Path", paths[0].Name);
        Assert.Empty(paths[0].Points);
    }

    [Fact]
    public void ParsePth_RejectsHeaderPastEndOfFile()
    {
        var error = Assert.Throws<InvalidDataException>(
            () => ShadowkeyZoneFiles.ParsePth(Concat([0x01, 0x00], new byte[10]), "crypt1.pth"));

        Assert.Contains("crypt1.pth", error.Message, StringComparison.Ordinal);
        Assert.Contains("byte 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsePth_RejectsPointsPastEndOfFile()
    {
        var header = new byte[ShadowkeyZoneFiles.PathHeaderLength];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(ShadowkeyZoneFiles.NameFieldLength), 9);

        var error = Assert.Throws<InvalidDataException>(
            () => ShadowkeyZoneFiles.ParsePth(Concat([0x01, 0x00], header, new byte[8]), "crypt1.pth"));

        Assert.Contains("9 points", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsePth_RejectsTrailingBytes()
    {
        var header = new byte[ShadowkeyZoneFiles.PathHeaderLength];

        var error = Assert.Throws<InvalidDataException>(
            () => ShadowkeyZoneFiles.ParsePth(Concat([0x01, 0x00], header, [0x00, 0x00]), "crypt1.pth"));

        Assert.Contains("leaving 2 unread", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsePth_RejectsShortFile()
    {
        Assert.Throws<InvalidDataException>(() => ShadowkeyZoneFiles.ParsePth([0x01], "crypt1.pth"));
    }

    /// <summary>
    ///     The count is ONE byte and the texture index is the record's LAST byte — the reading that
    ///     makes max(index) == the zone's texture count minus one in all 21 zones.
    /// </summary>
    [Fact]
    public void ParseSur_ReadsCountByteAndTrailingTextureIndex()
    {
        byte[] bytes =
        [
            0x02,
            6, 6, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            5, 7, 0xFD, 0xFC, 0x58, 0x02, 0x20, 0x0F,
        ];

        var surfaces = ShadowkeyZoneFiles.ParseSur(bytes, "azra.sur");

        Assert.Equal(2, surfaces.Count);
        Assert.Equal(0, surfaces[0].TextureIndex);
        Assert.Equal(6, surfaces[0].Log2U);
        Assert.Equal(5, surfaces[1].Log2U);
        Assert.Equal(7, surfaces[1].Log2V);
        Assert.Equal(-771, surfaces[1].OffsetU);
        Assert.Equal(600, surfaces[1].OffsetV);
        Assert.Equal(0x20, surfaces[1].Flags);
        Assert.Equal(15, surfaces[1].TextureIndex);
    }

    [Fact]
    public void ParseSur_RejectsEmptyFile()
    {
        var error = Assert.Throws<InvalidDataException>(() => ShadowkeyZoneFiles.ParseSur([], "azra.sur"));

        Assert.Contains("azra.sur", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseSur_RejectsTilingMismatch()
    {
        var error = Assert.Throws<InvalidDataException>(
            () => ShadowkeyZoneFiles.ParseSur([0x02, 1, 2, 3, 4, 5, 6, 7, 8], "azra.sur"));

        Assert.Contains("17 bytes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseEnt_ReadsFixedPointPositionsAndBothNameFields()
    {
        var record = EntityRecord(
            15142,
            30960,
            -6008,
            -32000,
            256,
            85,
            Encoding.Latin1.GetBytes("noname\0Ì"),
            Encoding.Latin1.GetBytes("gryphon\0"));

        var list = ShadowkeyZoneFiles.ParseEnt(EntityFile(1, record), "azra.ent");

        Assert.True(list.HasNames);
        var entity = Assert.Single(list.Entities);
        Assert.Equal(15142, entity.RawX);
        Assert.Equal(30960, entity.RawY);
        Assert.Equal(-6008, entity.RawZ);
        Assert.Equal(59.1484375f, entity.TileX);
        Assert.Equal(-23.46875f, entity.TileZ);
        Assert.Equal(-32000, entity.Angle2);
        Assert.Equal(256, entity.RawScale);
        Assert.Equal(1f, entity.Scale);
        Assert.Equal(85u, entity.EntityId);
        Assert.Equal("noname", entity.Name);
        Assert.Equal("gryphon", entity.Script);
    }

    /// <summary>
    ///     41 retail records hold "Containe" in the 8-byte name with no terminator — the editor
    ///     wrote "Container" into eight bytes. The field is bounded, never NUL-required.
    /// </summary>
    [Fact]
    public void ParseEnt_AcceptsAnEightByteNameWithNoNul()
    {
        var record = EntityRecord(
            0,
            0,
            0,
            0,
            256,
            1234,
            Encoding.Latin1.GetBytes("Containe"),
            Encoding.Latin1.GetBytes("dstar_e\\Thief_RT_B.s\0"));

        var list = ShadowkeyZoneFiles.ParseEnt(EntityFile(1, record), "dstar_e.ent");

        Assert.Equal("Containe", list.Entities[0].Name);
        Assert.Equal(8, list.Entities[0].Name.Length);
        Assert.Equal("dstar_e\\Thief_RT_B.s", list.Entities[0].Script);
    }

    /// <summary>lakvan record 5 carries a 0xFF byte inside its script text: Latin-1, never a throw.</summary>
    [Fact]
    public void ParseEnt_DecodesNonAsciiScriptTextAsLatin1()
    {
        byte[] script = [(byte)'b', (byte)'o', (byte)'t', (byte)'t', 0xFF, (byte)'g', 0x00, 0xCC];
        var record = EntityRecord(0, 0, 0, 0, 256, 7, Encoding.Latin1.GetBytes("noname\0"), script);

        var list = ShadowkeyZoneFiles.ParseEnt(EntityFile(1, record), "lakvan.ent");

        Assert.Equal("bottÿg", list.Entities[0].Script);
    }

    /// <summary>
    ///     A script field whose NUL is followed by an earlier record's text — azra record 2 reads
    ///     "bottle" over the previous record's "tradinggate" — must cut at the NUL.
    /// </summary>
    [Fact]
    public void ParseEnt_DiscardsStaleScriptTextAfterTheNul()
    {
        var script = new byte[ShadowkeyZoneFiles.ScriptFieldLength];
        Array.Fill(script, (byte)0xCC);
        Encoding.Latin1.GetBytes("bottle\0gate\0").CopyTo(script, 0);
        var record = EntityRecord(0, 0, 0, 0, 128, 5, Encoding.Latin1.GetBytes("noname\0"), script);

        var list = ShadowkeyZoneFiles.ParseEnt(EntityFile(1, record), "azra.ent");

        Assert.Equal("bottle", list.Entities[0].Script);
    }

    [Fact]
    public void ParseEnt_RejectsShortFile()
    {
        var error = Assert.Throws<InvalidDataException>(
            () => ShadowkeyZoneFiles.ParseEnt([0x01, 0x00, 0x00], "azra.ent"));

        Assert.Contains("azra.ent", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseEnt_RejectsTilingMismatch()
    {
        var record = EntityRecord(0, 0, 0, 0, 256, 1, new byte[8], new byte[32]);

        var error = Assert.Throws<InvalidDataException>(
            () => ShadowkeyZoneFiles.ParseEnt(EntityFile(2, record), "azra.ent"));

        Assert.Contains("2 records of 72 bytes", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A count that would overrun the address space must be refused, not allocated.</summary>
    [Fact]
    public void ParseEnt_RejectsAbsurdCountWithoutOverflowing()
    {
        var bytes = new byte[76];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, uint.MaxValue);

        var error = Assert.Throws<InvalidDataException>(() => ShadowkeyZoneFiles.ParseEnt(bytes, "azra.ent"));

        Assert.Contains("4294967295 records", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseSta_ReadsThirtyTwoByteCoresWithoutNames()
    {
        var list = ShadowkeyZoneFiles.ParseSta(
            EntityFile(2, EntityRecord(512, 1024, -256, 16384, 256, 85), EntityRecord(0, 0, 0, 0, 512, 4012)),
            "azra.sta");

        Assert.False(list.HasNames);
        Assert.Equal(2, list.Entities.Count);
        Assert.Equal(2f, list.Entities[0].TileX);
        Assert.Equal(4f, list.Entities[0].TileY);
        Assert.Equal(-1f, list.Entities[0].TileZ);
        Assert.Equal(16384, list.Entities[0].Angle2);
        Assert.Equal(85u, list.Entities[0].EntityId);
        Assert.Equal(string.Empty, list.Entities[0].Name);
        Assert.Equal(string.Empty, list.Entities[0].Script);
        Assert.Equal(2f, list.Entities[1].Scale);
    }

    [Fact]
    public void ParseSta_RejectsTilingMismatch()
    {
        var error = Assert.Throws<InvalidDataException>(
            () => ShadowkeyZoneFiles.ParseSta(EntityFile(3, EntityRecord(0, 0, 0, 0, 256, 1)), "azra.sta"));

        Assert.Contains("3 records of 32 bytes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseEntities_ReadsWhitespaceSeparatedRows()
    {
        const string text = "0 0 1 !NO_ENTITY\r\n\r\n8\t7\t11\tdoor.s\r\n4208  9  2  monsters\\Azra.s\r\n4202 9 2 !bag_loot\r\n";

        var table = ShadowkeyTextTables.ParseEntities(text, "entities.txt");

        Assert.Equal(4, table.Entities.Count);
        Assert.Equal(0u, table.Entities[0].Id);
        Assert.True(table.Entities[0].IsPlaceholder);
        Assert.Null(table.Entities[0].ScriptPath);

        var door = table.Find(8);
        Assert.NotNull(door);
        Assert.Equal(7, door.ModelIndex);
        Assert.Equal(11, door.Kind);
        Assert.Equal("door.s", door.ScriptPath);

        // Ids are not sorted on retail (4208 precedes 4202) and lookup must not assume they are.
        Assert.Equal("monsters\\Azra.s", table.Find(4208)?.Name);
        Assert.Equal("!bag_loot", table.Find(4202)?.Name);
        Assert.Null(table.Find(9999));
    }

    [Fact]
    public void ParseEntities_RejectsWrongFieldCount()
    {
        var error = Assert.Throws<InvalidDataException>(
            () => ShadowkeyTextTables.ParseEntities("0 0 1 !NO_ENTITY\n5 6 7\n", "entities.txt"));

        Assert.Contains("line 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseEntities_RejectsNonNumericId()
    {
        var error = Assert.Throws<InvalidDataException>(
            () => ShadowkeyTextTables.ParseEntities("x 0 1 !NO_ENTITY\n", "entities.txt"));

        Assert.Contains("not a non-negative number", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseEntities_RejectsDuplicateId()
    {
        var error = Assert.Throws<InvalidDataException>(
            () => ShadowkeyTextTables.ParseEntities("7 0 1 a.s\n7 1 1 b.s\n", "entities.txt"));

        Assert.Contains("appears twice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseModels_ReadsRowsAndFlagsUnusedSlots()
    {
        const string text = "0 0 0 0 fern.bin\r\n1 2 64 64 bottle.bin\r\n2 0 0 0 NULL.bin\r\n";

        var table = ShadowkeyTextTables.ParseModels(text, "azra_models.txt");

        Assert.Equal(3, table.Models.Count);
        Assert.Equal(2, table.ResidentCount);
        Assert.False(table.Models[0].IsUnused);
        Assert.Equal(2, table.Models[1].Flag);
        Assert.Equal(64, table.Models[1].Width);
        Assert.True(table.Models[2].IsUnused);
        Assert.Null(table.Find(3));
        Assert.Null(table.Find(-1));
    }

    [Fact]
    public void ParseModels_RejectsNonContiguousIndex()
    {
        var error = Assert.Throws<InvalidDataException>(
            () => ShadowkeyTextTables.ParseModels("0 0 0 0 a.bin\n2 0 0 0 b.bin\n", "models.txt"));

        Assert.Contains("breaks the run", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseModels_RejectsWrongFieldCount()
    {
        var error = Assert.Throws<InvalidDataException>(
            () => ShadowkeyTextTables.ParseModels("0 0 0 a.bin\n", "models.txt"));

        Assert.Contains("line 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseModels_RejectsNonNumericField()
    {
        var error = Assert.Throws<InvalidDataException>(
            () => ShadowkeyTextTables.ParseModels("0 x 0 0 a.bin\n", "models.txt"));

        Assert.Contains("the flag field reads 'x'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_WalksEntityIdToTheZoneModelSlot()
    {
        var entities = ShadowkeyTextTables.ParseEntities("85 1 2 gryphon\n", "entities.txt");
        var zoneModels = ShadowkeyTextTables.ParseModels(
            "0 0 0 0 NULL.bin\n1 2 64 64 bottle.bin\n", "azra_models.txt");

        var resolved = ShadowkeyTextTables.Resolve(85, entities, zoneModels);

        Assert.True(resolved.EntityFound);
        Assert.True(resolved.ModelFound);
        Assert.True(resolved.IsResident);
        Assert.Equal("bottle.bin", resolved.Model?.File);
        Assert.Equal("gryphon", resolved.Entity?.Name);
    }

    [Fact]
    public void Resolve_ReportsMissesInsteadOfThrowing()
    {
        var entities = ShadowkeyTextTables.ParseEntities("85 4 2 gryphon\n", "entities.txt");
        var zoneModels = ShadowkeyTextTables.ParseModels("0 0 0 0 NULL.bin\n", "azra_models.txt");

        var unknownId = ShadowkeyTextTables.Resolve(9999, entities, zoneModels);
        Assert.False(unknownId.EntityFound);
        Assert.False(unknownId.ModelFound);
        Assert.False(unknownId.IsResident);
        Assert.Equal(9999u, unknownId.EntityId);

        var indexPastTheList = ShadowkeyTextTables.Resolve(85, entities, zoneModels);
        Assert.True(indexPastTheList.EntityFound);
        Assert.False(indexPastTheList.ModelFound);

        var notLoadedHere = ShadowkeyTextTables.Resolve(
            85,
            ShadowkeyTextTables.ParseEntities("85 0 2 gryphon\n", "entities.txt"),
            zoneModels);
        Assert.True(notLoadedHere.ModelFound);
        Assert.False(notLoadedHere.IsResident);

        var noModelTable = ShadowkeyTextTables.Resolve(85, entities, null);
        Assert.True(noModelTable.EntityFound);
        Assert.False(noModelTable.ModelFound);
    }

    [Fact]
    public void ParseEntities_FromBytesDecodesLatin1()
    {
        var bytes = new List<byte>(Encoding.Latin1.GetBytes("50 3 4 weapons\\bl"));
        bytes.Add(0xE9);
        bytes.AddRange(Encoding.Latin1.GetBytes("de.s\r\n"));

        var table = ShadowkeyTextTables.ParseEntities(bytes.ToArray(), "entities.txt");

        Assert.Equal("weapons\\bléde.s", table.Entities[0].Name);
    }
}
