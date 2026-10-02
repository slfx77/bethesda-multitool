using BethesdaMultitool.Core.Modeling.Units;

namespace BethesdaMultitool.Core.Modeling.Redguard;

/// <summary>
///     The document diagnostics the Redguard <c>.3DC</c> reader raises besides the XnGine geometry codes it shares with
///     the <c>.3D</c> reader (<see cref="Xngine.XnGineModelDiagnostics.AddGeometry" />) and the identity code it reuses
///     (<see cref="Xngine.XnGineGameIdentity.StepRefutedDiagnostic" />). Every message is raw text.
/// </summary>
internal static class Redguard3DcModelDiagnostics
{
    /// <summary>
    ///     Every <c>.3DC</c> document: the actor scale is unknown and the geometry is normalized to the int16 range (the
    ///     design's Degraded row, which a reader can only carry as a diagnostic; plan section 4, "Units").
    /// </summary>
    public const string ActorScaleUnknown = ClassicModelUnits.ActorScaleDiagnostic;

    /// <summary>Every <c>.3DC</c> document: the UV encoding is not decoded (plan decision D3).</summary>
    public const string UvUndecoded = "bmt.redguard.3dc.uv-undecoded";

    /// <summary>A stack with a clip: the 15 frames per second rate and the Step interpolation are Assumed.</summary>
    public const string PoseRateAssumed = "bmt.redguard.3dc.pose-rate-assumed";

    /// <summary>A single-frame stack: no later pose, so no clip.</summary>
    public const string SingleFrame = "bmt.redguard.3dc.single-frame";

    /// <summary>
    ///     N-gons whose pose-independent kept corners differ from the keyframe's own corner test (41 retail n-gons), so
    ///     the triangles differ from the legacy keyframe-only export there.
    /// </summary>
    public const string PoseDependentCorners = "bmt.redguard.3dc.pose-dependent-corners";

    /// <summary>A later pose's coordinate magnitude reaches 2^24, so its float32 position is rounded (none on retail).</summary>
    public const string PosePositionRounded = "bmt.redguard.3dc.pose-position-rounded";
}
