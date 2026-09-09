using System.Globalization;
using BethesdaMultitool.Core.Formats.Audio;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>
///     Synthesizes browsable records from a Daggerfall install. From <c>MAPS.BSA</c>: one
///     <c>DREG</c> per region (all 62, the 17 authored-empty ones included — they are named
///     entities the political overlay refers to) and one <c>DLOC</c> per location, carrying the
///     map-table classification and coordinates plus the exterior and dungeon summaries. From
///     <c>TEXT.RSC</c>: one <c>DTXT</c> per string-table record with its subrecord variants. From
///     <c>BOOKS/</c>: one <c>DBOK</c> per book with its pages. Then one <c>DMSH</c> per ARCH3D
///     mesh, one <c>DBLK</c> per BLOCKS entry, one <c>DQST</c> per quest, one <c>DSND</c> per
///     DAGGER.SND sound and one <c>DMUS</c> per MIDI.BSA song.
/// </summary>
internal static class DaggerfallRecordSource
{
    /// <summary>Domain byte for <c>DREG</c> (region) records.</summary>
    public const byte RegionDomain = 0x10;

    /// <summary>Domain byte for <c>DLOC</c> (location) records.</summary>
    public const byte LocationDomain = 0x11;

    /// <summary>Domain byte for <c>DTXT</c> (TEXT.RSC string) records.</summary>
    public const byte TextDomain = 0x12;

    /// <summary>Domain byte for <c>DBOK</c> (book) records.</summary>
    public const byte BookDomain = 0x13;

    /// <summary>The record signature used for a region.</summary>
    public const string RegionRecordType = "DREG";

    /// <summary>The record signature used for a location.</summary>
    public const string LocationRecordType = "DLOC";

    /// <summary>The record signature used for a TEXT.RSC record.</summary>
    public const string TextRecordType = "DTXT";

    /// <summary>The record signature used for a book.</summary>
    public const string BookRecordType = "DBOK";

    /// <summary>Domain byte for <c>DMSH</c> (ARCH3D mesh) records.</summary>
    public const byte MeshDomain = 0x14;

    /// <summary>The record signature used for a mesh.</summary>
    public const string MeshRecordType = "DMSH";

    /// <summary>Domain byte for <c>DBLK</c> (BLOCKS.BSA block) records.</summary>
    public const byte BlockDomain = 0x15;

    /// <summary>The record signature used for a block.</summary>
    public const string BlockRecordType = "DBLK";

    /// <summary>Domain byte for <c>DQST</c> (quest) records.</summary>
    public const byte QuestDomain = 0x16;

    /// <summary>The record signature used for a quest.</summary>
    public const string QuestRecordType = "DQST";

    /// <summary>Domain byte for <c>DSND</c> (sound effect) records.</summary>
    public const byte SoundDomain = 0x17;

    /// <summary>Domain byte for <c>DMUS</c> (music) records.</summary>
    public const byte MusicDomain = 0x18;

    /// <summary>The record signature used for a sound effect.</summary>
    public const string SoundRecordType = "DSND";

    /// <summary>The record signature used for a music track.</summary>
    public const string MusicRecordType = "DMUS";

    /// <summary>Domain byte for <c>DVID</c> (movie) records.</summary>
    public const byte VideoDomain = 0x19;

    /// <summary>The record signature used for a movie.</summary>
    public const string VideoRecordType = "DVID";

    private const string MusicArchiveName = "MIDI.BSA";

    private const string MapsArchiveName = "MAPS.BSA";
    private const string BooksDirectoryName = "BOOKS";

    /// <summary>
    ///     Reads <paramref name="dataRoot" /> (the ARENA2 directory) and appends every synthesized
    ///     record to <paramref name="records" />.
    /// </summary>
    public static void Populate(string dataRoot, RecordCollection records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);
        ArgumentNullException.ThrowIfNull(records);

        PopulateMaps(dataRoot, records, cancellationToken);
        PopulateText(dataRoot, records, cancellationToken);
        PopulateBooks(dataRoot, records, cancellationToken);
        PopulateMeshes(dataRoot, records, cancellationToken);
        PopulateBlocks(dataRoot, records, cancellationToken);
        PopulateQuests(dataRoot, records, cancellationToken);
        PopulateSounds(dataRoot, records, cancellationToken);
        PopulateMusic(dataRoot, records, cancellationToken);
        PopulateVideos(dataRoot, records, cancellationToken);
    }

    private static void PopulateVideos(string dataRoot, RecordCollection records, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(dataRoot))
        {
            return;
        }

        var paths = Directory.EnumerateFiles(dataRoot)
            .Where(p => DaggerfallVidFile.IsVidFileName(Path.GetFileName(p)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = 0; i < paths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.GenericRecords.Add(BuildVideoRecord(i,
                DaggerfallVidFile.Parse(File.ReadAllBytes(paths[i]), Path.GetFileName(paths[i]))));
        }
    }

    /// <summary>Builds the record form of one movie. Identity is its position in name order.</summary>
    public static GenericEsmRecord BuildVideoRecord(int index, DaggerfallVidFile video)
    {
        ArgumentNullException.ThrowIfNull(video);

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Width"] = video.Width,
            ["Height"] = video.Height,
            ["Frames"] = video.FrameCount,
            ["DeclaredFrames"] = video.DeclaredFrameCount,
            ["AudioSamples"] = video.Audio.Length,
            ["AudioSeconds"] = Math.Round(video.AudioSeconds, 2),
            ["HeaderDelay"] = video.GlobalDelay,
            ["EndsCleanly"] = video.EndOfFileSeen
        };

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(VideoDomain, (uint)index),
            RecordType = VideoRecordType,
            EditorId = ClassicRecordNaming.ToEditorId(Path.GetFileNameWithoutExtension(video.Name).ToUpperInvariant()),
            FullName = null,
            Fields = fields
        };
    }

    private static void PopulateSounds(string dataRoot, RecordCollection records, CancellationToken cancellationToken)
    {
        var path = Path.Combine(dataRoot, DaggerfallSoundFile.FileName);
        if (!File.Exists(path))
        {
            return;
        }

        var sounds = DaggerfallSoundFile.Open(path);
        for (var i = 0; i < sounds.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.GenericRecords.Add(BuildSoundRecord(sounds, i));
        }
    }

    private static void PopulateMusic(string dataRoot, RecordCollection records, CancellationToken cancellationToken)
    {
        var path = Path.Combine(dataRoot, MusicArchiveName);
        if (!File.Exists(path))
        {
            return;
        }

        using var archive = ArchiveReader.Open(path);
        var index = 0;
        foreach (var entry in archive.ListFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = archive.ReadFile(entry.FullPath);
            records.GenericRecords.Add(BuildMusicRecord(index++, entry.Name, bytes));
        }
    }

    /// <summary>
    ///     Builds the record form of one DAGGER.SND sound. Identity is the archive index because
    ///     retail repeats one sound id.
    /// </summary>
    public static GenericEsmRecord BuildSoundRecord(DaggerfallSoundFile sounds, int index)
    {
        ArgumentNullException.ThrowIfNull(sounds);

        var id = sounds.SoundId(index);
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["SoundId"] = (int)id,
            ["Samples"] = sounds.Samples(index).Length,
            ["Seconds"] = Math.Round(sounds.DurationSeconds(index), 3),
            ["Format"] = $"{DaggerfallSoundFile.SampleRate} Hz, {DaggerfallSoundFile.BitsPerSample}-bit unsigned, mono"
        };

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(SoundDomain, (uint)index),
            RecordType = SoundRecordType,
            EditorId = $"SOUND{id}",
            FullName = null,
            Fields = fields
        };
    }

    /// <summary>
    ///     Builds the record form of one MIDI.BSA entry. The HMI container is read for its track
    ///     table; its event stream is not decoded.
    /// </summary>
    public static GenericEsmRecord BuildMusicRecord(int index, string name, byte[]? bytes)
    {
        ArgumentNullException.ThrowIfNull(name);

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Bytes"] = bytes?.Length ?? 0
        };

        if (bytes is not null && HmiFile.IsHmi(bytes))
        {
            var song = HmiFile.Parse(bytes, name);
            fields["Tag"] = song.Tag;
            fields["Tracks"] = song.Tracks.Count;
            fields["LargestTrack"] = song.Tracks.Max(t => t.Length);
        }

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(MusicDomain, (uint)index),
            RecordType = MusicRecordType,
            EditorId = ClassicRecordNaming.ToEditorId(name),
            FullName = null,
            Fields = fields
        };
    }

    private static void PopulateQuests(string dataRoot, RecordCollection records, CancellationToken cancellationToken)
    {
        foreach (var name in DaggerfallQuestFile.EnumerateNames(dataRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.GenericRecords.Add(BuildQuestRecord(DaggerfallQuestFile.Load(dataRoot, name)));
        }
    }

    /// <summary>
    ///     Builds the record form of one quest: its text messages, and the QBN's size and header
    ///     words. The QBN's contents are not decoded — no documentation for that layout is
    ///     available here — so nothing is claimed about them.
    /// </summary>
    public static GenericEsmRecord BuildQuestRecord(DaggerfallQuestFile quest)
    {
        ArgumentNullException.ThrowIfNull(quest);

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Messages"] = quest.Text?.Records.Count ?? 0,
            ["CompiledBytes"] = quest.Compiled.Length
        };

        if (quest.Compiled.Length > 0)
        {
            fields["CompiledHeader"] = string.Join(" ",
                quest.CompiledHeader.Take(16).Select(w => w.ToString("X4", CultureInfo.InvariantCulture)));
        }

        string? first = null;
        foreach (var record in quest.Text?.Records ?? [])
        {
            var text = record.Subrecords.FirstOrDefault(s => s.Length > 0);
            if (text is null)
            {
                continue;
            }

            first ??= text;
            fields[$"Message{record.Id:D4}"] = ClassicRecordNaming.OneLine(text);
        }

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(QuestDomain, StableNameIndex(quest.Name)),
            RecordType = QuestRecordType,
            EditorId = ClassicRecordNaming.ToEditorId(quest.Name),
            FullName = first is null ? null : ClassicRecordNaming.Summarize(first),
            Fields = fields
        };
    }

    /// <summary>
    ///     A quest's identity is its base name, so its stable index is a 24-bit FNV-1a hash of the
    ///     upper-cased name — the same scheme the Arena .INF records use.
    /// </summary>
    private static uint StableNameIndex(string name)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var character in name.ToUpperInvariant())
        {
            hash = (hash ^ character) * prime;
        }

        var folded = ((hash >> 24) ^ hash) & ClassicFormIdScheme.MaxIndex;
        return folded == 0 ? 1 : folded;
    }

    private static void PopulateBlocks(string dataRoot, RecordCollection records, CancellationToken cancellationToken)
    {
        var blocksPath = Path.Combine(dataRoot, DaggerfallBlocksFile.FileName);
        if (!File.Exists(blocksPath))
        {
            return;
        }

        var blocks = DaggerfallBlocksFile.Open(blocksPath);
        for (var i = 0; i < blocks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.GenericRecords.Add(BuildBlockRecord(blocks, i));
        }
    }

    /// <summary>Builds the record form of one BLOCKS.BSA entry. Identity is the archive index.</summary>
    public static GenericEsmRecord BuildBlockRecord(DaggerfallBlocksFile blocks, int index)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        var name = blocks.Name(index);
        var type = blocks.TypeAt(index);
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Kind"] = type.ToString().ToUpperInvariant(),
            ["Bytes"] = blocks.RecordBytes(index).Length
        };

        string? fullName = null;
        switch (type)
        {
            case DaggerfallBlockType.Rmb:
                fullName = DescribeRmb(blocks.ParseRmb(index), fields);
                break;
            case DaggerfallBlockType.Rdb:
                DescribeRdb(blocks.ParseRdb(index), fields);
                break;
        }

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(BlockDomain, (uint)index),
            RecordType = BlockRecordType,
            EditorId = ClassicRecordNaming.ToEditorId(name),
            FullName = fullName,
            Fields = fields
        };
    }

    private static string? DescribeRmb(DaggerfallRmbBlock block, Dictionary<string, object?> fields)
    {
        var exteriors = block.SubRecords.Select(s => s.Exterior).ToList();
        var interiors = block.SubRecords.Select(s => s.Interior).ToList();
        fields["SubBlocks"] = block.SubRecords.Count;
        fields["Models"] = block.AllModels.Count();
        fields["Flats"] = exteriors.Sum(e => e.Flats.Count) + interiors.Sum(i => i.Flats.Count) + block.MiscFlats.Count;
        fields["People"] = exteriors.Sum(e => e.People.Count) + interiors.Sum(i => i.People.Count);
        fields["Doors"] = exteriors.Sum(e => e.Doors.Count) + interiors.Sum(i => i.Doors.Count);
        fields["Section3"] = exteriors.Sum(e => e.Section3.Count) + interiors.Sum(i => i.Section3.Count);
        fields["GroundTextures"] = block.GroundTiles.Select(t => t.TextureRecord).Distinct().Count();
        fields["Scenery"] = block.GroundScenery.Count(s => s.HasScenery);

        var buildingTypes = block.Buildings.Take(block.SubRecords.Count)
            .GroupBy(b => b.BuildingType)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.ToString(), StringComparer.Ordinal)
            .Select(g => $"{g.Key}={g.Count()}")
            .ToList();
        if (buildingTypes.Count > 0)
        {
            fields["BuildingTypes"] = string.Join(", ", buildingTypes);
        }

        return string.IsNullOrEmpty(block.HeaderName) ? null : block.HeaderName;
    }

    private static void DescribeRdb(DaggerfallRdbBlock block, Dictionary<string, object?> fields)
    {
        var objects = block.AllObjects.ToList();
        fields["DungeonType"] = block.Type.ToString();
        fields["Width"] = block.Width;
        fields["Height"] = block.Height;
        fields["ListsUsed"] = block.ObjectRoots.Count(r => r.Objects.Count > 0);
        fields["Objects"] = objects.Count;
        fields["Models"] = objects.Count(o => o.Type == DaggerfallRdbResourceType.Model);
        fields["Flats"] = objects.Count(o => o.Type == DaggerfallRdbResourceType.Flat);
        fields["Lights"] = objects.Count(o => o.Type == DaggerfallRdbResourceType.Light);
        fields["Actions"] = objects.Count(o => o.Model?.Action is not null);
        fields["ModelIds"] = objects
            .Select(o => o.Model)
            .OfType<DaggerfallRdbModelResource>()
            .Select(m => block.ModelReferences[m.ModelIndex].ModelIdNumber)
            .Where(id => id is not null)
            .Distinct()
            .Count();
    }

    private static void PopulateMaps(string dataRoot, RecordCollection records, CancellationToken cancellationToken)
    {
        var archivePath = Path.Combine(dataRoot, MapsArchiveName);
        if (!File.Exists(archivePath))
        {
            return;
        }

        var maps = DaggerfallMapsFile.Open(archivePath);
        foreach (var region in maps.Regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.GenericRecords.Add(BuildRegionRecord(region));
            foreach (var location in region.Locations)
            {
                records.GenericRecords.Add(BuildLocationRecord(region, location));
            }
        }
    }

    private static void PopulateText(string dataRoot, RecordCollection records, CancellationToken cancellationToken)
    {
        var textPath = Path.Combine(dataRoot, DaggerfallTextFile.FileName);
        if (!File.Exists(textPath))
        {
            return;
        }

        var text = DaggerfallTextFile.Parse(File.ReadAllBytes(textPath));
        foreach (var record in text.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.GenericRecords.Add(BuildTextRecord(record));
        }
    }

    private static void PopulateBooks(string dataRoot, RecordCollection records, CancellationToken cancellationToken)
    {
        var booksDirectory = Path.Combine(dataRoot, BooksDirectoryName);
        if (!Directory.Exists(booksDirectory))
        {
            return;
        }

        var bookPaths = Directory.EnumerateFiles(booksDirectory)
            .Where(p => DaggerfallBookFile.IsBookFileName(Path.GetFileName(p)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        foreach (var path in bookPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.GenericRecords.Add(
                BuildBookRecord(DaggerfallBookFile.Parse(File.ReadAllBytes(path), Path.GetFileName(path))));
        }
    }

    private static void PopulateMeshes(string dataRoot, RecordCollection records, CancellationToken cancellationToken)
    {
        var arch3dPath = Path.Combine(dataRoot, DaggerfallArch3DFile.FileName);
        if (!File.Exists(arch3dPath))
        {
            return;
        }

        var meshes = DaggerfallArch3DFile.Open(arch3dPath);
        for (var i = 0; i < meshes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (meshes.TryParse(i, out var mesh, out _))
            {
                records.GenericRecords.Add(BuildMeshRecord(i, mesh));
            }
        }
    }

    /// <summary>Builds the record form of one ARCH3D mesh. Identity is the archive index (ten retail ids repeat).</summary>
    public static GenericEsmRecord BuildMeshRecord(int index, XnGineMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        var size = mesh.Size;
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ObjectId"] = (int)mesh.ObjectId,
            ["Version"] = mesh.VersionTag,
            ["Points"] = mesh.Points.Count,
            ["Planes"] = mesh.Planes.Count,
            ["Triangles"] = mesh.Planes.Sum(p => Math.Max(0, p.Points.Count - 2)),
            ["Radius"] = mesh.RadiusUnits,
            ["SizeX"] = size.X,
            ["SizeY"] = size.Y,
            ["SizeZ"] = size.Z,
            ["ObjectData"] = mesh.ObjectDataCount,
            ["Textures"] = string.Join(", ", mesh.UniqueTextures.Select(t => $"{t.Archive:D3}:{t.Record}"))
        };

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(MeshDomain, (uint)index),
            RecordType = MeshRecordType,
            EditorId = $"MESH{mesh.ObjectId}",
            FullName = null,
            Fields = fields
        };
    }

    /// <summary>Builds the record form of one TEXT.RSC record.</summary>
    public static GenericEsmRecord BuildTextRecord(DaggerfallTextRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Id"] = record.Id,
            ["Variants"] = record.Subrecords.Count,
            ["Bytes"] = record.Raw.Length
        };

        var raw = record.Raw.Span;
        if (raw.IndexOf(DaggerfallTextTokens.InputCursor) >= 0)
        {
            fields["InputCursor"] = true;
        }

        for (var i = 0; i < record.Subrecords.Count; i++)
        {
            fields[$"Text{i:D2}"] = ClassicRecordNaming.OneLine(record.Subrecords[i]);
        }

        var first = record.Subrecords.FirstOrDefault(s => s.Length > 0);
        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(TextDomain, (uint)record.Id),
            RecordType = TextRecordType,
            EditorId = $"TEXT{record.Id:D4}",
            FullName = first is null ? null : ClassicRecordNaming.Summarize(first),
            Fields = fields
        };
    }

    /// <summary>Builds the record form of one book.</summary>
    public static GenericEsmRecord BuildBookRecord(DaggerfallBookFile book)
    {
        ArgumentNullException.ThrowIfNull(book);

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Author"] = book.Author,
            ["Pages"] = book.Pages.Count,
            ["Price"] = book.Price,
            ["Unknown1"] = (int)book.Unknown1
        };

        if (book.IsNaughty)
        {
            fields["Naughty"] = true;
        }

        for (var i = 0; i < book.PageTexts.Count; i++)
        {
            fields[$"Page{i:D2}"] = ClassicRecordNaming.OneLine(book.PageTexts[i]);
        }

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(BookDomain, (uint)book.Number),
            RecordType = BookRecordType,
            EditorId = Path.GetFileNameWithoutExtension(book.Name),
            FullName = book.Title,
            Fields = fields
        };
    }

    /// <summary>Builds the record form of one region.</summary>
    public static GenericEsmRecord BuildRegionRecord(DaggerfallRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["RegionIndex"] = region.Index,
            ["Locations"] = region.Locations.Count,
            ["Dungeons"] = region.Locations.Count(l => l.Dungeon is not null)
        };

        foreach (var group in region.Locations.GroupBy(l => l.LocationType).OrderBy(g => g.Key))
        {
            fields[group.Key.ToString()] = group.Count();
        }

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(RegionDomain, (uint)region.Index + 1),
            RecordType = RegionRecordType,
            EditorId = ClassicRecordNaming.ToEditorId(region.Name),
            FullName = region.Name,
            Fields = fields
        };
    }

    /// <summary>Builds the record form of one location.</summary>
    public static GenericEsmRecord BuildLocationRecord(DaggerfallRegion region, DaggerfallLocation location)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(location);

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Region"] = region.Name,
            ["Type"] = location.LocationType.ToString(),
            ["MapX"] = location.MapPixelX,
            ["MapY"] = location.MapPixelY,
            ["Longitude"] = location.Longitude,
            ["Latitude"] = location.Latitude,
            ["WorldPixelId"] = location.WorldPixelId,
            ["LocationId"] = (int)location.LocationId,
            ["Discovered"] = location.Discovered,
            ["Key"] = location.Key,
            ["BlocksWide"] = (int)location.BlocksWide,
            ["BlocksHigh"] = (int)location.BlocksHigh,
            ["Doors"] = location.DoorCount,
            ["Buildings"] = location.Buildings.Count
        };

        if (location.Buildings.Count > 0)
        {
            fields["BuildingTypes"] = string.Join(", ", location.Buildings
                .GroupBy(b => b.BuildingType)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key.ToString(), StringComparer.Ordinal)
                .Select(g => $"{g.Key}={g.Count()}"));
        }

        if (location.PortTownAndUnknown != 0)
        {
            fields["ExteriorFlags"] = $"0x{location.PortTownAndUnknown:X2}";
        }

        if (location.DungeonType != DaggerfallDungeonType.None)
        {
            fields["DungeonType"] = location.DungeonType.ToString();
        }

        if (location.Dungeon is { } dungeon)
        {
            fields["DungeonBlocks"] = dungeon.Blocks.Count;
            fields["DungeonDoors"] = dungeon.DoorCount;
        }

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(LocationDomain, LocationIndex(region.Index, location.Index)),
            RecordType = LocationRecordType,
            EditorId = ClassicRecordNaming.ToEditorId($"{region.Name}_{location.Name}"),
            FullName = location.Name,
            Fields = fields
        };
    }

    /// <summary>
    ///     A location's identity is its region and its position in that region's authored tables,
    ///     so the stable index is composed: region in the high byte, table index below. The largest
    ///     retail region holds 1,833 locations, far inside the 16-bit slot.
    /// </summary>
    public static uint LocationIndex(int regionIndex, int locationIndex)
    {
        if (regionIndex is < 0 or >= DaggerfallMapsFile.RegionCount)
        {
            throw new ArgumentOutOfRangeException(nameof(regionIndex), regionIndex,
                "Region index is outside the 62-region table.");
        }

        if (locationIndex is < 0 or > 0xFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(locationIndex), locationIndex,
                "Location index does not fit the 16-bit slot.");
        }

        return ((uint)regionIndex << 16) | (uint)locationIndex;
    }
}
