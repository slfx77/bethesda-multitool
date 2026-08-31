namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Conditions the per-frame timestep the camera integrates: median-of-3 over the last three raw
///     samples, then a ceiling.
///     <para>
///         A single spiked frame (GC pause, compositor stall) otherwise integrates as a visible
///         camera jump. The median rejects any lone outlier while — unlike an EMA — adding zero
///         steady-state lag: when the three samples agree, the median <em>is</em> the current
///         sample.
///     </para>
///     <para>
///         Order matters. The clamp runs <em>after</em> the median so a genuine multi-frame stall
///         still reaches the camera bounded, rather than being median-ed away and then clamped to
///         nothing. Missing history on the first two frames duplicates the current raw value, so
///         those frames pass through unfiltered instead of being dragged toward a zero-initialised
///         sample.
///     </para>
///     <para>
///         Lives in <c>Core/</c> rather than beside its caller in <c>App/</c> because it is pure
///         arithmetic with no renderer dependency: <c>App/**</c> is excluded from the
///         <c>net10.0</c> target framework, and while this logic lived there the only way to cover
///         it was to assert that the renderer's source text still contained a particular
///         <c>MathF.Max(MathF.Min(…))</c> expression — which broke on reformatting and could not
///         check a single numeric outcome.
///     </para>
///     <para>
///         A class, not a struct: the filter carries three frames of history, and a mutable struct
///         would silently lose it on any copy (passing it to a method, capturing it, storing it in
///         a collection). Each owner holds one instance for its lifetime.
///     </para>
/// </summary>
public sealed class FrameDeltaFilter
{
    /// <summary>
    ///     Ceiling applied after the median. Bounds a long pause or debugger break so the camera
    ///     advances at most this far in one frame.
    /// </summary>
    public const float MaxDeltaSeconds = 0.1f;

    private float _previous1;
    private float _previous2;
    private int _sampleCount;

    /// <summary>
    ///     Discards timing history when rendering pauses for a scene load. Without this, the first
    ///     two frames of the new scene can inherit pre-load samples and turn the load gap into camera
    ///     motion even though the caller has reseeded its wall-clock timestamp.
    /// </summary>
    public void Reset()
    {
        _previous1 = 0;
        _previous2 = 0;
        _sampleCount = 0;
    }

    /// <summary>
    ///     Feeds one raw timestep and returns the value the camera should integrate.
    /// </summary>
    /// <param name="rawSeconds">The measured wall-clock delta for this frame.</param>
    public float Push(float rawSeconds)
    {
        // Before three samples exist, substitute the current value for the missing history: the
        // median of three equal values is that value, so early frames pass through raw.
        var previous1 = _sampleCount >= 1 ? _previous1 : rawSeconds;
        var previous2 = _sampleCount >= 2 ? _previous2 : rawSeconds;

        _previous2 = _previous1;
        _previous1 = rawSeconds;
        if (_sampleCount < 2)
        {
            _sampleCount++;
        }

        var median = MathF.Max(
            MathF.Min(rawSeconds, previous1),
            MathF.Min(MathF.Max(rawSeconds, previous1), previous2));

        return median > MaxDeltaSeconds ? MaxDeltaSeconds : median;
    }
}
