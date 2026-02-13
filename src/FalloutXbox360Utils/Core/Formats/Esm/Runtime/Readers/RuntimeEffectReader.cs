using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Reader for BGSProjectile runtime structs from Xbox 360 memory dumps.
///     Extracts physics/sound data for projectile records.
/// </summary>
internal sealed class RuntimeEffectReader(RuntimeMemoryContext context)
{
    private readonly RuntimeMemoryContext _context = context;

    // Build-specific offset shift: Proto Debug PDB + _s = actual dump offset.
    private readonly int _s = RuntimeBuildOffsets.GetPdbShift(
        MinidumpAnalyzer.DetectBuildType(context.MinidumpInfo));

    #region Projectile Struct Layout (Proto Debug PDB base + _s)

    // BGSProjectile: PDB size 208, Debug dump 212, Release dump 224
    private int ProjStructSize => 208 + _s;
    private int ProjGravityOffset => 100 + _s;
    private int ProjSpeedOffset => 104 + _s;
    private int ProjRangeOffset => 108 + _s;
    private int ProjExplosionOffset => 132 + _s;
    private int ProjActiveSoundOffset => 136 + _s;
    private int ProjMuzzleFlashDurOffset => 140 + _s;
    private int ProjForceOffset => 148 + _s;
    private int ProjCountdownSoundOffset => 152 + _s;
    private int ProjDeactivateSoundOffset => 156 + _s;

    /// <summary>Model path BSStringT offset, shared by all TESBoundObject-derived types.</summary>
    private int ModelPathOffset => 64 + _s;

    #endregion

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

        // Read physics floats
        var gravity = BinaryUtils.ReadFloatBE(buffer, ProjGravityOffset);
        var speed = BinaryUtils.ReadFloatBE(buffer, ProjSpeedOffset);
        var range = BinaryUtils.ReadFloatBE(buffer, ProjRangeOffset);
        var muzzleFlashDuration = BinaryUtils.ReadFloatBE(buffer, ProjMuzzleFlashDurOffset);
        var force = BinaryUtils.ReadFloatBE(buffer, ProjForceOffset);

        // Basic validation: at least one physics value should be non-zero and reasonable
        if (float.IsNaN(speed) || float.IsInfinity(speed) ||
            float.IsNaN(gravity) || float.IsInfinity(gravity) ||
            float.IsNaN(range) || float.IsInfinity(range))
        {
            return null;
        }

        // Follow pointer fields
        var explosion = _context.FollowPointerToFormId(buffer, ProjExplosionOffset);
        var activeSound = _context.FollowPointerToFormId(buffer, ProjActiveSoundOffset);
        var countdownSound = _context.FollowPointerToFormId(buffer, ProjCountdownSoundOffset);
        var deactivateSound = _context.FollowPointerToFormId(buffer, ProjDeactivateSoundOffset);

        // Read world model path
        var modelPath = _context.ReadBSStringT(fileOffset, ModelPathOffset); // Same +80 offset as other TESBoundObject types

        return new ProjectilePhysicsData
        {
            Gravity = gravity,
            Speed = speed,
            Range = range,
            ExplosionFormId = explosion,
            ActiveSoundLoopFormId = activeSound,
            CountdownSoundFormId = countdownSound,
            DeactivateSoundFormId = deactivateSound,
            MuzzleFlashDuration = muzzleFlashDuration,
            Force = force,
            ModelPath = modelPath
        };
    }
}
