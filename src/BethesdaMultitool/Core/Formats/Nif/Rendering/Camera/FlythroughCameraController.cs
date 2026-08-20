#if WINDOWS_GUI
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;
using Windows.System;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>Camera control modes exposed by <see cref="FlythroughCameraController" />.</summary>
internal enum CameraMode
{
    /// <summary>Free-fly camera: WASD pans, Q/E climbs/descends, MoveSpeed defaults to a fast value.</summary>
    Fly,

    /// <summary>
    ///     First-person walk: WASD pans, Q/E are ignored, the camera Z is snapped each frame to
    ///     the terrain height under the camera plus <see cref="FlythroughCameraController.EyeHeight" />.
    ///     Space jumps (gravity-integrated hop). MoveSpeed defaults to the in-game player walking pace.
    /// </summary>
    Walk
}

/// <summary>
///     WASD + mouse-look + scroll-zoom flythrough camera controller for the v3 worldspace
///     view. Stateless w.r.t. GPU/UI — input methods just push into accumulators, and
///     <see cref="Update" /> integrates the state every frame via its <c>deltaSeconds</c> argument.
///     Bind input from <c>WorldView3DControl</c>'s KeyDown / KeyUp / PointerMoved / PointerWheelChanged
///     handlers.
///     <para>
///         Supports two modes via <see cref="Mode" />: <see cref="CameraMode.Fly" /> (default)
///         and <see cref="CameraMode.Walk" /> (snaps the camera to the ground at
///         <see cref="EyeHeight" />, disables vertical Q/E, and uses a separate MoveSpeed
///         slot so scrolling in one mode doesn't clobber the other's speed setting).
///     </para>
/// </summary>
internal sealed class FlythroughCameraController
{
    /// <summary>
    ///     Default fly-mode move speed (units/sec). Like every other constant in this type it is
    ///     authored in CLASSIC world units (~70 per metre) and multiplied by <see cref="UnitScale" />
    ///     for games whose unit differs — see <see cref="SetUnitScale" />.
    /// </summary>
    public const float FlySpeedDefault = 4096f;

    /// <summary>
    ///     Default walk-mode move speed (units/sec). Roughly the Bethesda human player
    ///     walking pace; run is ~3× via Shift, sneak ~0.25× via Ctrl.
    /// </summary>
    public const float WalkSpeedDefault = 100f;

    /// <summary>
    ///     Default eye height above the terrain in walk mode (units). The Bethesda player
    ///     capsule is 128 units tall; eye is roughly at 110–115.
    /// </summary>
    public const float WalkEyeHeightDefault = 112f;

    private readonly CameraState _camera;
    private readonly HashSet<VirtualKey> _keysDown = [];
    private float _accumulatedScroll;
    private Vector2 _accumulatedMouseDelta;

    private CameraMode _mode = CameraMode.Fly;
    private float _flyMoveSpeed = FlySpeedDefault;
    private float _walkMoveSpeed = WalkSpeedDefault;
    // Walk-mode jump state. _airborne gates SnapToGround (which would otherwise glue the camera to
    // the ground every frame); _verticalVelocity integrates against Gravity until we land.
    private bool _airborne;
    private float _verticalVelocity;
    private readonly WalkVoidRecovery _voidRecovery = new();

    // Grounded-Z easing state: false → the next grounded frame sets Z directly (mode switch /
    // teleport), so the camera never glides from a stale height across the map.
    private bool _walkZSettled;

    // Exponential approach rate (1/s) for grounded elevation changes: stairs/kerbs raise the target
    // eye height in discrete jumps, and hard-snapping to it teleported the view ("going up stairs
    // feels unnatural"). ~12/s closes 70% of a step in ~100 ms — fast enough to track slopes at run
    // speed, slow enough that a step reads as stepping UP rather than a cut.
    private const float WalkStepSmoothingRate = 12f;

    // Grounded downward gap (world units) beyond which the camera FALLS (gravity) instead of easing
    // down: walking off a ledge should drop, not glide. Comfortably above the tallest stair step.
    private const float WalkFallThreshold = 48f;

    public FlythroughCameraController(CameraState camera)
    {
        _camera = camera;
    }

    /// <summary>
    ///     Multiplier from this type's classic-unit constants into the active game's units
    ///     (<c>GameProfiles.HumanScaleFactor</c>). 1 for every classic-unit game; 1/70 for Starfield,
    ///     whose unit is a metre.
    /// </summary>
    public float UnitScale { get; private set; } = 1f;

    /// <summary>
    ///     Re-seeds every human-scale constant for a world whose unit is not the classic ~1.43 cm.
    ///     Without this a Starfield walk-mode eye sits 112 metres up — taller than the whole cell —
    ///     and one walking step covers an entire city block.
    ///     <para>
    ///         Call on worldspace load, before entering walk mode. Speeds are re-seeded from their
    ///         defaults rather than rescaled in place, so switching worlds does not compound a user's
    ///         scroll adjustments; a scale of 1 restores exactly the authored defaults.
    ///     </para>
    /// </summary>
    public void SetUnitScale(float unitScale)
    {
        if (!(unitScale > 0f) || UnitScale.Equals(unitScale)) return;

        UnitScale = unitScale;
        _flyMoveSpeed = FlySpeedDefault * unitScale;
        _walkMoveSpeed = WalkSpeedDefault * unitScale;
        EyeHeight = WalkEyeHeightDefault * unitScale;
        JumpSpeed = JumpSpeedDefault * unitScale;
        Gravity = GravityDefault * unitScale;
        CeilingHeadroom = CeilingHeadroomDefault * unitScale;
        // A settled walk pose is in the OLD scale — re-seat rather than glide to the new height.
        _walkZSettled = false;
    }

    /// <summary>
    ///     Active camera mode. Setting this snaps the camera to the ground if switching to walk
    ///     (so the user doesn't have to take a step before the height adjusts).
    /// </summary>
    public CameraMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            // Leaving any in-progress jump behind on a mode switch.
            _airborne = false;
            _verticalVelocity = 0f;
            _voidRecovery.Reset();
            _walkZSettled = false; // entering walk seats the camera directly, no glide
            if (_mode == CameraMode.Walk) SnapToGround(0f);
        }
    }

    /// <summary>
    ///     Movement speed in world units per second for the active mode (Shift = ×SpeedBoost,
    ///     Ctrl = ×SpeedSlow). Fly and walk modes track separate speeds; scrolling in one mode
    ///     leaves the other's speed untouched.
    /// </summary>
    public float MoveSpeed
    {
        get => _mode == CameraMode.Walk ? _walkMoveSpeed : _flyMoveSpeed;
        set
        {
            if (_mode == CameraMode.Walk) _walkMoveSpeed = value;
            else _flyMoveSpeed = value;
        }
    }

    public float SpeedBoostMultiplier { get; set; } = 4f;
    public float SpeedSlowMultiplier { get; set; } = 0.25f;

    /// <summary>Radians per pixel of mouse drag. Negative inverts pitch.</summary>
    public float MouseSensitivity { get; set; } = 0.003f;
    public bool InvertY { get; set; }

    /// <summary>Per-scroll-tick speed multiplier (logarithmic).</summary>
    public float ScrollSpeedFactor { get; set; } = 1.2f;

    /// <summary>One scroll wheel notch in WinUI 3 (delta == 120 raw → 1.0f here after normalization).</summary>
    public float ScrollNormalization { get; set; } = 120f;

    /// <summary>Height the camera is held above ground when in <see cref="CameraMode.Walk" />.</summary>
    public float EyeHeight { get; set; } = WalkEyeHeightDefault;

    /// <summary>Initial upward velocity (classic units/sec) of a walk-mode jump: a ~160-unit hop.</summary>
    public const float JumpSpeedDefault = 700f;

    /// <summary>Downward acceleration (classic units/sec²) applied while airborne in walk mode.</summary>
    public const float GravityDefault = 1500f;

    /// <summary>Clearance kept between the eye and a ceiling on jump contact (classic units).</summary>
    public const float CeilingHeadroomDefault = 8f;

    /// <summary>Initial upward velocity (units/sec) of a walk-mode jump. Default ≈ a ~160-unit hop
    /// against <see cref="Gravity" /> (a bit over the 128-unit player capsule height).</summary>
    public float JumpSpeed { get; set; } = JumpSpeedDefault;

    /// <summary>Downward acceleration (units/sec²) applied while airborne in walk mode.</summary>
    public float Gravity { get; set; } = GravityDefault;

    /// <summary>
    ///     Walk-mode ground-height lookup. <c>(worldX, worldY) → groundZ</c> or <c>null</c> when
    ///     the camera is over a cell without terrain data. Grounded motion leaves Z alone; an armed
    ///     jump that descends there restores its saved launch pose.
    /// </summary>
    public Func<float, float, float?>? GroundHeightSampler { get; set; }

    /// <summary>
    ///     Walk-mode ceiling lookup used during a jump's ascent. <c>(worldX, worldY) → world Z of the
    ///     nearest surface ABOVE the eye</c>, or <c>null</c> when nothing is overhead. When set, a jump
    ///     under a low roof bonks (the eye stops at the ceiling minus <see cref="CeilingHeadroom" />)
    ///     instead of the camera being lifted on top. Null leaves jumping unconstrained overhead.
    /// </summary>
    public Func<float, float, float?>? CeilingHeightSampler { get; set; }

    /// <summary>Clearance kept between the eye and a ceiling on jump contact (world units).</summary>
    public float CeilingHeadroom { get; set; } = CeilingHeadroomDefault;

    /// <summary>
    ///     Optional continuous horizontal collision resolver for walk mode. Receives the current eye
    ///     position and requested XY displacement and returns the allowed displacement (normally the
    ///     full move, a wall-clamped move, or a tangent slide). Fly mode deliberately bypasses it.
    /// </summary>
    public Func<Vector3, Vector2, Vector2>? HorizontalMovementResolver { get; set; }

    public void OnKeyDown(VirtualKey key) => _keysDown.Add(key);
    public void OnKeyUp(VirtualKey key) => _keysDown.Remove(key);

    /// <summary>Reset all keys (call when control loses focus to avoid stuck movement).</summary>
    public void ClearKeys() => _keysDown.Clear();

    public void OnMouseDelta(Vector2 delta) => _accumulatedMouseDelta += delta;
    public void OnScroll(float delta) => _accumulatedScroll += delta;

    /// <summary>
    ///     Integrates one frame. Called from the render loop with the elapsed time since
    ///     the previous call.
    /// </summary>
    public void Update(float deltaSeconds)
    {
        if (deltaSeconds <= 0f) return;

        ApplyScroll();
        ApplyMouseLook();
        ApplyKeyboardMovement(deltaSeconds);

        if (_mode == CameraMode.Walk) UpdateWalkVertical(deltaSeconds);

        _accumulatedMouseDelta = Vector2.Zero;
        _accumulatedScroll = 0f;
    }

    private void ApplyScroll()
    {
        if (MathF.Abs(_accumulatedScroll) < float.Epsilon) return;
        var ticks = _accumulatedScroll / ScrollNormalization;
        // ScrollSpeedFactor^ticks — wheel up (+) speeds up, wheel down (-) slows down.
        // Different speed ranges per mode: fly spans 16..200k (covers slow scout to teleport),
        // walk spans 16..2048 (covers sneak to sprint).
        var current = MoveSpeed;
        var (min, max) = _mode == CameraMode.Walk ? (16f, 2048f) : (16f, 200_000f);
        // The bounds are speeds, so they scale with the unit like the defaults do — left unscaled, the
        // 16-unit floor is 16 m/s in Starfield and the slowest possible walk is a sprint.
        MoveSpeed = Math.Clamp(
            current * MathF.Pow(ScrollSpeedFactor, ticks), min * UnitScale, max * UnitScale);
    }

    private void ApplyMouseLook()
    {
        if (_accumulatedMouseDelta == Vector2.Zero) return;

        // X delta → yaw (turn left/right). Y delta → pitch (look up/down).
        _camera.Yaw += _accumulatedMouseDelta.X * MouseSensitivity;
        var pitchDelta = _accumulatedMouseDelta.Y * MouseSensitivity;
        if (!InvertY) pitchDelta = -pitchDelta; // screen Y is down; non-inverted = drag down → look down
        _camera.Pitch = Math.Clamp(
            _camera.Pitch + pitchDelta,
            -MathF.PI * 0.5f + 0.01f,
            MathF.PI * 0.5f - 0.01f);
    }

    private void ApplyKeyboardMovement(float deltaSeconds)
    {
        var multiplier = 1f;
        if (_keysDown.Contains(VirtualKey.Shift)) multiplier *= SpeedBoostMultiplier;
        if (_keysDown.Contains(VirtualKey.Control)) multiplier *= SpeedSlowMultiplier;
        var step = MoveSpeed * multiplier * deltaSeconds;

        var move = Vector3.Zero;

        // In walk mode, WASD moves along the ground-plane projection of the camera basis
        // (so looking up doesn't make W glide skyward). In fly mode, follow the camera basis
        // as-is so the user can fly along the look direction.
        if (_mode == CameraMode.Walk)
        {
            var forwardGround = ProjectToGround(_camera.Forward);
            var rightGround = ProjectToGround(_camera.Right);
            if (_keysDown.Contains(VirtualKey.W)) move += forwardGround;
            if (_keysDown.Contains(VirtualKey.S)) move -= forwardGround;
            if (_keysDown.Contains(VirtualKey.D)) move += rightGround;
            if (_keysDown.Contains(VirtualKey.A)) move -= rightGround;
            // Q/E intentionally ignored in walk mode — SnapToGround owns the Z axis.
        }
        else
        {
            if (_keysDown.Contains(VirtualKey.W)) move += _camera.Forward;
            if (_keysDown.Contains(VirtualKey.S)) move -= _camera.Forward;
            if (_keysDown.Contains(VirtualKey.D)) move += _camera.Right;
            if (_keysDown.Contains(VirtualKey.A)) move -= _camera.Right;
            if (_keysDown.Contains(VirtualKey.E)) move += Vector3.UnitZ;
            if (_keysDown.Contains(VirtualKey.Q)) move -= Vector3.UnitZ;
        }

        if (move != Vector3.Zero)
        {
            var displacement = Vector3.Normalize(move) * step;
            if (_mode == CameraMode.Walk && HorizontalMovementResolver is { } resolveHorizontal)
            {
                var allowed = resolveHorizontal(
                    _camera.Position,
                    new Vector2(displacement.X, displacement.Y));
                _camera.Position += new Vector3(allowed.X, allowed.Y, 0f);
            }
            else
            {
                _camera.Position += displacement;
            }
        }
    }

    private static Vector3 ProjectToGround(Vector3 v)
    {
        var flat = new Vector3(v.X, v.Y, 0f);
        var len = flat.Length();
        return len > 0.0001f ? flat / len : Vector3.UnitY;
    }

    /// <summary>
    ///     Walk-mode vertical integration. Grounded: snaps to the terrain (Space launches a jump).
    ///     Airborne: integrates <see cref="Gravity" /> against <see cref="JumpSpeed" /> and lands back
    ///     on the terrain under the camera, or restores an explicitly-jumping camera when descent has
    ///     no floor. Without a <see cref="GroundHeightSampler" /> there is no ground to jump from (and
    ///     nothing to land on), so jumping is disabled and any in-progress hop is canceled.
    /// </summary>
    private void UpdateWalkVertical(float deltaSeconds)
    {
        if (GroundHeightSampler is null)
        {
            _airborne = false;
            _verticalVelocity = 0f;
            _voidRecovery.Reset();
            return;
        }

        var jumpHeld = _keysDown.Contains(VirtualKey.Space);
        _voidRecovery.ObserveJumpKey(jumpHeld);

        // Launch a jump when grounded and Space is held (re-hops after a NORMAL landing if still
        // held). Capture an exact safe floor pose first. If horizontal movement crossed an edge in
        // this same frame, TryArmJump falls back to the preceding grounded pose.
        if (!_airborne && jumpHeld)
        {
            var launchPosition = _camera.Position;
            Vector3? currentGroundedPosition =
 GroundHeightSampler(launchPosition.X, launchPosition.Y) is float launchGround
                ? new Vector3(launchPosition.X, launchPosition.Y, launchGround + EyeHeight)
                : null;
            if (_voidRecovery.TryArmJump(currentGroundedPosition))
            {
                _airborne = true;
                _verticalVelocity = JumpSpeed;
            }
        }

        if (!_airborne)
        {
            SnapToGround(deltaSeconds);
            return;
        }

        _verticalVelocity -= Gravity * deltaSeconds;
        var pos = _camera.Position;
        var newZ = pos.Z + _verticalVelocity * deltaSeconds;

        // Ceiling: while ascending, stop the eye at the nearest surface overhead (minus headroom) so a
        // jump under a low roof bonks instead of clipping through and being lifted on top. Zeroing the
        // velocity lets gravity pull the camera back down next frame.
        if (_verticalVelocity > 0f && CeilingHeightSampler is { } ceilingSampler)
        {
            var clamped = WalkVerticalMath.ClampToCeiling(newZ, ceilingSampler(pos.X, pos.Y), CeilingHeadroom);
            if (clamped < newZ)
            {
                newZ = clamped;
                _verticalVelocity = 0f;
            }
        }

        // Land once we descend to the eye-height floor above the ground beneath the (possibly
        // air-controlled) X/Y. An explicitly-armed jump that descends over NO floor returns to its
        // pre-jump safe pose; natural ledge falls are unarmed and retain the ordinary gravity path.
        if (_verticalVelocity <= 0f)
        {
            var ground = GroundHeightSampler(pos.X, pos.Y);
            if (_voidRecovery.TryRecover(descending: true, floorKnown: ground is not null, out var recovered))
            {
                _airborne = false;
                _verticalVelocity = 0f;
                _walkZSettled = true;
                _camera.Position = recovered;
                return;
            }

            // Floor of last resort. Clipping through geometry (or drifting off the loaded grid while
            // airborne) leaves NO floor under the camera and no armed jump, which used to fall forever.
            // Restore the last confirmed floor pose; with none ever recorded, freeze the camera where
            // it is rather than inventing a height to teleport to.
            if (_voidRecovery.TryObserveFallStep(
                    descending: true, floorKnown: ground is not null, newZ,
                    out var fallOutcome, out var fallSafePosition))
            {
                _airborne = false;
                _verticalVelocity = 0f;
                _walkZSettled = true;
                _camera.Position = fallOutcome == WalkFallOutcome.Restore
                    ? fallSafePosition
                    : new Vector3(pos.X, pos.Y, newZ);
                return;
            }

            if (ground is float floorGround)
            {
                var floor = floorGround + EyeHeight;
                if (newZ <= floor)
                {
                    newZ = floor;
                    _airborne = false;
                    _verticalVelocity = 0f;
                    _walkZSettled = true; // landed exactly at the floor — no post-landing glide
                    _voidRecovery.CompleteLanding(new Vector3(pos.X, pos.Y, floor));
                }
            }
        }

        _camera.Position = new Vector3(pos.X, pos.Y, newZ);
    }

    /// <summary>
    ///     Grounded walk-mode Z: eases the camera toward <c>groundHeight + EyeHeight</c>
    ///     (frame-rate-independent exponential approach — see <see cref="WalkStepSmoothingRate" />)
    ///     so stairs/kerbs read as stepping up instead of a snap cut. A downward gap beyond
    ///     <see cref="WalkFallThreshold" /> hands off to gravity (walking off a ledge FALLS). The
    ///     first grounded frame after a mode switch/teleport (<see cref="_walkZSettled" /> false, or
    ///     <paramref name="deltaSeconds" /> 0) seats the camera directly. No-op when no sampler is
    ///     set or the height is unknown (camera off the loaded grid) — the existing Z is preserved
    ///     so walking off the edge never teleports to Z=0.
    /// </summary>
    private void SnapToGround(float deltaSeconds)
    {
        if (GroundHeightSampler is not { } sampler) return;
        var pos = _camera.Position;
        if (sampler(pos.X, pos.Y) is not float ground) return;
        var target = ground + EyeHeight;

        if (!_walkZSettled || deltaSeconds <= 0f)
        {
            _walkZSettled = true;
            _camera.Position = new Vector3(pos.X, pos.Y, target);
            _voidRecovery.RecordGrounded(_camera.Position);
            return;
        }

        if (target < pos.Z - (WalkFallThreshold * UnitScale))
        {
            // Ledge: let gravity take it from here (UpdateWalkVertical integrates the fall and
            // lands on the floor below) instead of gliding down at the easing rate.
            _airborne = true;
            _verticalVelocity = 0f;
            return;
        }

        // A step-up under a low roof must not push the eye through it. Only an actual RISE can, and
        // only then is the ceiling sampled — the ceiling query rebuilds the same 3x3 candidate set as
        // the ground capsule, so running it every grounded frame would double walk-mode collision cost
        // for the flat-ground case that can never bonk. The fall check above deliberately uses the
        // unclamped ground target so a low roof cannot be read as a ledge. The 1-unit rise gate is
        // human-scale (≈1.4 cm classic) and must scale like its sibling threshold above — unscaled it
        // is a full METRE in Starfield, letting stair steps push the eye through a low ceiling.
        if (target > pos.Z + (1f * UnitScale) && CeilingHeightSampler is { } ceilingSampler)
        {
            target = WalkVerticalMath.ClampToCeiling(target, ceilingSampler(pos.X, pos.Y), CeilingHeadroom);
        }

        var blend = 1f - MathF.Exp(-WalkStepSmoothingRate * deltaSeconds);
        var newZ = pos.Z + ((target - pos.Z) * blend);
        if (MathF.Abs(target - newZ) < 0.05f) newZ = target; // settle exactly, no asymptotic crawl
        _camera.Position = new Vector3(pos.X, pos.Y, newZ);
        _voidRecovery.RecordGrounded(new Vector3(pos.X, pos.Y, target));
    }
}
#endif
