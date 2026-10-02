using Slfx77.Multitool.WinUI.Direct3D12.Shaders;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>Resolves shader text through Bethesda's flat, case-insensitive embedded-resource index.</summary>
/// <remarks>Directory components and including-source names do not affect resolution because embedded
/// shader file names are globally unique. This stateless provider can be borrowed by concurrent
/// compilations; Shared owns a separate native include callback for every compiler call.</remarks>
internal sealed class EmbeddedShaderSourceProvider : IShaderSourceProvider
{
    /// <summary>Loads a root shader or include using only its file name.</summary>
    /// <param name="name">Requested shader or include path; directory components are ignored.</param>
    /// <param name="includingSource">Parent source identity, unused by the flat embedded-name policy.</param>
    /// <returns>The file-name identity and complete embedded shader text.</returns>
    /// <exception cref="FileNotFoundException">No embedded shader has the requested file name.</exception>
    public ShaderSource Load(string name, string? includingSource)
    {
        var fileName = Path.GetFileName(name);
        return new ShaderSource(fileName, GpuShaderCompiler12.ReadSource(fileName));
    }
}
