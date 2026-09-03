using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>
///     Synthesizes browsable records from a Daggerfall install. From <c>MAPS.BSA</c>: one
///     <c>DREG</c> per region (all 62, the 17 authored-empty ones included — they are named
///     entities the political overlay refers to) and one <c>DLOC</c> per location, carrying the
///     map-table classification and coordinates plus the exterior and dungeon summaries. From
///     <c>TEXT.RSC</c>: one <c>DTXT</c> per string-table record with its subrecord variants. From
///     <c>BOOKS/</c>: one <c>DBOK</c> per book with its pages.
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

    private const string MapsArchiveName = "MAPS.BSA";
    private const string BooksDirectoryName = "BOOKS";

    /// <summary>
    ///     Reads <paramref name="dataRoot" /> (the ARENA2 directory) and appends every synthesized
    ///     record to <paramref name="records" />.
    /// </summary>
    public static void Populate(string dataRoot, RecordCollection records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);
        ArgumentNullException.ThrowIfNull(records);

        var archivePath = Path.Combine(dataRoot, MapsArchiveName);
        if (File.Exists(archivePath))
        {
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

        var textPath = Path.Combine(dataRoot, DaggerfallTextFile.FileName);
        if (File.Exists(textPath))
        {
            var text = DaggerfallTextFile.Parse(File.ReadAllBytes(textPath));
            foreach (var record in text.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                records.GenericRecords.Add(BuildTextRecord(record));
            }
        }

        var booksDirectory = Path.Combine(dataRoot, BooksDirectoryName);
        if (Directory.Exists(booksDirectory))
        {
            var bookPaths = Directory.EnumerateFiles(booksDirectory)
                .Where(p => DaggerfallBookFile.IsBookFileName(Path.GetFileName(p)))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
            foreach (var path in bookPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                records.GenericRecords.Add(BuildBookRecord(DaggerfallBookFile.Parse(File.ReadAllBytes(path), Path.GetFileName(path))));
            }
        }
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
            throw new ArgumentOutOfRangeException(nameof(regionIndex), regionIndex, "Region index is outside the 62-region table.");
        }

        if (locationIndex is < 0 or > 0xFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(locationIndex), locationIndex, "Location index does not fit the 16-bit slot.");
        }

        return ((uint)regionIndex << 16) | (uint)locationIndex;
    }
}
