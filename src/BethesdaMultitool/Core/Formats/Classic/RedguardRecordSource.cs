using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Redguard;

namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>
///     Synthesizes browsable records from a Redguard install. <c>WORLD.INI</c> is the master
///     registry every other file hangs off, so its 29 worlds are the first record family: each
///     <c>RWLD</c> carries the world's map, terrain, palette and sky plus its lighting settings,
///     and says which of its referenced files are actually shipped. The map databases those
///     worlds name follow as <c>RMAP</c>/<c>ROBJ</c> via <see cref="RedguardMapRecordSource" />.
/// </summary>
internal static class RedguardRecordSource
{
    /// <summary>Domain byte for <c>RWLD</c> (world) records.</summary>
    public const byte WorldDomain = 0x30;

    /// <summary>The record signature used for a world.</summary>
    public const string WorldRecordType = "RWLD";

    /// <summary>Reads <paramref name="dataRoot" /> and appends every synthesized record.</summary>
    public static void Populate(string dataRoot, RecordCollection records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);
        ArgumentNullException.ThrowIfNull(records);

        var path = Path.Combine(dataRoot, RedguardWorldIni.FileName);
        if (!File.Exists(path))
        {
            return;
        }

        var registry = RedguardWorldIni.Load(dataRoot);
        foreach (var world in registry.Worlds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.GenericRecords.Add(BuildWorldRecord(dataRoot, world, registry));
        }

        RedguardMapRecordSource.Populate(dataRoot, records, cancellationToken);
        RedguardTextRecordSource.Populate(dataRoot, records, cancellationToken);
    }

    /// <summary>
    ///     Builds the record form of one world. Identity is its declared index, not its position:
    ///     retail skips 9, 10 and 16 and then uses 99, so position would not be stable.
    /// </summary>
    public static GenericEsmRecord BuildWorldRecord(string dataRoot, RedguardWorld world, RedguardWorldIni registry)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(registry);

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Index"] = world.Index,
            ["IsStartWorld"] = ReferenceEquals(registry.StartWorld, world)
        };

        AddPath(fields, dataRoot, "Map", world.MapPath);
        AddPath(fields, dataRoot, "Terrain", world.TerrainPath);
        AddPath(fields, dataRoot, "Palette", world.PalettePath);
        AddPath(fields, dataRoot, "Sky", world.SkyPath);

        if (world.RedbookTrack is { } track)
        {
            fields["RedbookTrack"] = track;
        }

        // Node maps are listed but never shipped — recorded as a count so the record does not
        // imply 32 openable files. See RedguardWorldIni's remarks.
        if (world.NodeMaps.Count > 0)
        {
            fields["NodeMapsDeclared"] = world.NodeMaps.Count;
        }

        // Everything the registry declared and this record has not already named, so a browsable
        // record never silently loses one of the 70 indexed keys.
        foreach (var (key, value) in world.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!IsModelled(key))
            {
                fields[key] = value;
            }
        }

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(WorldDomain, (uint)world.Index),
            RecordType = WorldRecordType,
            EditorId = ClassicRecordNaming.ToEditorId(NameOf(world)),
            FullName = null,
            Fields = fields
        };
    }

    /// <summary>Records a declared path and whether the install actually ships it.</summary>
    private static void AddPath(Dictionary<string, object?> fields, string dataRoot, string label, string? declared)
    {
        if (declared is null)
        {
            return;
        }

        fields[label] = declared;
        fields[label + "Present"] =
            File.Exists(Path.Combine(dataRoot, declared.Replace('\\', Path.DirectorySeparatorChar)));
    }

    private static bool IsModelled(string key)
    {
        return key.StartsWith("world_node_map", StringComparison.OrdinalIgnoreCase)
               || key.Equals("world_map", StringComparison.OrdinalIgnoreCase)
               || key.Equals("world_world", StringComparison.OrdinalIgnoreCase)
               || key.Equals("world_palette", StringComparison.OrdinalIgnoreCase)
               || key.Equals("world_sky", StringComparison.OrdinalIgnoreCase)
               || key.Equals("world_redbook", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The map's file stem names the world when it has one; otherwise its index does.</summary>
    private static string NameOf(RedguardWorld world)
    {
        return world.MapPath is { Length: > 0 } map
            ? Path.GetFileNameWithoutExtension(map).ToUpperInvariant()
            : "WORLD" + world.Index.ToString(CultureInfo.InvariantCulture);
    }
}
