using FalloutXbox360Utils.Core.Formats.EsmRecord.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord;

public sealed partial class RuntimeStructReader
{
    /// <summary>
    ///     Read BGSProjectile physics/sound data from a runtime struct at the given file offset.
    ///     Returns null if validation fails (struct not readable or values out of range).
    /// </summary>
    public ProjectilePhysicsData? ReadProjectilePhysics(long fileOffset, uint expectedFormId)
    {
        if (fileOffset + ProjStructSize > _fileSize)
        {
            return null;
        }

        var buffer = new byte[ProjStructSize];
        try
        {
            _accessor.ReadArray(fileOffset, buffer, 0, ProjStructSize);
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
        var explosion = FollowPointerToFormId(buffer, ProjExplosionOffset);
        var activeSound = FollowPointerToFormId(buffer, ProjActiveSoundOffset);
        var countdownSound = FollowPointerToFormId(buffer, ProjCountdownSoundOffset);
        var deactivateSound = FollowPointerToFormId(buffer, ProjDeactivateSoundOffset);

        // Read world model path
        var modelPath = ReadBSStringT(fileOffset, WeapModelPathOffset); // Same +80 offset as other TESBoundObject types

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
