namespace BethesdaMultitool.Core.Formats.VanBuren;

/// <summary>
///     One playable Van Buren level as the game assembles it: the <c>EMAP</c> placements plus the
///     two companions its header names — the <c>8TRE</c> scene (for the world-space frame) and the
///     run-length walk grid. Either companion may be absent; four shipped maps have no scene at
///     all and the level is then only its placements.
/// </summary>
/// <param name="Map">The placements.</param>
/// <param name="WalkGrid">The cell grid, when found.</param>
/// <param name="SceneEntryName">The archive entry the scene was read from, when found.</param>
/// <param name="WalkGridEntryName">The archive entry the grid was read from, when found.</param>
/// <param name="GridWidth">Cells across, from the scene's <c>LVLD</c> when present, else the grid's own.</param>
/// <param name="GridHeight">Cells down.</param>
/// <param name="CellSize">World units per cell; 0.5 on every shipped scene.</param>
/// <param name="Origin">The world-space corner of cell (0, 0) — the scene's root bounds minimum.</param>
/// <param name="Extent">The world-space far corner — the root bounds maximum.</param>
/// <param name="PairedBy">How the companions were found: <c>resource.rht</c>, the scene's texture names, or nothing.</param>
internal sealed record VanBurenMapLevel(
    VanBurenMapFile Map,
    VanBurenWalkGrid? WalkGrid,
    string? SceneEntryName,
    string? WalkGridEntryName,
    int GridWidth,
    int GridHeight,
    float CellSize,
    VanBurenVector3? Origin,
    VanBurenVector3? Extent,
    string PairedBy)
{
    /// <summary>The map's stem, e.g. <c>MarkTest</c>.</summary>
    public string Stem => Map.Header.Stem;

    /// <summary>Whether the level carries a world-space frame the placements can be drawn in.</summary>
    public bool HasFrame => Origin is not null && GridWidth > 0 && GridHeight > 0;
}

/// <summary>
///     Finds an <c>EMAP</c>'s companions among its archive's other entries.
///     <para>
///         ⚑ With <c>resource.rht</c> the pairing is the game's own: the header's <c>.8</c> stem
///         is the scene's indexed NAME under type 400 and the grid's under type 1700, in the same
///         group. Without it, a scene is identified by listing <c>&lt;stem&gt;_N.ctx</c> among its
///         textures (38 of 39 scenes carry their stem that way), and a grid by matching the
///         scene's <c>LVLD</c> width and height — the nearest such grid after the scene in the
///         directory, since the archives are written in load order. ⚠ The dimension rule is
///         weaker: two shipped grids share 84x84 and two share 644x644, so a scene-less map can
///         be given no grid at all rather than a guessed one.
///     </para>
/// </summary>
internal static class VanBurenMapCompanions
{
    /// <summary>
    ///     Resolves the companions of a parsed map.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="group">The archive's group name (its file stem), e.g. <c>Maps</c>.</param>
    /// <param name="candidates">Every entry in the archive.</param>
    /// <param name="read">Reads one entry's payload by name; may return null.</param>
    /// <param name="index">The build's resource index, when available.</param>
    public static VanBurenMapLevel Resolve(
        VanBurenMapFile map,
        string group,
        IReadOnlyList<Candidate> candidates,
        Func<string, byte[]?> read,
        VanBurenResourceIndex? index)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(read);

        var stem = map.Header.Stem;
        var byIndex = candidates.ToDictionary(c => c.Index);

        VanBurenSceneFile? scene = null;
        byte[]? sceneBytes = null;
        string? sceneName = null;
        string? gridName = null;
        VanBurenWalkGrid? grid = null;
        var pairedBy = "nothing";

        if (index is not null)
        {
            foreach (var entry in index.Find(group, stem, VanBurenResourceIndex.SceneType))
            {
                if (byIndex.TryGetValue(entry.Index, out var candidate) && read(candidate.Name) is { } bytes
                                                                        && VanBurenSceneFile.TryParse(bytes,
                                                                            candidate.Name, out var parsed, out _))
                {
                    scene = parsed;
                    sceneBytes = bytes;
                    sceneName = candidate.Name;
                    pairedBy = VanBurenResourceIndex.FileName;
                    break;
                }
            }

            foreach (var entry in index.Find(group, stem, VanBurenResourceIndex.WalkGridType))
            {
                if (byIndex.TryGetValue(entry.Index, out var candidate) && read(candidate.Name) is { } bytes
                                                                        && VanBurenWalkGrid.TryParse(bytes,
                                                                            candidate.Name, out var parsed, out _))
                {
                    grid = parsed;
                    gridName = candidate.Name;
                    pairedBy = VanBurenResourceIndex.FileName;
                    break;
                }
            }
        }

        if (scene is null)
        {
            // ⚠ Reads every scene in the archive until one lists the stem. The Maps archive holds
            // 39 of them totalling ~100 MB, so this is the fallback, not the first choice.
            var wanted = stem + "_";
            foreach (var candidate in candidates.Where(c => c.Tag == VanBurenSceneFile.Tag).OrderBy(c => c.Index))
            {
                if (read(candidate.Name) is not { } bytes ||
                    !VanBurenSceneFile.TryParse(bytes, candidate.Name, out var parsed, out _))
                {
                    continue;
                }

                var textures = VanBurenSceneFile.ReadTextureNames(bytes, parsed);
                if (textures.Any(t => t.EndsWith(".ctx", StringComparison.OrdinalIgnoreCase)
                                      && t.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)
                                      && t.Length > wanted.Length + 4
                                      && t[wanted.Length..^4].All(char.IsAsciiDigit)))
                {
                    scene = parsed;
                    sceneBytes = bytes;
                    sceneName = candidate.Name;
                    pairedBy = "scene texture names";
                    break;
                }
            }
        }

        var gridWidth = 0;
        var gridHeight = 0;
        var cell = 0.5f;
        VanBurenVector3? origin = null;
        VanBurenVector3? extent = null;
        if (scene is not null && sceneBytes is not null)
        {
            if (VanBurenSceneFile.TryReadLevelGrid(sceneBytes, scene, out var w, out var h, out var cx, out _))
            {
                gridWidth = w;
                gridHeight = h;
                cell = cx;
            }

            if (VanBurenSceneFile.TryReadBounds(sceneBytes, scene, out var minimum, out var maximum))
            {
                origin = minimum;
                extent = maximum;
                if (gridWidth == 0)
                {
                    // Two shipped scenes lack LVLD; their bounds still frame the placements.
                    gridWidth = Math.Max(1, (int)MathF.Round((maximum.X - minimum.X) / cell));
                    gridHeight = Math.Max(1, (int)MathF.Round((maximum.Z - minimum.Z) / cell));
                }
            }
        }

        if (grid is null && gridWidth > 0)
        {
            var sceneIndex = sceneName is null ? -1 : candidates.First(c => c.Name == sceneName).Index;
            foreach (var candidate in candidates
                         .Where(c => !IsTaggedFamily(c.Tag))
                         .OrderBy(c => c.Index < sceneIndex ? 1 : 0)
                         .ThenBy(c => c.Index))
            {
                if (read(candidate.Name) is not { } bytes ||
                    !VanBurenWalkGrid.TryParse(bytes, candidate.Name, out var parsed, out _))
                {
                    continue;
                }

                if (parsed.Width == gridWidth && parsed.Height == gridHeight)
                {
                    grid = parsed;
                    gridName = candidate.Name;
                    break;
                }
            }
        }

        if (grid is not null && gridWidth == 0)
        {
            gridWidth = grid.Width;
            gridHeight = grid.Height;
        }

        return new VanBurenMapLevel(map, grid, sceneName, gridName, gridWidth, gridHeight, cell, origin, extent,
            pairedBy);
    }

    /// <summary>
    ///     Whether an entry's tag places it in a family that is NOT a walk grid, so the fallback
    ///     search does not parse every mesh and texture in a 232 MB archive.
    /// </summary>
    private static bool IsTaggedFamily(string tag)
    {
        // The archive lists printable tag bytes as text and every other byte as a dot; a walk
        // grid's first dword is its width, whose high bytes are zero and so always show as dots.
        return tag.Length > 0 && !tag.Contains('.');
    }

    /// <summary>One archive entry offered to the resolver: its position, tag and a reader.</summary>
    /// <param name="Index">Position in the archive directory.</param>
    /// <param name="Name">The entry's name, e.g. <c>00002.8TRE</c>.</param>
    /// <param name="Tag">The payload's leading four bytes as the archive lists them.</param>
    internal readonly record struct Candidate(int Index, string Name, string Tag);
}
