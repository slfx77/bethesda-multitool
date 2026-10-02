using System.Globalization;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Core.Games;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Shadowkey;

/// <summary>
///     The units, bases and placement matrix of the Shadowkey documents (cut-2 plan
///     <c>docs/design/cut2-shadowkey-reader-plan-20260928.md</c>, section 5.3 and decision D7; design section 4.1, row
///     "Shadowkey meshes and zones"). Owned here rather than in <c>Core/Modeling/Units/ClassicModelUnits.cs</c> so this
///     track does not touch a cut-1c file; the row can move there once cut 1c lands.
/// </summary>
/// <remarks>
///     <para>
///         One unit serves mesh, sky and zone: 1/512 m (0.5 m per tile, 256 mesh units per tile), exact in binary, so
///         every coordinate stays an exact integer (tile corners x 256, heights raw 8.8, placements raw 24.8) and the
///         writers' root scale is exact. Assumed, pending RE-6 (<c>6r51.app</c>).
///     </para>
///     <para>
///         Mesh and sky: +Y up, +Z forward, right-handed (Assumed). Zone: +Z up, -Y forward, right-handed (Assumed).
///         The placement matrix (<see cref="PlacementMatrix" />, row vectors applied left to right) is scale(RawScale /
///         256), then <see cref="MeshToZone" /> ((x, y, z) to (x, -z, y), a proper rotation), then a rotation about +Z
///         by -2 pi Angle2 / 65536, then the raw translation. Measured (plan section 0.2): the yaw sign and binary-angle
///         unit (7,687 blocked vertices against 21,017 for the legacy (x, z, y) with +yaw), mesh +Z to zone -Y, mesh +Y to
///         zone +Z. Not measured: the x mirror (248 against 251 wins); the proper map is chosen because it keeps every
///         placed mesh congruent to its own document.
///     </para>
///     <para>
///         The <see cref="BethesdaModelRegistration.GameOption" /> app option may be absent, <c>auto</c> or name
///         Shadowkey (any case); any other value throws <see cref="ArgumentException" />, because the user asserted a
///         game a Shadowkey file cannot belong to.
///     </para>
/// </remarks>
internal static class ShadowkeyModelUnits
{
    /// <summary>Meters per Shadowkey mesh unit: 0.5 m per tile over 256 units per tile, exact in binary.</summary>
    public const double MetersPerUnit = 1.0 / 512;

    /// <summary>Mesh units per tile (the placements' 24.8 and the heights' 8.8 fixed point).</summary>
    public const int UnitsPerTile = 256;

    /// <summary>Binary-angle units per turn of <see cref="ShadowkeyEntity.Angle2" />.</summary>
    public const int AngleUnitsPerTurn = 65536;

    /// <summary>A quarter turn in binary-angle units.</summary>
    public const int QuarterTurn = AngleUnitsPerTurn / 4;

    /// <summary>The reverse-engineering item that could replace the row (design section 4.4).</summary>
    public const string ReverseEngineeringItem = "RE-6";

    /// <summary>The evidence every mesh, sky and zone document states for its unit (plan section 5.3).</summary>
    public const string UnitEvidence =
        "0.5 m per tile, 256 mesh units per tile: 8 of the 10 door meshes are 1,036 units, 4.047 tiles, against the " +
        "4.0-tile standard room height of 43,082 of 66,829 prototypes (BMT measurement, " +
        "SK/ShadowkeyZoneSceneBuilder.MeshUnitsPerTile); with that unit 2,025 of 8,255 placements rest within 1/8 tile " +
        "of their cell's floor, against at most 1,070 at half or double the unit or with the axis flipped; RE-6 " +
        "(6r51.app) pending";

    /// <summary>
    ///     The short evidence the per-slot placement documents of a zone state: the composition carries each placed
    ///     document's unit evidence per placement, so the full text is stated once, on the zone document.
    /// </summary>
    public const string PlacementUnitEvidence = "Shadowkey mesh units, 1/512 m (see the zone document; RE-6)";

    /// <summary>The evidence of the mesh and sky basis.</summary>
    public const string MeshBasisEvidence =
        "the second vertex component is up (pine tree 1,897 units tall, humanoid tunic 657); +Z forward and the " +
        "handedness are Assumed; 75 of 79 closed records are counter-clockwise from outside when read right-handed";

    /// <summary>The short mesh-basis evidence of a placement document (see <see cref="PlacementUnitEvidence" />).</summary>
    public const string PlacementBasisEvidence = "Shadowkey mesh basis, +Y up (see the zone document)";

    /// <summary>The evidence of the zone basis.</summary>
    public const string ZoneBasisEvidence =
        "the .zmp grid is row-major with x fastest and heights rise with the .zcp 8.8 values; placements map mesh +Y " +
        "to zone +Z and mesh +Z to zone -Y (measured: 7,687 blocked vertices against 21,017 for the legacy map), so " +
        "forward is -Y; the x mirror is not measured (248 against 251 wins) and right-handed is Assumed";

    /// <summary>The mesh and sky unit row.</summary>
    public static SceneUnits Units { get; } = new(MetersPerUnit, SceneValueProvenance.Assumed, UnitEvidence);

    /// <summary>The placement documents' unit row: the same factor with the short evidence.</summary>
    public static SceneUnits PlacementUnits { get; } =
        new(MetersPerUnit, SceneValueProvenance.Assumed, PlacementUnitEvidence);

    /// <summary>The mesh and sky basis: +Y up, +Z forward, right-handed, Assumed.</summary>
    public static SceneSourceBasis MeshBasis { get; } = new(Vector3.UnitY, Vector3.UnitZ, SceneHandedness.RightHanded,
        normalizedByReader: false, SceneValueProvenance.Assumed, MeshBasisEvidence);

    /// <summary>The placement documents' basis: the mesh basis with the short evidence.</summary>
    public static SceneSourceBasis PlacementBasis { get; } = new(Vector3.UnitY, Vector3.UnitZ,
        SceneHandedness.RightHanded, normalizedByReader: false, SceneValueProvenance.Assumed, PlacementBasisEvidence);

    /// <summary>The zone basis: +Z up, -Y forward, right-handed, Assumed.</summary>
    public static SceneSourceBasis ZoneBasis { get; } = new(Vector3.UnitZ, -Vector3.UnitY, SceneHandedness.RightHanded,
        normalizedByReader: false, SceneValueProvenance.Assumed, ZoneBasisEvidence);

    /// <summary>
    ///     The mesh-to-zone basis map M as a row-vector matrix: (x, y, z) to (x, -z, y), determinant +1. Its rows are the
    ///     images of mesh +X (zone +X), mesh +Y (zone +Z) and mesh +Z (zone -Y).
    /// </summary>
    public static Matrix4x4 MeshToZone { get; } = new(
        1, 0, 0, 0,
        0, 0, 1, 0,
        0, -1, 0, 0,
        0, 0, 0, 1);

    /// <summary>
    ///     The placement's first-row x and y entries, <c>m00 = fl32(s cos t)</c> and <c>m01 = fl32(s sin t)</c> with
    ///     <c>s = RawScale / 256</c> and <c>t = -2 pi Angle2 / 65536</c> evaluated in binary64 and rounded once; a quarter
    ///     turn (0 included) takes the exact cosine and sine.
    /// </summary>
    public static (float M00, float M01) RotationEntries(int angle2, ushort rawScale)
    {
        var scale = rawScale / 256.0;
        double cosine;
        double sine;
        if (angle2 % QuarterTurn == 0)
        {
            var quarter = (int)(((-(long)angle2 / QuarterTurn) % 4 + 4) % 4);
            (cosine, sine) = quarter switch
            {
                0 => (1.0, 0.0),
                1 => (0.0, 1.0),
                2 => (-1.0, 0.0),
                _ => (0.0, -1.0)
            };
        }
        else
        {
            var theta = -Math.Tau * angle2 / AngleUnitsPerTurn;
            cosine = Math.Cos(theta);
            sine = Math.Sin(theta);
        }

        return ((float)(scale * cosine), (float)(scale * sine));
    }

    /// <summary>
    ///     The placement transform of one <c>.ent</c> record, mesh units to zone units (row vectors):
    ///     <c>[[m00, m01, 0], [0, 0, s], [m01, -m00, 0]]</c> plus the raw translation, which is scale(s) x
    ///     <see cref="MeshToZone" /> x rotateZ(t) x translate(RawX, RawY, RawZ) with every entry rounded once (see
    ///     <see cref="RotationEntries" />). Angle0 and Angle1 are not applied (their axis and unit are not established).
    /// </summary>
    public static Matrix4x4 PlacementMatrix(ShadowkeyEntity placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var (m00, m01) = RotationEntries(placement.Angle2, placement.RawScale);
        var scale = placement.RawScale / 256f;
        return new Matrix4x4(
            m00, m01, 0, 0,
            0, 0, scale, 0,
            m01, -m00, 0, 0,
            placement.RawX, placement.RawY, placement.RawZ, 1);
    }

    /// <summary>
    ///     Resolves the game option: absent, <c>auto</c> or Shadowkey keeps the Shadowkey rows; anything else throws.
    /// </summary>
    /// <exception cref="ArgumentException">The game option names a game other than Shadowkey.</exception>
    public static void RequireShadowkey(IReadOnlyDictionary<string, string> appOptions)
    {
        ArgumentNullException.ThrowIfNull(appOptions);
        if (!appOptions.TryGetValue(BethesdaModelRegistration.GameOption, out var value) ||
            string.Equals(value.Trim(), "auto", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value.Trim(), nameof(BethesdaGame.Shadowkey), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"The {BethesdaModelRegistration.GameOption} option '{value}' names a game other than Shadowkey, but a " +
                $"Shadowkey mesh record or zone belongs only to Shadowkey (use shadowkey or auto)."),
            nameof(appOptions));
    }

    /// <summary>One culture-invariant line for <c>mesh formats</c>: the factor, its provenance and the RE item.</summary>
    public static string DescribeRow()
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"Shadowkey meshes and zones: {MetersPerUnit:R} m per mesh unit | {SceneValueProvenance.Assumed} | {ReverseEngineeringItem}");
    }
}
