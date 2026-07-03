namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     Shader and texture-slot metadata resolved from a NIF shader property block.
/// </summary>
internal sealed class NifShaderTextureMetadata
{
    public string PropertyType { get; init; } = "";
    public uint? ShaderType { get; init; }
    public uint? ShaderFlags { get; init; }
    public uint? ShaderFlags2 { get; init; }
    public float? EnvMapScale { get; init; }
    public IReadOnlyList<string?> TextureSlots { get; init; } = [];

    /// <summary>
    ///     FO4/FO76 external material path (<c>materials\….bgsm</c>/<c>.bgem</c>) from the shader's Name,
    ///     populated even when the inline texture set supplied a diffuse — the engine gives the material's
    ///     render state (alpha test/blend, two-sided, specular) priority over the NIF's inline properties.
    /// </summary>
    public string? MaterialPath { get; init; }

    public string? DiffusePath => GetTextureSlot(0);
    public string? NormalMapPath => GetTextureSlot(1);
    public string? GlowMapPath => GetTextureSlot(2);
    public string? HeightMapPath => GetTextureSlot(3);
    public string? EnvironmentMapPath => GetTextureSlot(4);
    public string? EnvironmentMaskPath => GetTextureSlot(5);

    public bool HasRemappableTextures =>
        ShaderFlags.HasValue && (ShaderFlags.Value & (1u << 25)) != 0;

    public string? GetTextureSlot(int index)
    {
        return index >= 0 && index < TextureSlots.Count
            ? TextureSlots[index]
            : null;
    }
}
