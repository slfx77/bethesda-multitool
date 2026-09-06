namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     One placement from a zone's <c>.ent</c> file — the 72-byte record that puts an entity in the
///     world. 8,258 of them over the 21 retail zones (41 in ffarena up to 1,072 in raiders).
///     <para>
///         The record does NOT restate the model: <see cref="EntityId" /> indexes
///         <c>entities.txt</c>, whose model column indexes <c>models.txt</c> and the zone's own
///         <c>&lt;zone&gt;_models.txt</c> residency list. All 8,258 ids resolve, and every one of
///         them lands on a slot the zone actually loads. See <see cref="ShadowkeyTextTables" />.
///     </para>
///     <para>
///         <b>Traps this record carries.</b> Positions are 24.8 fixed point, so raw / 256 is a map
///         tile; 8,255 of 8,258 land inside the zone map, the three exceptions being editor
///         leftovers parked outside delfhide. The three angle slots are s32 whose values always
///         fit int16 — the unit is a hypothesis (see <see cref="Angle2" />). The two-byte slot
///         before the id is 0xCCCC in 8,258/8,258, MSVC debug stack fill in an alignment hole, and
///         is never data.
///     </para>
///     <para>
///         <see cref="Name" /> is a TRUNCATING 8-byte field: 41 records across dstar_e, dstar_w,
///         lakvan, raiders and twilite hold <c>Containe</c> with no terminator at all, the editor
///         having written "Container" into eight bytes. A reader must bound the field and must not
///         require a NUL. Both text fields are Latin-1, not ASCII — lakvan record 5 carries a 0xFF
///         byte inside its script text — and the bytes after a NUL are 0xCC fill or stale text from
///         an earlier record (azra record 2's script reads <c>bottle\0gate</c>, "gate" being the
///         tail of the previous record's "tradinggate"), so everything past the first NUL is
///         discarded.
///     </para>
/// </summary>
/// <param name="RawX">s32 at +0, 24.8 tile units.</param>
/// <param name="RawY">s32 at +4, 24.8 tile units.</param>
/// <param name="RawZ">s32 at +8, 24.8 height (retail spans -30.6 to 13.7 tiles).</param>
/// <param name="Angle0">s32 at +12; zero in about 95% of records.</param>
/// <param name="Angle1">s32 at +16; zero in about 95% of records.</param>
/// <param name="Angle2">
///     s32 at +20 — the dominant slot, non-zero in about 30% of records, clustering at plus or
///     minus 16384 and 32768. Hypothesis: a binary angle, 65536 per turn, which is what those
///     clusters mean (quarter and half turns); a "degrees times 256" reading would instead imply an
///     odd cap at plus or minus 128 degrees with no 180. Not settled here.
/// </param>
/// <param name="RawScale">
///     u16 at +24. 256 in 7,300 of 8,258 records and 32..7424 elsewhere; hypothesis: 8.8 fixed
///     point with 256 as unity.
/// </param>
/// <param name="EntityId">u32 at +28 — the <c>entities.txt</c> id, up to 6023 on retail.</param>
/// <param name="Name">
///     char[8] at +32, the instance name (<c>noname</c> is the default, <c>skelos2</c> and
///     <c>door12</c> are typical). Cut at the first NUL, or the full 8 bytes when there is none.
///     This is what <c>.stn</c> lock rows join against. Empty for a record read from a
///     <c>.sta</c> file, which stops before this field.
/// </param>
/// <param name="Script">
///     char[32] at +40 — the per-instance script or entity name (<c>gryphon</c>,
///     <c>monsters\Skelos_Undriel_Azra.s</c>). Usually the <c>entities.txt</c> name with its
///     leading <c>!</c> stripped; where it differs it is a per-instance override. Empty for a
///     <c>.sta</c> record.
/// </param>
internal sealed record ShadowkeyEntity(
    int RawX,
    int RawY,
    int RawZ,
    int Angle0,
    int Angle1,
    int Angle2,
    ushort RawScale,
    uint EntityId,
    string Name,
    string Script)
{
    /// <summary>X in whole-and-fractional map tiles.</summary>
    public float TileX => RawX / ShadowkeyZoneFiles.FixedPointScale;

    /// <summary>Y in whole-and-fractional map tiles.</summary>
    public float TileY => RawY / ShadowkeyZoneFiles.FixedPointScale;

    /// <summary>Height in whole-and-fractional map tiles.</summary>
    public float TileZ => RawZ / ShadowkeyZoneFiles.FixedPointScale;

    /// <summary>
    ///     <see cref="RawScale" /> read as the hypothesised 8.8 fixed point, 1.0 being unity.
    /// </summary>
    public float Scale => RawScale / ShadowkeyZoneFiles.FixedPointScale;
}
