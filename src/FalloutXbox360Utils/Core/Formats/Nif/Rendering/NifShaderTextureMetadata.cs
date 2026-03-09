namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

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

    public string? DiffusePath => GetTextureSlot(0);
    public string? NormalMapPath => GetTextureSlot(1);

    public bool HasRemappableTextures =>
        ShaderFlags.HasValue && (ShaderFlags.Value & (1u << 25)) != 0;

    public string? GetTextureSlot(int index)
    {
        return index >= 0 && index < TextureSlots.Count
            ? TextureSlots[index]
            : null;
    }
}
