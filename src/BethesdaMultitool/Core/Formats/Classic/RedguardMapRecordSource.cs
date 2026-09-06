using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Redguard;

namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>
///     Synthesizes Redguard's map records from <c>maps\*.RGM</c>: one <c>RMAP</c> per map
///     database and one <c>ROBJ</c> per script object inside it. A map's identity is its file
///     stem — WORLD.INI can register a map several times (ISLAND three times) or not at all
///     (HIDEOUT), so the world index cannot be the key — hashed through <see cref="ClassicNameHash" />;
///     an object's is that plus its position in the map's own object table, which IS stable
///     (RAHD order is the script compile order and the first 24 slots are fixed engine objects).
///     <para>
///         Placements are folded into the object that owns them rather than emitted as a third
///         family: every MPOB name is an object label, so a 3,147-record placement family would
///         only restate the object list with coordinates.
///     </para>
/// </summary>
internal static class RedguardMapRecordSource
{
    /// <summary>Domain byte for <c>RMAP</c> records; the index is the 24-bit name hash of the map stem.</summary>
    public const byte MapDomain = 0x31;

    /// <summary>Domain byte for <c>ROBJ</c> records; the index is a 15-bit map hash above a 9-bit object index.</summary>
    public const byte ObjectDomain = 0x32;

    public const string MapRecordType = "RMAP";
    public const string ObjectRecordType = "ROBJ";

    private const int MapHashBits = 24;
    private const int ObjectMapHashBits = 15;
    private const int ObjectIndexBits = 9;

    /// <summary>Reads every <c>maps\*.RGM</c> under <paramref name="dataRoot" /> and appends its records.</summary>
    public static void Populate(string dataRoot, RecordCollection records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);
        ArgumentNullException.ThrowIfNull(records);

        var mapsDirectory = Path.Combine(dataRoot, "maps");
        if (!Directory.Exists(mapsDirectory))
        {
            return;
        }

        var registry = File.Exists(Path.Combine(dataRoot, RedguardWorldIni.FileName))
            ? RedguardWorldIni.Load(dataRoot)
            : null;

        var seen = new Dictionary<uint, string>();
        foreach (var path in Directory.EnumerateFiles(mapsDirectory, "*.rgm").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stem = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
            var map = RedguardRgmFile.Parse(File.ReadAllBytes(path), Path.GetFileName(path));
            var robSegments = LoadRobSegments(dataRoot, stem);

            foreach (var record in Build(stem, map, registry, robSegments))
            {
                if (!seen.TryAdd(record.FormId, record.EditorId ?? stem))
                {
                    throw new InvalidDataException(
                        $"Redguard map records: '{record.EditorId}' hashes to the same FormID as '{seen[record.FormId]}' (0x{record.FormId:X8}); the name hash needs widening.");
                }

                records.GenericRecords.Add(record);
            }
        }
    }

    /// <summary>The records for one parsed map: its <c>RMAP</c> first, then an <c>ROBJ</c> per object.</summary>
    public static IEnumerable<GenericEsmRecord> Build(
        string stem, RedguardRgmFile map, RedguardWorldIni? registry, IReadOnlySet<string>? robSegments)
    {
        ArgumentNullException.ThrowIfNull(stem);
        ArgumentNullException.ThrowIfNull(map);
        return BuildCore(stem, map, registry, robSegments);
    }

    private static IEnumerable<GenericEsmRecord> BuildCore(
        string stem, RedguardRgmFile map, RedguardWorldIni? registry, IReadOnlySet<string>? robSegments)
    {
        var placementsByObject = map.Placements
            .GroupBy(p => p.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        yield return BuildMapRecord(stem, map, registry, robSegments);

        foreach (var obj in map.Objects)
        {
            placementsByObject.TryGetValue(obj.Label, out var placements);
            yield return BuildObjectRecord(stem, map, obj, placements ?? [], robSegments);
        }
    }

    private static GenericEsmRecord BuildMapRecord(string stem, RedguardRgmFile map, RedguardWorldIni? registry, IReadOnlySet<string>? robSegments)
    {
        var worlds = registry?.Worlds
            .Where(w => w.MapPath is { } mapPath &&
                        Path.GetFileNameWithoutExtension(mapPath).Equals(stem, StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Index)
            .ToList() ?? [];

        var entries = map.Placements.Where(p => p.IsEntryOrExit).Select(p => p.ObjectName).ToList();
        var placedMeshes = map.Placements.Where(p => p.HasMesh).Select(p => p.MeshStem).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var staticNames = map.StaticMeshes.Select(s => s.MeshName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["File"] = $@"maps\{stem}.RGM",
            ["Worlds"] = worlds.Count == 0 ? "(unregistered)" : string.Join(", ", worlds),
            ["CompileWord"] = $"0x{map.CompileWord:X}",
            ["Objects"] = map.Objects.Count,
            ["MapObjects"] = map.Objects.Count - map.Objects.Count(o => o.IsFixed),
            ["Placements"] = map.Placements.Count,
            ["PlacementsWithMesh"] = map.Placements.Count(p => p.HasMesh),
            ["EntriesAndExits"] = entries.Count == 0 ? "(none)" : string.Join(", ", entries),
            ["StaticMeshes"] = map.StaticMeshes.Count,
            ["DistinctStaticMeshes"] = staticNames.Count,
            ["Lights"] = map.Lights.Count,
            ["Flats"] = map.Flats.Count,
            ["Markers"] = map.Markers.Count,
            ["MarkersUsed"] = map.Markers.Count(m => m.IsUsed),
            ["Ropes"] = map.Ropes.Count,
            ["MeshSizeRows"] = map.MeshSizes.Count,
            ["AnimationMeshes"] = map.AnimationMeshes.Count,
            ["NavigationMaps"] = map.NavigationMaps.Count,
            ["NavigationNodes"] = map.NavigationMaps.Sum(n => n.Nodes.Count),
            ["NavigationRoutes"] = map.NavigationMaps.Sum(n => n.Nodes.Sum(node => node.Routes.Count)),
            ["ScriptBytes"] = map.ChunkLength("RASC"),
            ["Strings"] = map.Strings.Count
        };

        if (robSegments is not null)
        {
            fields["RobArchive"] = $@"3dart\{stem}.ROB";
            fields["StaticMeshesInRob"] = map.StaticMeshes.Count(s => robSegments.Contains(s.MeshName));
            fields["PlacedMeshesInRob"] = placedMeshes.Count(robSegments.Contains);
        }

        AddBounds(fields, map);

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(MapDomain, ClassicNameHash.Of(stem, MapHashBits)),
            RecordType = MapRecordType,
            EditorId = ClassicRecordNaming.ToEditorId(stem),
            FullName = null,
            Fields = fields
        };
    }

    private static GenericEsmRecord BuildObjectRecord(
        string stem, RedguardRgmFile map, RedguardRgmObject obj, List<RedguardRgmPlacement> placements, IReadOnlySet<string>? robSegments)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Map"] = stem,
            ["Index"] = obj.Index,
            ["Label"] = obj.Label,
            ["IsFixed"] = obj.IsFixed,
            ["IsActor"] = obj.IsActor,
            ["Instances"] = obj.InstanceCount,
            ["ScriptVariables"] = obj.VariablesPerInstance,
            ["ScriptBytes"] = obj.ScriptLength,
            ["HookBytes"] = obj.HookLength,
            ["LocalVectors"] = obj.LocalVectorCount,
            ["AnimationMeshes"] = obj.AnimationMeshCount,
            ["Frames"] = obj.FrameCount,
            ["Placements"] = placements.Count
        };

        if (obj.ScriptName.Length > 0)
        {
            fields["ScriptName"] = obj.ScriptName;
        }

        if (obj.CharacterId != 0)
        {
            fields["CharacterId"] = obj.CharacterId;
        }

        if (obj.StringReferences.Count > 0)
        {
            fields["StringReferences"] = string.Join(", ", obj.StringReferences);
        }

        if (obj.CollisionSphereCount > 0)
        {
            fields["CollisionSpheres"] = obj.CollisionSphereCount;
        }

        var meshes = map.AnimationMeshes.Where(a => a.ObjectIndex == obj.Index).Select(a => Path.GetFileName(a.Path)).ToList();
        if (meshes.Count > 0)
        {
            fields["AnimationMeshFiles"] = string.Join(", ", meshes);
        }

        if (placements.Count > 0)
        {
            var first = placements[0];
            fields["PlacementType"] = first.Type;
            var (x, y, z) = first.Position.WorldUnits;
            fields["Position"] = $"{x}, {y}, {z}";
            if (first.HasMesh)
            {
                fields["Mesh"] = first.MeshStem;
                if (robSegments is not null)
                {
                    fields["MeshInRob"] = robSegments.Contains(first.MeshStem);
                }
            }

            if (first.Type == 6)
            {
                fields["WorldIndex"] = first.WorldIndex;
            }
        }

        var index = (ClassicNameHash.Of(stem, ObjectMapHashBits) << ObjectIndexBits) | (uint)obj.Index;
        if (obj.Index >= 1 << ObjectIndexBits)
        {
            throw new InvalidDataException($"{stem}: object index {obj.Index} exceeds the {1 << ObjectIndexBits}-slot FormID budget.");
        }

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(ObjectDomain, index),
            RecordType = ObjectRecordType,
            EditorId = ClassicRecordNaming.ToEditorId(stem + "_" + obj.Label),
            FullName = obj.ScriptName.Length > 0 ? obj.ScriptName : null,
            Fields = fields
        };
    }

    private static void AddBounds(Dictionary<string, object?> fields, RedguardRgmFile map)
    {
        var positions = map.Placements.Select(p => p.Position)
            .Concat(map.StaticMeshes.Select(s => s.Position))
            .ToList();
        if (positions.Count == 0)
        {
            return;
        }

        var min = (positions.Min(p => p.X), positions.Min(p => p.Y), positions.Min(p => p.Z));
        var max = (positions.Max(p => p.X), positions.Max(p => p.Y), positions.Max(p => p.Z));
        const int scale = RedguardRgmFile.UnitsPerWorldUnit;
        fields["BoundsMin"] = $"{min.Item1 / scale}, {min.Item2 / scale}, {min.Item3 / scale}";
        fields["BoundsMax"] = $"{max.Item1 / scale}, {max.Item2 / scale}, {max.Item3 / scale}";
    }

    /// <summary>The segment names of the map's own ROB, or null when the install has no such archive.</summary>
    private static HashSet<string>? LoadRobSegments(string dataRoot, string stem)
    {
        var path = Path.Combine(dataRoot, "3dart", stem + ".ROB");
        if (!File.Exists(path))
        {
            return null;
        }

        var archive = RedguardRobParser.Parse(path);
        return archive.Entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
