using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 16b (RE-24): the Shared nested-normalization policy a quaternion TBC or QUADRATIC rotation gets on
///     one file, decided by the platform the file was shipped for, or the reason the Squad mapping is refused on that
///     platform. Explicit and per file, never inferred from the keys.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             A little-endian file is a PC file: <see cref="SceneGamebryoSquadPolicy.PcFloat32" />, the GECK's x87
///             precision-24 FastNormalize that RE-24 verified bit for bit (whatever the console option says, because the
///             option names the console a BIG-endian file was shipped for).
///         </item>
///         <item>
///             A big-endian file shipped for the Xbox 360 (<see cref="NifPackedPlatform.X360" />, the cut-1a default when
///             the shell sets no option): <see cref="SceneGamebryoSquadPolicy.Xbox360Estimate" />. Its FastNormalize is a
///             VMX128 reciprocal-square-root estimate whose table is not public, so Shared holds the track and refuses to
///             sample it; that is the truthful state, not a substitute.
///         </item>
///         <item>
///             A big-endian file shipped for the PlayStation 3 (<see cref="NifPackedPlatform.Ps3" />) is refused with
///             <see cref="NifModelCurveBlock.SquadPolicyPs3" />: RE-24 read the GECK and the X360 build and examined no PS3
///             binary, so no normalization policy is measured for it.
///         </item>
///     </list>
/// </remarks>
/// <param name="Policy">The Shared policy; null when the mapping is refused.</param>
/// <param name="Block">Why the mapping is refused; <see cref="NifModelCurveBlock.None" /> when a policy applies.</param>
/// <param name="Source">Where the decision came from, for native state (the byte order, or the platform selection's source).</param>
internal readonly record struct NifModelSquadPolicy(
    SceneGamebryoSquadPolicy? Policy,
    NifModelCurveBlock Block,
    string Source)
{
    /// <summary>The source text of the PC decision.</summary>
    public const string PcSource = "little-endian file: PC, GECK x87 precision 24 (RE-24)";

    /// <summary>The PC policy: every little-endian file.</summary>
    public static NifModelSquadPolicy Pc { get; } =
        new(SceneGamebryoSquadPolicy.PcFloat32, NifModelCurveBlock.None, PcSource);

    /// <summary>True when the Squad mapping is refused on this platform.</summary>
    public bool IsBlocked => Block != NifModelCurveBlock.None;

    /// <summary>True when the value names a policy or a refusal (the default value names neither).</summary>
    public bool IsResolved => Policy is not null || IsBlocked;

    /// <summary>The Xbox 360 policy, with the platform selection's source as evidence.</summary>
    /// <param name="source">The selection's source text (<see cref="NifPackedPlatformSelection.Source" />).</param>
    /// <returns>The X360 policy.</returns>
    public static NifModelSquadPolicy Xbox360(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new NifModelSquadPolicy(SceneGamebryoSquadPolicy.Xbox360Estimate, NifModelCurveBlock.None, source);
    }

    /// <summary>The PlayStation 3 refusal, with the platform selection's source as evidence.</summary>
    /// <param name="source">The selection's source text (<see cref="NifPackedPlatformSelection.Source" />).</param>
    /// <returns>The blocked decision.</returns>
    public static NifModelSquadPolicy Ps3Blocked(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new NifModelSquadPolicy(null, NifModelCurveBlock.SquadPolicyPs3, source);
    }

    /// <summary>Decides the policy of one file from its byte order and the read's platform selection.</summary>
    /// <param name="bigEndian">True when the file is big-endian (a console file).</param>
    /// <param name="platform">The platform the cut-1a path resolved for the read (<see cref="NifPackedPlatformOption.Resolve" />).</param>
    /// <returns>The decision.</returns>
    public static NifModelSquadPolicy Resolve(bool bigEndian, NifPackedPlatformSelection platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        if (!bigEndian)
        {
            return Pc;
        }

        return platform.Platform == NifPackedPlatform.Ps3 ? Ps3Blocked(platform.Source) : Xbox360(platform.Source);
    }
}
