using BethesdaMultitool.Core.Formats.Nif.Rendering.Abstractions;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Slfx77.Multitool.Core.Lifetime;
using Slfx77.Multitool.WinUI.Direct3D12.Shaders;
using Vortice.Direct3D;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>Retains one shader pair and independent Shared caches for ordinary and depth-writing blend pipelines.</summary>
/// <remarks>All access uses the creating thread. Profile replacement and disposal require the caller to retire
/// every recorded or submitted draw using prior handles; this route supplies no GPU fence.</remarks>
internal sealed class ReferenceShaderRoute12 : IDisposable
{
    private readonly ShaderPipelineCache<ReferenceBlendPipelineKey> _blendPsos;
    private readonly ShaderPipelineCache<ReferenceBlendPipelineKey> _blendDepthWritePsos;
    private readonly RetiredResourceDisposal _retiredResources = new();
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private GameShaderPair _profile;
    private ReadOnlyMemory<byte>? _vsBytecode;
    private ReadOnlyMemory<byte>? _psBytecode;
    private bool _profileChangePending;
    private bool _disposed;

    /// <summary>Creates an inactive route with empty caches and no native allocations.</summary>
    /// <param name="rootSignature">Borrowed world root retained independently by each future cache entry.</param>
    internal ReferenceShaderRoute12(GpuRootSignature12 rootSignature)
    {
        _blendPsos = rootSignature.CreatePipelineCache<ReferenceBlendPipelineKey>();
        _blendDepthWritePsos = rootSignature.CreatePipelineCache<ReferenceBlendPipelineKey>();
        _retiredResources.Add(_blendPsos, "reference blend cache");
        _retiredResources.Add(_blendDepthWritePsos, "reference depth-writing blend cache");
    }

    /// <summary>Creates the always-active shared route over already compiled shader bytecode.</summary>
    /// <param name="rootSignature">Borrowed world root retained independently by each future cache entry.</param>
    /// <param name="vsBytecode">Borrowed vertex shader selecting the route's input and constant-buffer ABI.</param>
    /// <param name="psBytecode">Borrowed pixel shader for this route.</param>
    internal ReferenceShaderRoute12(
        GpuRootSignature12 rootSignature, ReadOnlyMemory<byte> vsBytecode, ReadOnlyMemory<byte> psBytecode)
        : this(rootSignature)
    {
        _vsBytecode = vsBytecode;
        _psBytecode = psBytecode;
    }

    /// <summary>Gets whether both shader stages are available for this route.</summary>
    internal bool Active => !_disposed && _vsBytecode is not null && _psBytecode is not null;

    /// <summary>Replaces the caller-retired profile, preserving the shared fallback on shader compilation failure.</summary>
    /// <param name="profile">Game-specific shader pair, or a disabled pair selecting the shared fallback.</param>
    /// <param name="consumerName">Diagnostic context passed to the existing shader compiler.</param>
    /// <param name="vsMacros">Optional vertex-only macros selecting the direct or instanced ABI.</param>
    /// <remarks>Unchanged profiles are no-ops unless earlier cache retirement failed. A failed clear keeps this
    /// route inactive and retryable; a failed compile resets the profile so the same request may retry.</remarks>
    /// <exception cref="AggregateException">Prior cache releases remain pending.</exception>
    /// <exception cref="ObjectDisposedException">The route has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Called from another thread.</exception>
    internal void Set(GameShaderPair profile, string consumerName, ShaderMacro[]? vsMacros = null)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_profileChangePending && profile.Equals(_profile)) { return; }

        _profileChangePending = true;
        _vsBytecode = null;
        _psBytecode = null;
        ClearCaches();
        _profile = profile;
        _profileChangePending = false;
        if (!profile.Enabled) { return; }

        var compiled = vsMacros is { Length: > 0 }
            ? profile.TryCompile(consumerName, vsMacros, [])
            : profile.TryCompile(consumerName);
        if (compiled is null)
        {
            _profile = default;
            return;
        }
        (_vsBytecode, _psBytecode) = compiled.Value;
    }

    /// <summary>Returns a route-local borrowed pipeline, creating an owned slot-zero family only on a miss.</summary>
    /// <typeparam name="TState">Caller state passed without allocating a per-draw closure.</typeparam>
    /// <param name="key">Unmodified authored blend and draw-state identity.</param>
    /// <param name="depthWrite">Selects the independent depth-writing cache.</param>
    /// <param name="state">State passed to the creation callback on a cache miss.</param>
    /// <param name="create">Creates slot zero through the supplied retained family and returns that exact handle.</param>
    /// <returns>The cached borrowed native pipeline; cache hits invoke no creation callback.</returns>
    /// <exception cref="InvalidOperationException">The route is inactive or accessed from another thread.</exception>
    /// <exception cref="ObjectDisposedException">The route has been disposed.</exception>
    internal ID3D12PipelineState GetOrCreate<TState>(
        ReferenceBlendPipelineKey key,
        bool depthWrite,
        TState state,
        Func<TState, ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, ShaderPipelineResources, ID3D12PipelineState> create)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_vsBytecode is not { } vs || _psBytecode is not { } ps)
        {
            throw new InvalidOperationException("The reference shader route is inactive.");
        }
        var cache = depthWrite ? _blendDepthWritePsos : _blendPsos;
        return cache.GetOrCreate(key, (state, vs, ps, create),
            static (context, family) => context.create(context.state, context.vs, context.ps, family));
    }

    /// <summary>Retries both caller-retired cache releases without losing a failed sibling.</summary>
    /// <exception cref="AggregateException">One or more cache entries remain owned for retry.</exception>
    /// <exception cref="InvalidOperationException">Called from another thread.</exception>
    public void Dispose()
    {
        VerifyAccess();
        _disposed = true;
        _retiredResources.Dispose();
    }

    /// <summary>Attempts both profile caches independently; each Shared cache retains its failed entries.</summary>
    /// <exception cref="AggregateException">One or more cache clears must be retried before activating a profile.</exception>
    private void ClearCaches()
    {
        List<Exception>? failures = null;
        try { _blendPsos.Clear(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        try { _blendDepthWritePsos.Clear(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        if (failures is not null)
        {
            throw new AggregateException("Reference shader route retirement remains pending.", failures);
        }
    }

    /// <summary>Rejects cross-thread cache access before any profile or stopped-state mutation.</summary>
    /// <exception cref="InvalidOperationException">Called from a thread other than the creating thread.</exception>
    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("Reference shader routes must be accessed on their creating thread.");
        }
    }
}
