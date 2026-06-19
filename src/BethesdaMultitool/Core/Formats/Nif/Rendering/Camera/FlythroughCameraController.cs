#if WINDOWS_GUI
using System.Numerics;
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
    /// <summary>Default fly-mode move speed (units/sec).</summary>
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

    public FlythroughCameraController(CameraState camera)
    {
        _camera = camera;
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
            if (_mode == CameraMode.Walk) SnapToGround();
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

    /// <summary>Initial upward velocity (units/sec) of a walk-mode jump. Default ≈ a ~160-unit hop
    /// against <see cref="Gravity" /> (a bit over the 128-unit player capsule height).</summary>
    public float JumpSpeed { get; set; } = 700f;

    /// <summary>Downward acceleration (units/sec²) applied while airborne in walk mode.</summary>
    public float Gravity { get; set; } = 1500f;

    /// <summary>
    ///     Walk-mode ground-height lookup. <c>(worldX, worldY) → groundZ</c> or <c>null</c> when
    ///     the camera is over a cell without terrain data (in which case the Z is left alone).
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
    public float CeilingHeadroom { get; set; } = 8f;

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
        MoveSpeed = Math.Clamp(current * MathF.Pow(ScrollSpeedFactor, ticks), min, max);
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
            _camera.Position += Vector3.Normalize(move) * step;
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
    ///     on the terrain under the camera. Without a <see cref="GroundHeightSampler" /> there is no
    ///     ground to jump from (and nothing to land on), so jumping is disabled and any in-progress
    ///     hop is cancelled — the camera Z is left wherever it is.
    /// </summary>
    private void UpdateWalkVertical(float deltaSeconds)
    {
        if (GroundHeightSampler is null)
        {
            _airborne = false;
            _verticalVelocity = 0f;
            return;
        }

        // Launch a jump when grounded and Space is held (re-hops after landing if still held).
        if (!_airborne && _keysDown.Contains(VirtualKey.Space))
        {
            _airborne = true;
            _verticalVelocity = JumpSpeed;
        }

        if (!_airborne)
        {
            SnapToGround();
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
        // air-controlled) X/Y. GroundHeightSampler null over a no-terrain cell leaves us airborne
        // until we drift back over terrain.
        if (_verticalVelocity <= 0f && GroundHeightSampler(pos.X, pos.Y) is float ground)
        {
            var floor = ground + EyeHeight;
            if (newZ <= floor)
            {
                newZ = floor;
                _airborne = false;
                _verticalVelocity = 0f;
            }
        }

        _camera.Position = new Vector3(pos.X, pos.Y, newZ);
    }

    /// <summary>
    ///     Snap the camera's Z to <c>groundHeight + EyeHeight</c>. No-op when no sampler is
    ///     set or the sampler reports an unknown height (e.g. camera outside the worldspace
    ///     grid). In that case the camera's existing Z is preserved so the user doesn't get
    ///     teleported to Z=0 if they walk off the edge of the loaded terrain.
    /// </summary>
    private void SnapToGround()
    {
        if (GroundHeightSampler is not { } sampler) return;
        var pos = _camera.Position;
        var ground = sampler(pos.X, pos.Y);
        if (ground is float h)
            _camera.Position = new Vector3(pos.X, pos.Y, h + EyeHeight);
    }
}
#endif
