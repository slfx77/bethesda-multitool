namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;

/// <summary>
///     Vertical search window for walk-mode placed-object ground. The window is anchored to the
///     player's feet, not the eye, so a ceiling or upper floor below eye height cannot become ground.
///     Terrain is intentionally outside this policy; landscape sampling continues to bridge cell seams
///     and follow authored slopes independently of placed-object step limits.
/// </summary>
internal readonly record struct WalkGroundProbeWindow(
    float FeetZ,
    float HighestStepZ,
    float RayOriginZ)
{
    public static WalkGroundProbeWindow FromEye(
        float eyeZ,
        float eyeHeight,
        float stepHeight,
        float rayOriginSlack)
    {
        var feetZ = eyeZ - eyeHeight;
        var highestStepZ = feetZ + stepHeight;
        return new WalkGroundProbeWindow(feetZ, highestStepZ, highestStepZ + rayOriginSlack);
    }

    /// <summary>
    ///     True when a warm collision hit or cold OBND top is no higher than the current step window.
    ///     There is deliberately no lower bound: ledge/fall handling belongs to the camera controller.
    /// </summary>
    public bool AllowsPlacedSurface(float surfaceZ)
    {
        return surfaceZ <= HighestStepZ;
    }
}
