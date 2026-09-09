// Ported from OpenTESArena (MIT License), https://github.com/afritz1/OpenTESArena
//   OpenTESArena/src/Assets/INFFile.cpp — the voxelTextures index model (@FLOORS then @WALLS in
//   discovery order, .SET lines expanded to one slot per tile), the boxCaps/boxSides/menus tables,
//   the *DRYCHASM/*WETCHASM/*LAVACHASM/*LEVELUP/*LEVELDOWN indices and the *WETCHASM fallback
//   (*BOXCAP 6 + 1); and src/Voxels/ArenaVoxelUtils.cpp clampVoxelTextureID (ids wrap modulo 64).
//   License texts are collected centrally in THIRD_PARTY_LICENSES.

namespace BethesdaMultitool.Core.Formats.Arena;

/// <summary>One slot of the voxel-texture index: an image file and, for a <c>.SET</c>, which tile.</summary>
internal readonly record struct ArenaVoxelTextureSlot(string FileName, int? SetIndex)
{
    /// <summary>A label that names the tile as well as the file, for materials and reports.</summary>
    public string Label => SetIndex is { } tile ? $"{FileName}#{tile}" : FileName;
}

/// <summary>
///     The <c>*CEILING</c> data a level is rendered with: wall height in Arena units (128 per
///     voxel), the raised-platform box scale, the outdoor flag, and the index of the texture the
///     directive was authored against (null when the file never declares one).
/// </summary>
internal sealed record ArenaVoxelCeiling(int Height, int? BoxScale, bool OutdoorDungeon, int? TextureIndex)
{
    /// <summary>The height the game assumes when an .INF declares no <c>*CEILING</c>.</summary>
    public const int DefaultHeight = 100;

    /// <summary>Wall height in voxel units — one voxel is 128 Arena units wide.</summary>
    public float Scale => Height / ArenaSceneAssembler.ArenaUnitsPerVoxel;
}

/// <summary>
///     The voxel-texture INDEX a level's ids resolve through, rebuilt from a parsed
///     <see cref="ArenaInfFile" /> the way the game's own loader lays it out.
///     <para>
///         ⚑ The index is ONE list: every <c>@FLOORS</c> entry, then every <c>@WALLS</c> entry, in
///         file order, and a <c>name.SET #N</c> line occupies N consecutive slots (tile 0..N-1). A
///         FLOR id, a MAP1 wall id and a MAP2 id are all positions in that list — the sections do
///         not have separate numbering. Measured over the 93 retail .INF files (2026-09-08):
///         <c>@FLOORS</c> precedes <c>@WALLS</c> in 91, and the two that open with a bare
///         <c>*BOXCAP</c> (DAGOTH1/2) are parsed as floors first by the game too, so "floors then
///         walls" and "file order" are the same rule on every retail file; list lengths run 42..62
///         (59 on 37 files).
///     </para>
///     <para>
///         ⚠ An id at or past 64 wraps modulo 64 before lookup (the game's <c>TOTAL_VOXEL_IDS</c>),
///         and an id past the end of the list falls back to slot 0. Both happen in retail data:
///         <c>40852494.MIF</c> carries 135 diagonal walls whose texture byte is 0xC4 against a
///         42-entry ELDEN1.INF, and 3,464 FLOR voxels carry bit 15 in their texture byte. Callers
///         that want to count those go through <see cref="TryResolve" />.
///     </para>
/// </summary>
internal sealed class ArenaInfVoxelTextures
{
    /// <summary>The game's voxel-id range; ids at or above it wrap.</summary>
    public const int TotalVoxelIds = 64;

    /// <summary>Slots the <c>*BOXCAP</c>/<c>*BOXSIDE</c>/<c>*MENU</c> tables have.</summary>
    public const int DirectiveTableSize = 16;

    private readonly int?[] _boxCaps = new int?[DirectiveTableSize];
    private readonly int?[] _boxSides = new int?[DirectiveTableSize];
    private readonly int?[] _menus = new int?[DirectiveTableSize];
    private readonly List<ArenaVoxelTextureSlot> _slots = [];

    private ArenaInfVoxelTextures(string name)
    {
        Name = name;
        Ceiling = new ArenaVoxelCeiling(ArenaVoxelCeiling.DefaultHeight, null, false, null);
    }

    /// <summary>The .INF this index was built from.</summary>
    public string Name { get; }

    /// <summary>The index, in slot order.</summary>
    public IReadOnlyList<ArenaVoxelTextureSlot> Slots => _slots;

    /// <summary>Slot the <c>*DRYCHASM</c> directive names, if any.</summary>
    public int? DryChasmIndex { get; private set; }

    /// <summary>
    ///     Slot the <c>*WETCHASM</c> directive names — or, when the file has none, one past its
    ///     <c>*BOXCAP 6</c> texture, the fallback the reference observes some interiors relying on.
    /// </summary>
    public int? WetChasmIndex { get; private set; }

    /// <summary>Slot the <c>*LAVACHASM</c> directive names, if any.</summary>
    public int? LavaChasmIndex { get; private set; }

    /// <summary>Slot the <c>*LEVELUP</c> directive names, if any.</summary>
    public int? LevelUpIndex { get; private set; }

    /// <summary>Slot the <c>*LEVELDOWN</c> directive names, if any.</summary>
    public int? LevelDownIndex { get; private set; }

    /// <summary>True when the wet-chasm slot came from the <c>*BOXCAP 6</c> fallback.</summary>
    public bool WetChasmIsFallback { get; private set; }

    /// <summary>The level's ceiling data (defaults applied).</summary>
    public ArenaVoxelCeiling Ceiling { get; private set; }

    /// <summary>Builds the index from a parsed .INF.</summary>
    public static ArenaInfVoxelTextures FromInf(ArenaInfFile inf)
    {
        ArgumentNullException.ThrowIfNull(inf);

        var result = new ArenaInfVoxelTextures(inf.Name);
        foreach (var floor in inf.Floors)
        {
            var first = result.Append(floor);
            foreach (var cap in floor.BoxCapIds)
            {
                Set(result._boxCaps, cap, first);
            }

            if (floor.Ceiling is { } ceiling && result.Ceiling.TextureIndex is null)
            {
                result.Ceiling = new ArenaVoxelCeiling(
                    ceiling.Height ?? ArenaVoxelCeiling.DefaultHeight,
                    ceiling.BoxScale,
                    ceiling.OutdoorDungeon,
                    first);
            }
        }

        foreach (var wall in inf.Walls)
        {
            var first = result.Append(wall);
            foreach (var cap in wall.BoxCapIds)
            {
                Set(result._boxCaps, cap, first);
            }

            foreach (var side in wall.BoxSideIds)
            {
                Set(result._boxSides, side, first);
            }

            if (wall.MenuId is { } menu)
            {
                Set(result._menus, menu, first);
            }

            if (wall.Flags.HasFlag(ArenaInfVoxelFlags.DryChasm))
            {
                result.DryChasmIndex = first;
            }

            if (wall.Flags.HasFlag(ArenaInfVoxelFlags.WetChasm))
            {
                result.WetChasmIndex = first;
            }

            if (wall.Flags.HasFlag(ArenaInfVoxelFlags.LavaChasm))
            {
                result.LavaChasmIndex = first;
            }

            if (wall.Flags.HasFlag(ArenaInfVoxelFlags.LevelUp))
            {
                result.LevelUpIndex = first;
            }

            if (wall.Flags.HasFlag(ArenaInfVoxelFlags.LevelDown))
            {
                result.LevelDownIndex = first;
            }
        }

        if (result.WetChasmIndex is null && result._boxCaps[6] is { } boxCap6)
        {
            result.WetChasmIndex = boxCap6 + 1;
            result.WetChasmIsFallback = true;
        }

        return result;
    }

    /// <summary>Slot a <c>*BOXCAP n</c> texture occupies, or null when the file declares none.</summary>
    public int? BoxCap(int id)
    {
        return (uint)id < DirectiveTableSize ? _boxCaps[id] : null;
    }

    /// <summary>
    ///     Slot a <c>*BOXSIDE n</c> texture occupies. Falls back to <c>*BOXSIDE 0</c> when the
    ///     requested id is undeclared, as the reference does — NOBLE1.INF's first level asks for
    ///     side 14, which the file never defines.
    /// </summary>
    public int? BoxSide(int id)
    {
        if ((uint)id < DirectiveTableSize && _boxSides[id] is { } slot)
        {
            return slot;
        }

        return _boxSides[0];
    }

    /// <summary>Slot a <c>*MENU n</c> texture occupies, or null.</summary>
    public int? Menu(int id)
    {
        return (uint)id < DirectiveTableSize ? _menus[id] : null;
    }

    /// <summary>
    ///     Resolves a voxel texture id to its slot the way the game does: wrap at 64, then fall
    ///     back to slot 0 past the end. <paramref name="clamped" /> reports that either happened,
    ///     so a caller can count it rather than let a wrong id pass as a right one.
    /// </summary>
    public ArenaVoxelTextureSlot? TryResolve(int id, out bool clamped)
    {
        clamped = false;
        if (_slots.Count == 0)
        {
            return null;
        }

        if (id >= TotalVoxelIds)
        {
            id %= TotalVoxelIds;
            clamped = true;
        }

        if (id < 0 || id >= _slots.Count)
        {
            id = 0;
            clamped = true;
        }

        return _slots[id];
    }

    /// <summary>Resolves to the wrapped-and-bounded slot INDEX rather than the slot.</summary>
    public int ResolveIndex(int id, out bool clamped)
    {
        clamped = false;
        if (id >= TotalVoxelIds)
        {
            id %= TotalVoxelIds;
            clamped = true;
        }

        if (id < 0 || id >= _slots.Count)
        {
            clamped = true;
            return 0;
        }

        return id;
    }

    private int Append(ArenaInfVoxelTexture texture)
    {
        var first = _slots.Count;
        var name = texture.FileName.ToUpperInvariant();
        if (texture.SetSize is { } setSize && setSize > 0)
        {
            for (var i = 0; i < setSize; i++)
            {
                _slots.Add(new ArenaVoxelTextureSlot(name, i));
            }
        }
        else
        {
            _slots.Add(new ArenaVoxelTextureSlot(name, null));
        }

        return first;
    }

    private static void Set(int?[] table, int id, int slot)
    {
        if ((uint)id < (uint)table.Length)
        {
            table[id] = slot;
        }
    }
}
