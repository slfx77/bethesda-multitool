namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     One standalone KF controller sequence whose transform targets are source names rather than
///     object refs into a destination NIF. The model owns every array and deliberately carries no
///     source bytes, archive handles, or invented destination block identities.
/// </summary>
internal sealed record NifNameTargetedAnimationClip(
    string Name,
    float Frequency,
    float StartTime,
    float StopTime,
    NifCycleType Cycle,
    string? AccumRootName,
    NifNodeTrack[] Tracks,
    NifAnimTextKey[] TextKeys,
    int UnsupportedTransformTrackCount);
