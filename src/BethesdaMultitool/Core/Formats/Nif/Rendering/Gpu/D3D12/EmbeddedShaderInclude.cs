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
/// </summary>
internal sealed class EmbeddedShaderInclude : CallbackBase, Include
{
    /// <summary>Process-lifetime singleton, mirroring the compiler's bytecode cache. Never disposed.</summary>
    internal static readonly EmbeddedShaderInclude Instance = new();

    private EmbeddedShaderInclude()
    {
    }

    public Stream Open(IncludeType type, string fileName, Stream? parentStream) =>
        new MemoryStream(Encoding.UTF8.GetBytes(GpuShaderCompiler12.ReadSource(Path.GetFileName(fileName))));

    public void Close(Stream stream) => stream.Dispose();
}
