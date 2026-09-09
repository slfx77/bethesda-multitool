using BethesdaMultitool.Core.Formats.Arena;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Classic;

/// <summary>
///     The Arena half of <c>classic level info|export</c>: a <c>.MIF</c> (loose, or an entry of
///     GLOBAL.BSA through <c>--entry</c>) or an <c>.RMD</c> wilderness chunk, assembled by
///     <see cref="ArenaSceneAssembler" /> into one textured GLB per level.
///     <para>
///         The level's art resolves against the install the file sits in — the data directory
///         holding GLOBAL.BSA and PAL.COL — so a .MIF copied elsewhere on its own exports
///         untextured, the same rule the Battlespire and Daggerfall exports follow.
///     </para>
/// </summary>
internal static class ArenaLevelCommandSupport
{
    /// <summary>The four bytes every .MIF opens with.</summary>
    private static ReadOnlySpan<byte> MifTag => "MHDR"u8;

    /// <summary>True when the input (or the named archive entry) is an Arena level this can handle.</summary>
    public static bool IsArenaLevel(string input, string? entryName)
    {
        ArgumentNullException.ThrowIfNull(input);

        var name = entryName ?? input;
        if (name.EndsWith(".RMD", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.EndsWith(".MIF", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (entryName is null && File.Exists(input))
        {
            Span<byte> head = stackalloc byte[4];
            using var stream = File.OpenRead(input);
            return stream.Read(head) == 4 && head.SequenceEqual(MifTag);
        }

        return false;
    }

    /// <summary>Prints the level's voxel census, its resolved .INF and what the assembly would build.</summary>
    public static void Info(string input, string? entryName, string? infOverride)
    {
        var (bytes, name) = Load(input, entryName);
        using var library = OpenLibrary(input);
        var levels = ParseLevels(bytes, name, out var map);

        AnsiConsole.MarkupLine(
            "[bold cyan]{0}[/] — Arena {1}, {2}x{3} voxels, {4} level(s){5}",
            Markup.Escape(name),
            map is null ? "wilderness chunk" : "map",
            levels[0].Planes.Width, levels[0].Planes.Depth, levels.Count,
            library is null ? " [yellow](no install beside it — textures unresolved)[/]" : string.Empty);

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Level");
        table.AddColumn("Kind");
        table.AddColumn(".INF");
        table.AddColumn("Slots");
        table.AddColumn("Wall h");
        table.AddColumn("Walls");
        table.AddColumn("Raised");
        table.AddColumn("Doors");
        table.AddColumn("Diag");
        table.AddColumn("Edges");
        table.AddColumn("T-walls");
        table.AddColumn("Flats");
        table.AddColumn("Chasms");
        table.AddColumn("Storeys");
        table.AddColumn("Quads");
        table.AddColumn("Clamped");

        foreach (var level in levels)
        {
            var textures = library?.ResolveTextures(level.InfoName, level.Kind, infOverride, out _);
            var resolvedName = textures?.Name ?? "—";
            var assembly = ArenaSceneAssembler.Assemble(level.Planes, textures ?? EmptyTextures(), level.Kind, level.Label);
            var census = assembly.Census;
            table.AddRow(
                Markup.Escape(level.Label),
                level.Kind.ToString(),
                Markup.Escape(resolvedName),
                textures is null ? "—" : textures.Slots.Count.ToString(),
                textures is null ? "—" : textures.Ceiling.Scale.ToString("0.00"),
                census.Walls.ToString(),
                census.RaisedPlatforms.ToString(),
                census.Doors.ToString(),
                census.Diagonals.ToString(),
                census.Edges.ToString(),
                census.TransparentWalls.ToString(),
                census.Flats.ToString(),
                $"{census.DryChasms}/{census.WetChasms}/{census.LavaChasms}",
                census.UpperStoreys == 0 ? "—" : $"{census.UpperStoreys} (max {census.MaxStoreys})",
                census.Quads.ToString(),
                census.ClampedTextureIds.ToString());
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            "[grey]Chasms are dry/wet/lava FLOR voxels; Clamped counts texture ids the game wraps modulo 64 or falls back to slot 0 for. Flats are sprites and are not built.[/]");
    }

    /// <summary>Writes one GLB per level (or only <paramref name="levelIndex" />).</summary>
    public static void Export(string input, string? entryName, string outputDir, int? levelIndex, string? infOverride)
    {
        var (bytes, name) = Load(input, entryName);
        using var library = OpenLibrary(input);
        var levels = ParseLevels(bytes, name, out _);
        if (levelIndex is { } wanted && (wanted < 0 || wanted >= levels.Count))
        {
            throw new InvalidOperationException($"{name} has {levels.Count} level(s); --level {wanted} is out of range.");
        }

        Directory.CreateDirectory(outputDir);
        var stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();

        foreach (var level in levels.Where((_, i) => levelIndex is null || i == levelIndex))
        {
            var textures = library?.ResolveTextures(level.InfoName, level.Kind, infOverride, out _);
            var assembly = ArenaSceneAssembler.Assemble(level.Planes, textures ?? EmptyTextures(), level.Kind, level.Label);
            if (assembly.Instances.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]{0} built no geometry — skipped.[/]", Markup.Escape(level.Label));
                continue;
            }

            var outputPath = Path.Combine(outputDir, levels.Count == 1 ? stem + ".glb" : $"{stem}_L{level.Index:D2}.glb");
            var textured = 0;
            var provider = library is not null && textures is not null ? library.TextureProviderFor(textures) : null;
            XnGineMeshGlbExporter.WriteScene(level.Label, assembly.Instances, outputPath, (archive, record) =>
            {
                var png = provider?.Invoke(archive, record);
                if (png is not null)
                {
                    textured++;
                }

                return png;
            });

            var census = assembly.Census;
            AnsiConsole.MarkupLine(
                "[green]Wrote[/] {0} [grey]({1}, .INF {2}; {3} walls, {4} raised, {5} doors, {6} diagonals, {7} edges, {8} storeys, {9} quads; {10} of {11} materials textured{12})[/]",
                Markup.Escape(outputPath), level.Kind, Markup.Escape(textures?.Name ?? "none"),
                census.Walls, census.RaisedPlatforms, census.Doors, census.Diagonals, census.Edges, census.UpperStoreys,
                census.Quads, textured, assembly.Mesh.SubMeshes.Select(s => s.TextureRecord).Distinct().Count(),
                census.ClampedTextureIds > 0 ? $"; {census.ClampedTextureIds} clamped texture ids" : string.Empty);
        }
    }

    /// <summary>One level ready to assemble: its planes, world kind, .INF name and label.</summary>
    private sealed record LevelInput(int Index, string Label, ArenaLevelPlanes Planes, ArenaMapKind Kind, string? InfoName);

    private static (byte[] Bytes, string Name) Load(string input, string? entryName)
    {
        if (!File.Exists(input))
        {
            throw new FileNotFoundException($"Input not found: {input}", input);
        }

        if (entryName is null)
        {
            return (File.ReadAllBytes(input), Path.GetFileName(input));
        }

        using var archive = ArchiveReader.Open(input);
        var entry = archive.ListFiles()
                        .FirstOrDefault(e => e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"No entry named '{entryName}' in {Path.GetFileName(input)}.");
        return (archive.ReadFile(entry.FullPath)!, entry.Name);
    }

    private static ArenaLevelLibrary? OpenLibrary(string input)
    {
        var root = ArenaLevelLibrary.FindDataRoot(input);
        return root is null ? null : ArenaLevelLibrary.Open(root);
    }

    private static List<LevelInput> ParseLevels(byte[] bytes, string name, out ArenaMifFile? map)
    {
        map = null;
        if (name.EndsWith(".RMD", StringComparison.OrdinalIgnoreCase))
        {
            var chunk = ArenaRmdFile.Parse(bytes, name);
            var stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
            return [new LevelInput(0, stem, ArenaLevelPlanes.FromRmd(chunk), ArenaMapKind.Wilderness, null)];
        }

        map = ArenaMifFile.Parse(bytes, name);
        if (map.Levels.Count == 0)
        {
            throw new InvalidOperationException($"{name} holds no LEVL block.");
        }

        var mapStem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        var singleLevel = map.Levels.Count == 1;
        return map.Levels
            .Select((level, i) => new LevelInput(
                i,
                singleLevel ? mapStem : $"{mapStem}_L{i:D2}",
                ArenaLevelPlanes.FromMifLevel(level),
                ArenaLevelLibrary.KindOf(level),
                level.InfoFile))
            .ToList();
    }

    /// <summary>A texture index with no slots, for a level whose install cannot be found.</summary>
    private static ArenaInfVoxelTextures EmptyTextures()
    {
        return ArenaInfVoxelTextures.FromInf(ArenaInfFile.ParseText(string.Empty, "NONE.INF"));
    }
}
