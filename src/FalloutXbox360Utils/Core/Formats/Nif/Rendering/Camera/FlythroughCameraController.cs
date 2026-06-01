#if WINDOWS_GUI
using System.Numerics;
using Windows.System;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;

/// <summary>Camera control modes exposed by <see cref="FlythroughCameraController" />.</summary>
internal enum CameraMode
{
    /// <summary>Free-fly camera: WASD pans, Q/E climbs/descends, MoveSpeed defaults to a fast value.</summary>
    Fly,

    /// <summary>
    ///     First-person walk: WASD pans, Q/E are ignored, the camera Z is snapped each frame to
    ///     the terrain height under the camera plus <see cref="FlythroughCameraController.EyeHeight" />.
    ///     MoveSpeed defaults to the in-game player walking pace.
    /// </summary>
    Walk
}

/// <summary>
///     WASD + mouse-look + scroll-zoom flythrough camera controller for the v3 worldspace
///     view. Stateless w.r.t. GPU/UI — input methods just push into accumulators, and
///     <see cref="Update" /> integrates the state every frame via <paramref name="deltaSeconds" />.
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

    /// <summary>
    ///     Walk-mode ground-height lookup. <c>(worldX, worldY) → groundZ</c> or <c>null</c> when
    ///     the camera is over a cell without terrain data (in which case the Z is left alone).
    /// </summary>
    public Func<float, float, float?>? GroundHeightSampler { get; set; }

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

        if (_mode == CameraMode.Walk) SnapToGround();

        _accumulatedMouseDelta = Vector2.Zero;
        _accumulatedScroll = 0f;
    }

    private void ApplyScroll()
    {
        if (_accumulatedScroll == 0f) return;
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
