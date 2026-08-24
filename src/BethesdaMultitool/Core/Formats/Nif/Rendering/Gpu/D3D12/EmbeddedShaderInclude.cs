using System.Text;
using SharpGen.Runtime;
using Vortice.Direct3D;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Resolves <c>#include</c> directives against the same flat embedded-resource index that
///     top-level shaders compile from. Any directory component in the include path is ignored —
///     mirroring the csproj's flat <c>LogicalName</c> scheme, in which a shader file name is
///     globally unique by build-time guarantee, so <c>#include "atmosphere.hlsli"</c> resolves
///     identically from any shader in any subdirectory. Nested includes work without extra code:
///     FXC re-enters <see cref="Open" /> once per directive.
/// <para>
///     ⚠ <b>One instance per compile, never shared.</b> This was a process-lifetime singleton, on
///     the reasoning that the handler is stateless so one would do. It is stateless — but
///     <see cref="CallbackBase" /> is a reference-counted COM callback, and two threads compiling at
///     once race its AddRef/Release. Losing that race drops the count to zero, disposes the shadow,
///     and every subsequent compile of a shader containing an <c>#include</c> fails with
///     <c>X1505: No include handler specified</c> — for the rest of the process. Measured at roughly
///     one run in three with three shader-compiling test classes in parallel (2026-08-24); in the
///     app the same race sits between the render thread's PSO creation and any warm-up compile, and
///     its symptom would be a startup shader failure that does not reproduce on the next launch.
///     Construction is a managed allocation against a multi-millisecond compile, so per-call
///     ownership costs nothing measurable.
/// </para>
/// </summary>
internal sealed class EmbeddedShaderInclude : CallbackBase, Include
{

    public Stream Open(IncludeType type, string fileName, Stream? parentStream)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(GpuShaderCompiler12.ReadSource(Path.GetFileName(fileName))));
    }

    public void Close(Stream stream)
    {
        stream.Dispose();
    }
}
