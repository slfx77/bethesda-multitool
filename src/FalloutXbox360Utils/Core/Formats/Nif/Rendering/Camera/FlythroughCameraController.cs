#if WINDOWS_GUI
using System.Numerics;
using Windows.System;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     WASD + mouse-look + scroll-zoom flythrough camera controller for the v3 worldspace
///     view. Stateless w.r.t. GPU/UI — input methods just push into accumulators, and
///     <see cref="Update" /> integrates the state every frame via <paramref name="deltaSeconds" />.
///     Bind input from <c>WorldView3DControl</c>'s KeyDown / KeyUp / PointerMoved / PointerWheelChanged
///     handlers.
/// </summary>
internal sealed class FlythroughCameraController
{
    private readonly CameraState _camera;
    private readonly HashSet<VirtualKey> _keysDown = [];
    private float _accumulatedScroll;
    private Vector2 _accumulatedMouseDelta;

    public FlythroughCameraController(CameraState camera)
    {
        _camera = camera;
    }

    /// <summary>Movement speed in world units per second (Shift = ×SpeedBoost, Ctrl = ×SpeedSlow).</summary>
    public float MoveSpeed { get; set; } = 4096f;

    public float SpeedBoostMultiplier { get; set; } = 4f;
    public float SpeedSlowMultiplier { get; set; } = 0.25f;

    /// <summary>Radians per pixel of mouse drag. Negative inverts pitch.</summary>
    public float MouseSensitivity { get; set; } = 0.003f;
    public bool InvertY { get; set; }

    /// <summary>Per-scroll-tick speed multiplier (logarithmic).</summary>
    public float ScrollSpeedFactor { get; set; } = 1.2f;

    /// <summary>One scroll wheel notch in WinUI 3 (delta == 120 raw → 1.0f here after normalization).</summary>
    public float ScrollNormalization { get; set; } = 120f;

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

        _accumulatedMouseDelta = Vector2.Zero;
        _accumulatedScroll = 0f;
    }

    private void ApplyScroll()
    {
        if (_accumulatedScroll == 0f) return;
        var ticks = _accumulatedScroll / ScrollNormalization;
        // ScrollSpeedFactor^ticks — wheel up (+) speeds up, wheel down (-) slows down.
        MoveSpeed = Math.Clamp(MoveSpeed * MathF.Pow(ScrollSpeedFactor, ticks), 16f, 200_000f);
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
        if (_keysDown.Contains(VirtualKey.W)) move += _camera.Forward;
        if (_keysDown.Contains(VirtualKey.S)) move -= _camera.Forward;
        if (_keysDown.Contains(VirtualKey.D)) move += _camera.Right;
        if (_keysDown.Contains(VirtualKey.A)) move -= _camera.Right;
        if (_keysDown.Contains(VirtualKey.E)) move += Vector3.UnitZ;
        if (_keysDown.Contains(VirtualKey.Q)) move -= Vector3.UnitZ;

        if (move != Vector3.Zero)
            _camera.Position += Vector3.Normalize(move) * step;
    }
}
#endif
