using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Vegetation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Abstractions;

/// <summary>
///     A per-game vertex+pixel shader pair, returned by a subject's <c>ForGame</c> registry
///     (<see cref="GrassShaderProfile" /> is pair #1).
///     <para>
///         <c>default</c> (<see cref="Enabled" /> = false) means "use the shared path". The fallback
///         is STRUCTURAL rather than a branch at any call site: disabled simply means the existing
///         shared shaders are used, so no draw path needs to know a per-game shader might exist.
///     </para>
///     <para>
///         Water deliberately does NOT use this type — it has no shared-path fallback to degrade to;
///         <c>WaterProfile.PixelShaderFile</c> is its per-game seam.
///     </para>
/// </summary>
/// <param name="Enabled">Whether a recovered per-game shader pair exists.</param>
/// <param name="VertexShaderName">Embedded vertex shader file name.</param>
/// <param name="PixelShaderName">Embedded pixel shader file name.</param>
internal readonly record struct GameShaderPair(
    bool Enabled,
    string? VertexShaderName,
    string? PixelShaderName)
{
    private static readonly Logger Log = Logger.Instance;

    /// <summary>
    ///     Compiles both stages, FAIL-SOFT: a compile failure logs and returns null so the caller
    ///     keeps drawing with the shared shaders rather than throwing — a caller's catch would
    ///     otherwise take down its entire pipeline (and with it every placed object) over one
    ///     game's pair. Disabled pairs also return null.
    /// </summary>
    /// <param name="consumerName">Names the consumer in the fallback log line.</param>
    internal (byte[] Vs, byte[] Ps)? TryCompile(string consumerName)
    {
        if (!Enabled) return null;

        try
        {
            var vs = GpuShaderCompiler12.Compile(VertexShaderName!, "main", "vs_5_1");
            var ps = GpuShaderCompiler12.Compile(PixelShaderName!, "main", "ps_5_1");
            return (vs, ps);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            Log.Error(
                "{0}: per-game shaders '{1}'/'{2}' failed to compile; " +
                "falling back to the shared shaders. {3}",
                consumerName, VertexShaderName, PixelShaderName, ex);
            return null;
        }
    }
}
