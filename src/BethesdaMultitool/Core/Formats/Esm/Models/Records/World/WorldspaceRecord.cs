namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Parsed Worldspace record.
/// </summary>
public record WorldspaceRecord
{
    /// <summary>
    ///     Synthetic FormID shared by every Morrowind (TES3) plugin's exterior worldspace. TES3 has no
    ///     WRLD records, so <see cref="BethesdaMultitool.Core.Formats.Tes3.Tes3RecordParser" /> synthesizes
    ///     one exterior worldspace per file; using a single well-known constant (rather than a per-file
    ///     value) lets the load-order merge fold every plugin's exterior cells into one worldspace
    ///     (Vvardenfell + Bloodmoon's Solstheim, etc.). It lives in the 0xFF-prefixed "shared" range that
    ///     <c>Tes3FormIdScheme.Namespace</c> leaves untouched when it namespaces per-plugin record IDs.
    /// </summary>
    public const uint Tes3SyntheticExteriorFormId = 0xFF000000u;

    /// <summary>FormID of the WRLD record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID (e.g., "WastelandNV").</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Parent worldspace FormID (if any).</summary>
    public uint? ParentWorldspaceFormId { get; init; }

    /// <summary>Climate FormID (CNAM subrecord).</summary>
    public uint? ClimateFormId { get; init; }

    /// <summary>Water FormID (NAM2 subrecord).</summary>
    public uint? WaterFormId { get; init; }

    /// <summary>
    ///     Starfield's authored WRLD water-material string (NAM7 subrecord). This is distinct from
    ///     <see cref="WaterFormId" />: NAM7 is not a WATR FormID. An empty string is retained as an
    ///     authored value; <see langword="null" /> means the subrecord was absent.
    /// </summary>
    public string? StarfieldWaterMaterial { get; init; }

    /// <summary>Default land height from DNAM subrecord.</summary>
    public float? DefaultLandHeight { get; init; }

    /// <summary>
    ///     Default water height from the WRLD DNAM subrecord. Exterior cells with sentinel XCLW fall
    ///     back to this value; a sentinel here means the worldspace has no authored default water plane.
    /// </summary>
    public float? DefaultWaterHeight { get; init; }

    /// <summary>
    ///     True when <see cref="DefaultWaterHeight" />/<see cref="WaterFormId" /> were inherited from the
    ///     WNAM parent chain rather than authored on this record. TES4 child worldspaces (BravilWorld
    ///     etc.) carry no NAM2/DNAM of their own — the engine inherits water from the parent, but only
    ///     cells flagging Has-Water actually get a plane, so resolvers must gate an inherited default on
    ///     <see cref="CellRecord.HasWater" /> (an authored default keeps the legacy every-cell fallback).
    /// </summary>
    public bool WaterFromParentWorldspace { get; init; }

    /// <summary>Map usable width from MNAM subrecord (WORLD_MAP_DATA).</summary>
    public int? MapUsableWidth { get; init; }

    /// <summary>Map usable height from MNAM subrecord (WORLD_MAP_DATA).</summary>
    public int? MapUsableHeight { get; init; }

    /// <summary>Map NW corner cell X from MNAM subrecord.</summary>
    public short? MapNWCellX { get; init; }

    /// <summary>Map NW corner cell Y from MNAM subrecord.</summary>
    public short? MapNWCellY { get; init; }

    /// <summary>Map SE corner cell X from MNAM subrecord.</summary>
    public short? MapSECellX { get; init; }

    /// <summary>Map SE corner cell Y from MNAM subrecord.</summary>
    public short? MapSECellY { get; init; }

    /// <summary>World bounds minimum X from NAM0 subrecord.</summary>
    public float? BoundsMinX { get; init; }

    /// <summary>World bounds minimum Y from NAM0 subrecord.</summary>
    public float? BoundsMinY { get; init; }

    /// <summary>World bounds maximum X from NAM9 subrecord.</summary>
    public float? BoundsMaxX { get; init; }

    /// <summary>World bounds maximum Y from NAM9 subrecord.</summary>
    public float? BoundsMaxY { get; init; }

    /// <summary>Encounter zone FormID (XEZN subrecord).</summary>
    public uint? EncounterZoneFormId { get; init; }

    /// <summary>Worldspace flags from DATA subrecord / cFlags byte (small world, can't fast travel, etc.).</summary>
    public byte? Flags { get; init; }

    /// <summary>Parent use flags from PNAM subrecord / sParentUseFlags (which parent fields to inherit).</summary>
    public ushort? ParentUseFlags { get; init; }

    /// <summary>Image space FormID (INAM subrecord / pImageSpace pointer).</summary>
    public uint? ImageSpaceFormId { get; init; }

    /// <summary>Music type FormID (ZNAM subrecord / pMusicType pointer).</summary>
    public uint? MusicTypeFormId { get; init; }

    /// <summary>Map offset X scale from ONAM subrecord / WorldMapOffsetData.</summary>
    public float? MapOffsetScaleX { get; init; }

    /// <summary>Map offset Y scale from ONAM subrecord / WorldMapOffsetData.</summary>
    public float? MapOffsetScaleY { get; init; }

    /// <summary>Map offset Z from ONAM subrecord / WorldMapOffsetData.</summary>
    public float? MapOffsetZ { get; init; }

    /// <summary>Cells belonging to this worldspace.</summary>
    public List<CellRecord> Cells { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>
    ///     Returns a copy whose <see cref="MapNWCellX" />/<see cref="MapNWCellY" />/<see cref="MapSECellX" />/
    ///     <see cref="MapSECellY" /> corners are recomputed to span <paramref name="exteriorCells" />.
    ///     Morrowind's map has +Y north, so NW = (min X, max Y) and SE = (max X, min Y). Used both when the
    ///     TES3 parser synthesizes the exterior worldspace and after a load-order merge, where the merged
    ///     worldspace would otherwise keep only the last plugin's bounds and clip the others' cells off the
    ///     map (e.g. Bloodmoon's Solstheim dropping Vvardenfell, or vice-versa).
    /// </summary>
    public WorldspaceRecord WithMorrowindExteriorBounds(IEnumerable<CellRecord> exteriorCells)
    {
        short? nwX = null, nwY = null, seX = null, seY = null;
        foreach (var c in exteriorCells)
        {
            if (c.GridX is not { } gx || c.GridY is not { } gy)
            {
                continue;
            }

            nwX = (short)Math.Min(nwX ?? gx, gx);
            seX = (short)Math.Max(seX ?? gx, gx);
            nwY = (short)Math.Max(nwY ?? gy, gy);
            seY = (short)Math.Min(seY ?? gy, gy);
        }

        return this with { MapNWCellX = nwX, MapNWCellY = nwY, MapSECellX = seX, MapSECellY = seY };
    }
}
