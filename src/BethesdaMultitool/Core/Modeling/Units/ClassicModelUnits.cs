using System.Globalization;
using BethesdaMultitool.Core.Games;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Units;

/// <summary>
///     The classic (XnGine) unit rows of design section 4.1 as the cut-1c readers apply them (plan section 6.3): the exact
///     meters-per-native-unit literal per game, each with its provenance and evidence, plus the guard row for a mesh
///     whose game is not established. Every value here is <see cref="SceneValueProvenance.Assumed" />: no classic
///     executable has been read for its unit yet (RE-2, RE-3, RE-4 in the design's backlog), and the evidence says what
///     was measured instead. The factors are stated once, as literals, so a test can pin them without recomputing them
///     and a document's units can be traced to one line.
/// </summary>
/// <remarks>
///     <para>
///         The rows deliberately do NOT come from <see cref="GameProfiles" />: the classic profiles keep the viewer's
///         unit (<c>ClassicViewerUnits</c>, 1/70 m so the level pane's camera constants apply to native units unchanged),
///         which is not a measurement of any of these games. The per-format registry the design schedules from cut 1c
///         is this type.
///     </para>
///     <para>
///         A native unit is 1/256 of a world unit in every XnGine mesh (<c>XnGineMesh.PointDivisor</c>), so each row is
///         the world unit's size divided by 256: Daggerfall 0.025/256 = 1/10240, Redguard 0.0125/256 = 1/20480 and
///         Battlespire (1/64)/256 = 2^-14 = 1/16384, which is exact in binary. The Daggerfall factor is exactly twice the
///         Redguard one, which is why the plan's ambiguity rule (an 8-byte-only mesh with header +20 = 0) must not guess:
///         calling an extracted ARCH3D record Redguard would halve its size.
///     </para>
/// </remarks>
internal static class ClassicModelUnits
{
    /// <summary>Daggerfall ARCH3D: 0.025 m per world unit, 1/256 world unit per native unit (design section 4.1, RE-2).</summary>
    public const double DaggerfallMetersPerUnit = 1.0 / 10240;

    /// <summary>Redguard loose <c>.3D</c>, ROB segments and <c>.3DC</c> actors: 1/80 m per world unit, 1/256 per native unit (RE-3).</summary>
    public const double RedguardMetersPerUnit = 1.0 / 20480;

    /// <summary>Battlespire: 1/64 m per world unit, 1/256 world unit per native unit; 2^-14, exact in binary (RE-4, weak).</summary>
    public const double BattlespireMetersPerUnit = 1.0 / 16384;

    /// <summary>The guard row's factor: one, an arithmetic fallback and not a claim of meters.</summary>
    public const double UnknownMetersPerUnit = 1.0;

    /// <summary>The evidence stated on every Daggerfall document (design section 4.1, Daggerfall row).</summary>
    public const string DaggerfallEvidence =
        "0.025 m per world unit, 1/256 world unit per native unit (XnGineMesh.PointDivisor): ARCH3D objects 55000 to " +
        "55005 measure 48 x 88 x 6 world units, a door leaf (2.2 m); 41000 is 19 x 88, a bed; tables 41109/41110 are " +
        "33 high (0.83 m); identities inferred from shape (arch3d_bounds.py); agrees with Daggerfall Unity's 0.025 " +
        "(recalled); RE-2 (FALL.EXE player height, eye height or collision radius) pending";

    /// <summary>The evidence stated on every Redguard static-mesh document (design section 4.1, Redguard row).</summary>
    public const string RedguardEvidence =
        "1/80 m per world unit, 1/256 world unit per native unit: RGM placements are world units x 256 " +
        "(RedguardRgmFile.cs:56-57); TV_TABLE 60 high (0.75 m), LH_DOOR 186 (2.3 m), MG_CHR01 seat width 44 (0.55 m), " +
        "TV_BOTTL 27 (0.34 m) (rob_bounds.py, xng3d_bounds.py); RE-3 (RG.EXE) pending";

    /// <summary>The evidence stated on every Redguard <c>.3DC</c> document (design section 4.1, actor row).</summary>
    public const string RedguardActorEvidence =
        RedguardEvidence + "; applied to a .3DC actor as an assumption for human actors only: keyframes are " +
        "normalized to the int16 range (median keyframe Y extent exactly 32,766 native units over 147 files; CYRSA001 " +
        "Y 32,766 = 1.60 m), so the true size needs a per-actor runtime scale (RGM MPSZ candidate, RE-3) and every " +
        ".3DC document carries the " + ActorScaleDiagnostic + " diagnostic";

    /// <summary>The evidence stated on every Battlespire document (design section 4.1, Battlespire row).</summary>
    public const string BattlespireEvidence =
        "1/64 m per world unit, 1/256 world unit per native unit: world units are mesh native / 256 (BS6 POSI); " +
        "CHAIR 57, TABLE 46, BENCH 36, BAREL 48 and the ARMOR.3D stand 152 world units give 0.012 to 0.023 m per " +
        "world unit, median about 0.016 (weak); RE-4 (Battlespire player height) pending";

    /// <summary>The evidence stated when the game is not established (the design's guard row).</summary>
    public const string UnknownEvidence =
        "game not established: factor 1.0 with Unknown provenance is an arithmetic fallback, not a claim of meters; " +
        "pass --game daggerfall, --game battlespire or --game redguard to select a row";

    /// <summary>The diagnostic code every Redguard <c>.3DC</c> document carries (design section 4.1, actor row).</summary>
    public const string ActorScaleDiagnostic = "bmt.redguard.3dc.actor-scale-unknown";

    /// <summary>The diagnostic message beside <see cref="ActorScaleDiagnostic" />, as the design words its Degraded row.</summary>
    public const string ActorScaleDiagnosticMessage =
        "actor scale unknown; geometry normalized to the int16 range (1/20480 m per native unit assumed for human actors only)";

    /// <summary>The design's unit table for the XnGine populations, in the design's row order, guard row last.</summary>
    public static IReadOnlyList<ClassicModelUnitRow> Rows { get; } =
    [
        new ClassicModelUnitRow("XnGine .3D, Daggerfall ARCH3D", BethesdaGame.Daggerfall, DaggerfallMetersPerUnit,
            SceneValueProvenance.Assumed, DaggerfallEvidence, "RE-2"),
        new ClassicModelUnitRow("XnGine .3D, Redguard loose and ROB", BethesdaGame.Redguard, RedguardMetersPerUnit,
            SceneValueProvenance.Assumed, RedguardEvidence, "RE-3"),
        new ClassicModelUnitRow("Redguard .3DC actors", BethesdaGame.Redguard, RedguardMetersPerUnit,
            SceneValueProvenance.Assumed, RedguardActorEvidence, "RE-3"),
        new ClassicModelUnitRow("XnGine .3D, Battlespire", BethesdaGame.Battlespire, BattlespireMetersPerUnit,
            SceneValueProvenance.Assumed, BattlespireEvidence, "RE-4"),
        new ClassicModelUnitRow("Game unknown", BethesdaGame.Unknown, UnknownMetersPerUnit,
            SceneValueProvenance.Unknown, UnknownEvidence, "none")
    ];

    /// <summary>
    ///     The static-mesh row for a game: Daggerfall, Battlespire or Redguard, or the guard row for
    ///     <see cref="BethesdaGame.Unknown" />. A <c>.3DC</c> document takes <see cref="ForRedguardActor" /> instead.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The game is not one the XnGine readers serve.</exception>
    public static SceneUnits For(BethesdaGame game)
    {
        return game switch
        {
            BethesdaGame.Daggerfall => new SceneUnits(DaggerfallMetersPerUnit, SceneValueProvenance.Assumed,
                DaggerfallEvidence),
            BethesdaGame.Redguard => new SceneUnits(RedguardMetersPerUnit, SceneValueProvenance.Assumed,
                RedguardEvidence),
            BethesdaGame.Battlespire => new SceneUnits(BattlespireMetersPerUnit, SceneValueProvenance.Assumed,
                BattlespireEvidence),
            BethesdaGame.Unknown => new SceneUnits(UnknownMetersPerUnit, SceneValueProvenance.Unknown, UnknownEvidence),
            _ => throw new ArgumentOutOfRangeException(nameof(game), game,
                "The classic unit table has rows for Daggerfall, Battlespire and Redguard (and the Unknown guard) only.")
        };
    }

    /// <summary>The Redguard <c>.3DC</c> actor row: the Redguard factor with the actor-scale evidence.</summary>
    public static SceneUnits ForRedguardActor()
    {
        return new SceneUnits(RedguardMetersPerUnit, SceneValueProvenance.Assumed, RedguardActorEvidence);
    }

    /// <summary>
    ///     One culture-invariant line per row for <c>mesh formats</c> and the format metadata's unit policy:
    ///     <c>name: factor m per native unit | provenance | RE item</c>.
    /// </summary>
    public static string DescribeRows()
    {
        var lines = new List<string>(Rows.Count);
        foreach (var row in Rows)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{row.Name}: {row.MetersPerUnit:R} m per native unit | {row.Provenance} | {row.ReverseEngineeringItem}"));
        }

        return string.Join("; ", lines);
    }
}
