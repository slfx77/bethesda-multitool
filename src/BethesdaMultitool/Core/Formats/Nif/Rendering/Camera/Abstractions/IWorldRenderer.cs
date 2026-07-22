using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.Abstractions;

/// <summary>
///     v3 Pass 4 Step 2 — common surface shared by every live worldspace renderer
///     (terrain, references, water, wireframe). Each renderer also implements a
///     renderer-type-specific interface that adds its <c>LoadData</c> shape and any
///     extra public properties.
///     <para>
///         WorldView3DControl holds one field per renderer-type, typed as the interface;
///         instantiation creates the D3D12 concrete class (the single supported backend).
///         The render-frame loop iterates / calls each renderer through its interface —
///         backend-agnostic.
///     </para>
/// </summary>
internal interface IWorldRenderer : IDisposable
{
    /// <summary>
    ///     Draws every cell intersecting <paramref name="cylinder" /> from the most
    ///     recently loaded data set. Returns a renderer-defined count (cells drawn,
    ///     REFRs drawn, etc.) for the HUD.
    /// </summary>
    int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder);

    /// <summary>Last-frame profiling stats. Populated by <see cref="Render" />.</summary>
    global::BethesdaMultitool.WorldRenderStats LastStats { get; }

    /// <summary>
    ///     Enables detailed phase timings. Cheap counters remain populated when this is false.
    /// </summary>
    bool DetailedProfilingEnabled { get; set; }
}
