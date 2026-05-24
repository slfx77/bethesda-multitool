using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Reader for BGSProjectile runtime structs (FormType 0x33 PROJ). Reads
///     physics/sound data via <see cref="PdbStructView" /> with a single
///     <see cref="PdbStructView.WithShift" /> band applied per
///     <see cref="RuntimeEffectProbe" />. The probe found uniform shift
///     of +16 across 31/32 sampled DMPs with one +12 outlier (a single
///     Release-Beta xex dump where Data lives 4 bytes earlier than the PDB
///     baseline).
///     <para>
///         The misleading class name is legacy — "Effect" here means BGSProjectile
///         (the magic/weapon projectile), not magic effects (those moved to
///         <see cref="RuntimeMagicReader" /> in Phase 1B.8).
///     </para>
/// </summary>
internal sealed class RuntimeEffectReader
{
    private const byte ProjFormType = 0x33;
    private const int MinProbeMargin = 3;
    private const int PdbBaselineShift = 16;

    // BGSProjectileData subfield offsets, relative to the Data field itself.
    // The PDB layout treats Data as an opaque 84-byte struct (no per-field
    // decomposition), so we hardcode the inner offsets — they're stable across
    // every dump in the probe sweep; only the OUTER Data offset shifts.
    private const int DataFlagsOffset = 0;
    private const int DataGravityOffset = 4;
    private const int DataSpeedOffset = 8;
    private const int DataRangeOffset = 12;
    private const int DataLightOffset = 16;
    private const int DataMuzzleFlashLightOffset = 20;
    private const int DataTracerChanceOffset = 24;
    private const int DataExplosionProximityOffset = 28;
    private const int DataExplosionTimerOffset = 32;
    private const int DataExplosionOffset = 36;
    private const int DataActiveSoundOffset = 40;
    private const int DataMuzzleFlashDurOffset = 44;
    private const int DataFadeOutTimeOffset = 48;
    private const int DataForceOffset = 52;
    private const int DataCountdownSoundOffset = 56;
    private const int DataDeactivateSoundOffset = 60;
    private const int DataDefaultWeaponSourceOffset = 64;
    private const int DataRotationXOffset = 68;
    private const int DataRotationYOffset = 72;
    private const int DataRotationZOffset = 76;
    private const int DataBounceMultiplierOffset = 80;

    private readonly RuntimeMemoryContext _context;
    private readonly RuntimePdbFieldAccessor _fields;
    private readonly int _shift; // delta vs PDB baseline (negative = read earlier than PDB)

    public RuntimeEffectReader(RuntimeMemoryContext context, RuntimeLayoutProbeResult<int[]>? probeResult = null)
    {
        _context = context;
        _fields = new RuntimePdbFieldAccessor(context);
        _shift = ComputeShift(probeResult, context);
    }

    private static int ComputeShift(RuntimeLayoutProbeResult<int[]>? probeResult, RuntimeMemoryContext context)
    {
        var probedAbsolute = probeResult is { Margin: >= MinProbeMargin } &&
                             probeResult.Winner.Layout.Length > 1
            ? probeResult.Winner.Layout[1]
            : RuntimeBuildOffsets.GetPdbShift(MinidumpAnalyzer.DetectBuildType(context.MinidumpInfo));

        // Probe / build-shift returns the absolute offset; PDB layouts were
        // built for the +16 baseline, so the band shift is the delta.
        return probedAbsolute - PdbBaselineShift;
    }

    /// <summary>
    ///     Read BGSProjectile physics/sound data from a runtime struct at the given file offset.
    ///     Returns null if validation fails (struct not readable or values out of range).
    /// </summary>
    public ProjectilePhysicsData? ReadProjectilePhysics(long fileOffset, uint expectedFormId)
    {
        // ReadProjectilePhysics is called directly with a fileOffset (not via
        // OpenStructView). Synthesize a minimal entry so we can route through the view.
        var entry = new RuntimeEditorIdEntry
        {
            EditorId = string.Empty,
            FormId = expectedFormId,
            FormType = ProjFormType,
            TesFormOffset = fileOffset
        };
        var view = _fields.OpenStructView(entry)?.WithShift(0, int.MaxValue, _shift);
        if (view == null)
        {
            return null;
        }

        return ReadPhysicsFromView(view);
    }

    /// <summary>
    ///     Read a full ProjectileRecord from a runtime BGSProjectile struct.
    ///     Used by MergeRuntimeRecords to create projectile records from DMP data.
    /// </summary>
    public ProjectileRecord? ReadRuntimeProjectile(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != ProjFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry)?.WithShift(0, int.MaxValue, _shift);
        if (view == null)
        {
            return null;
        }

        var physics = ReadPhysicsFromView(view);
        if (physics == null)
        {
            return null;
        }

        // BGSProjectileData.iFlags is a uint32 where low 16 bits = flags, high 16 bits = type
        var flags = (ushort)(physics.Flags & 0xFFFF);
        var projType = (ushort)((physics.Flags >> 16) & 0xFFFF);

        return new ProjectileRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = physics.FullName,
            ModelPath = physics.ModelPath,
            Flags = flags,
            ProjectileType = projType,
            Gravity = physics.Gravity,
            Speed = physics.Speed,
            Range = physics.Range,
            Light = physics.LightFormId ?? 0,
            MuzzleFlashLight = physics.MuzzleFlashLightFormId ?? 0,
            TracerChance = physics.TracerChance,
            ExplosionProximity = physics.ExplosionProximity,
            ExplosionTimer = physics.ExplosionTimer,
            Explosion = physics.ExplosionFormId ?? 0,
            Sound = physics.ActiveSoundLoopFormId ?? 0,
            MuzzleFlashDuration = physics.MuzzleFlashDuration,
            FadeDuration = physics.FadeOutTime,
            ImpactForce = physics.Force,
            CountdownSound = physics.CountdownSoundFormId ?? 0,
            DeactivateSound = physics.DeactivateSoundFormId ?? 0,
            DefaultWeaponSource = physics.DefaultWeaponSourceFormId ?? 0,
            RotationX = physics.RotationX,
            RotationY = physics.RotationY,
            RotationZ = physics.RotationZ,
            BounceMultiplier = physics.BounceMultiplier,
            SoundLevel = physics.SoundLevel
        };
    }

    private ProjectilePhysicsData? ReadPhysicsFromView(PdbStructView view)
    {
        // The PDB describes Data as an opaque 84-byte struct (no per-field decomposition),
        // so we resolve the outer Data offset via the view (PDB+shift) and add the
        // inner field offsets manually from DataXxxOffset constants.
        var dataOffset = view.Offset("Data", "BGSProjectile");
        if (dataOffset is not { } dataOff)
        {
            return null;
        }

        var buffer = view.Buffer;
        var flags = BinaryUtils.ReadUInt32BE(buffer, dataOff + DataFlagsOffset);
        var gravity = BinaryUtils.ReadFloatBE(buffer, dataOff + DataGravityOffset);
        var speed = BinaryUtils.ReadFloatBE(buffer, dataOff + DataSpeedOffset);
        var range = BinaryUtils.ReadFloatBE(buffer, dataOff + DataRangeOffset);
        var tracerChance = BinaryUtils.ReadFloatBE(buffer, dataOff + DataTracerChanceOffset);
        var explosionProximity = BinaryUtils.ReadFloatBE(buffer, dataOff + DataExplosionProximityOffset);
        var explosionTimer = BinaryUtils.ReadFloatBE(buffer, dataOff + DataExplosionTimerOffset);
        var muzzleFlashDuration = BinaryUtils.ReadFloatBE(buffer, dataOff + DataMuzzleFlashDurOffset);
        var fadeOutTime = BinaryUtils.ReadFloatBE(buffer, dataOff + DataFadeOutTimeOffset);
        var force = BinaryUtils.ReadFloatBE(buffer, dataOff + DataForceOffset);
        var rotationX = BinaryUtils.ReadFloatBE(buffer, dataOff + DataRotationXOffset);
        var rotationY = BinaryUtils.ReadFloatBE(buffer, dataOff + DataRotationYOffset);
        var rotationZ = BinaryUtils.ReadFloatBE(buffer, dataOff + DataRotationZOffset);
        var bounceMultiplier = BinaryUtils.ReadFloatBE(buffer, dataOff + DataBounceMultiplierOffset);

        if (float.IsNaN(speed) || float.IsInfinity(speed) ||
            float.IsNaN(gravity) || float.IsInfinity(gravity) ||
            float.IsNaN(range) || float.IsInfinity(range))
        {
            return null;
        }

        var light = _context.FollowPointerToFormId(buffer, dataOff + DataLightOffset);
        var muzzleFlashLight = _context.FollowPointerToFormId(buffer, dataOff + DataMuzzleFlashLightOffset);
        var explosion = _context.FollowPointerToFormId(buffer, dataOff + DataExplosionOffset);
        var activeSound = _context.FollowPointerToFormId(buffer, dataOff + DataActiveSoundOffset);
        var countdownSound = _context.FollowPointerToFormId(buffer, dataOff + DataCountdownSoundOffset);
        var deactivateSound = _context.FollowPointerToFormId(buffer, dataOff + DataDeactivateSoundOffset);
        var defaultWeaponSource = _context.FollowPointerToFormId(buffer, dataOff + DataDefaultWeaponSourceOffset);

        var modelPath = view.BsString("cModel", "TESModel");
        var fullName = view.BsString("cFullName", "TESFullName");
        var soundLevel = view.UInt32("eSoundLevel", "BGSProjectile");

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
}
