using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Reader for BGSProjectile runtime structs from Xbox 360 memory dumps.
///     Extracts physics/sound data for projectile records.
///     Supports auto-detected layouts via <see cref="RuntimeEffectProbe" />.
/// </summary>
internal sealed class RuntimeEffectReader(
    RuntimeMemoryContext context,
    RuntimeLayoutProbeResult<int[]>? probeResult = null)
{
    private readonly RuntimeMemoryContext _context = context;

    // Uniform shift for all post-TESForm fields: probed value if confident, else PDB default.
    private readonly int _s = probeResult is { Margin: >= MinProbeMargin }
        ? probeResult.Winner.Layout.Length > 1 ? probeResult.Winner.Layout[1] : 0
        : RuntimeBuildOffsets.GetPdbShift(MinidumpAnalyzer.DetectBuildType(context.MinidumpInfo));

    /// <summary>
    ///     Read BGSProjectile physics/sound data from a runtime struct at the given file offset.
    ///     Returns null if validation fails (struct not readable or values out of range).
    /// </summary>
    public ProjectilePhysicsData? ReadProjectilePhysics(long fileOffset, uint expectedFormId)
    {
        if (fileOffset + ProjStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[ProjStructSize];
        try
        {
            _context.Accessor.ReadArray(fileOffset, buffer, 0, ProjStructSize);
        }
        catch
        {
            return null;
        }

        // Validate FormID at +12
        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != expectedFormId)
        {
            return null;
        }

        // Validate FormType at +4 should be 0x33 (PROJ)
        if (buffer[4] != 0x33)
        {
            return null;
        }

        // Read BGSProjectileData flags
        var flags = BinaryUtils.ReadUInt32BE(buffer, ProjFlagsOffset);

        // Read physics floats from BGSProjectileData
        var gravity = BinaryUtils.ReadFloatBE(buffer, ProjGravityOffset);
        var speed = BinaryUtils.ReadFloatBE(buffer, ProjSpeedOffset);
        var range = BinaryUtils.ReadFloatBE(buffer, ProjRangeOffset);
        var tracerChance = BinaryUtils.ReadFloatBE(buffer, ProjTracerChanceOffset);
        var explosionProximity = BinaryUtils.ReadFloatBE(buffer, ProjExplosionProximityOffset);
        var explosionTimer = BinaryUtils.ReadFloatBE(buffer, ProjExplosionTimerOffset);
        var muzzleFlashDuration = BinaryUtils.ReadFloatBE(buffer, ProjMuzzleFlashDurOffset);
        var fadeOutTime = BinaryUtils.ReadFloatBE(buffer, ProjFadeOutTimeOffset);
        var force = BinaryUtils.ReadFloatBE(buffer, ProjForceOffset);
        var rotationX = BinaryUtils.ReadFloatBE(buffer, ProjRotationXOffset);
        var rotationY = BinaryUtils.ReadFloatBE(buffer, ProjRotationYOffset);
        var rotationZ = BinaryUtils.ReadFloatBE(buffer, ProjRotationZOffset);
        var bounceMultiplier = BinaryUtils.ReadFloatBE(buffer, ProjBounceMultiplierOffset);

        // Basic validation: at least one physics value should be non-zero and reasonable
        if (float.IsNaN(speed) || float.IsInfinity(speed) ||
            float.IsNaN(gravity) || float.IsInfinity(gravity) ||
            float.IsNaN(range) || float.IsInfinity(range))
        {
            return null;
        }

        // Follow pointer fields from BGSProjectileData
        var light = _context.FollowPointerToFormId(buffer, ProjLightOffset);
        var muzzleFlashLight = _context.FollowPointerToFormId(buffer, ProjMuzzleFlashLightOffset);
        var explosion = _context.FollowPointerToFormId(buffer, ProjExplosionOffset);
        var activeSound = _context.FollowPointerToFormId(buffer, ProjActiveSoundOffset);
        var countdownSound = _context.FollowPointerToFormId(buffer, ProjCountdownSoundOffset);
        var deactivateSound = _context.FollowPointerToFormId(buffer, ProjDeactivateSoundOffset);
        var defaultWeaponSource = _context.FollowPointerToFormId(buffer, ProjDefaultWeaponSourceOffset);

        // Read outer BGSProjectile fields
        var modelPath = _context.ReadBSStringT(fileOffset, ModelPathOffset);
        var fullName = _context.ReadBSStringT(fileOffset, ProjFullNameOffset);
        var soundLevel = BinaryUtils.ReadUInt32BE(buffer, ProjSoundLevelOffset);

        return new ProjectilePhysicsData
        {
            Flags = flags,
            Gravity = gravity,
            Speed = speed,
            Range = range,
            LightFormId = light,
            MuzzleFlashLightFormId = muzzleFlashLight,
            TracerChance = tracerChance,
            ExplosionProximity = explosionProximity,
            ExplosionTimer = explosionTimer,
            ExplosionFormId = explosion,
            ActiveSoundLoopFormId = activeSound,
            MuzzleFlashDuration = muzzleFlashDuration,
            FadeOutTime = fadeOutTime,
            Force = force,
            CountdownSoundFormId = countdownSound,
            DeactivateSoundFormId = deactivateSound,
            DefaultWeaponSourceFormId = defaultWeaponSource,
            RotationX = rotationX,
            RotationY = rotationY,
            RotationZ = rotationZ,
            BounceMultiplier = bounceMultiplier,
            ModelPath = modelPath,
            FullName = fullName,
            SoundLevel = soundLevel
        };
    }

    #region Projectile Struct Layout (Proto Debug PDB base + _s)

    // BGSProjectile: PDB size 208, Debug dump 212, Release dump 224
    private int ProjStructSize => 208 + _s;

    // --- Outer BGSProjectile fields ---

    /// <summary>TESFullName.cFullName BSStringT (Proto +52).</summary>
    private int ProjFullNameOffset => 52 + _s;

    /// <summary>TESModel.cModel BSStringT (Proto +64).</summary>
    private int ModelPathOffset => 64 + _s;

    /// <summary>BGSProjectile.eSoundLevel enum (Proto +204).</summary>
    private int ProjSoundLevelOffset => 204 + _s;

    // --- BGSProjectileData fields (embedded at Proto +96) ---

    /// <summary>iFlags uint32 (Data+0).</summary>
    private int ProjFlagsOffset => 96 + _s;

    private int ProjGravityOffset => 100 + _s;
    private int ProjSpeedOffset => 104 + _s;
    private int ProjRangeOffset => 108 + _s;

    /// <summary>pLight TESObjectLIGH* (Data+16).</summary>
    private int ProjLightOffset => 112 + _s;

    /// <summary>pMuzzleFlashLight TESObjectLIGH* (Data+20).</summary>
    private int ProjMuzzleFlashLightOffset => 116 + _s;

    /// <summary>fTracerChance float (Data+24).</summary>
    private int ProjTracerChanceOffset => 120 + _s;

    /// <summary>fExplosionProximity float (Data+28).</summary>
    private int ProjExplosionProximityOffset => 124 + _s;

    /// <summary>fExplosionTimer float (Data+32).</summary>
    private int ProjExplosionTimerOffset => 128 + _s;

    private int ProjExplosionOffset => 132 + _s;
    private int ProjActiveSoundOffset => 136 + _s;
    private int ProjMuzzleFlashDurOffset => 140 + _s;

    /// <summary>fFadeOutTime float (Data+48).</summary>
    private int ProjFadeOutTimeOffset => 144 + _s;

    private int ProjForceOffset => 148 + _s;
    private int ProjCountdownSoundOffset => 152 + _s;
    private int ProjDeactivateSoundOffset => 156 + _s;

    /// <summary>pDefaultWeaponSource TESObjectWEAP* (Data+64).</summary>
    private int ProjDefaultWeaponSourceOffset => 160 + _s;

    /// <summary>fRotationX float (Data+68).</summary>
    private int ProjRotationXOffset => 164 + _s;

    /// <summary>fRotationY float (Data+72).</summary>
    private int ProjRotationYOffset => 168 + _s;

    /// <summary>fRotationZ float (Data+76).</summary>
    private int ProjRotationZOffset => 172 + _s;

    /// <summary>fBounceMultiplier float (Data+80).</summary>
    private int ProjBounceMultiplierOffset => 176 + _s;

    private const int MinProbeMargin = 3;

    #endregion
}
