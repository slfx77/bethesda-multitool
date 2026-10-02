namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     The fields an NiTextureTransformController stores after its NiSingleInterpController header (nif.xml: Shader Map
///     bool, Texture Slot TexType, Operation TransformMember), exactly as stored. Read by
///     <see cref="NifPropertyControllerReader.TryReadTextureTransformView" />.
/// </summary>
/// <param name="ShaderMapByte">The stored Shader Map byte (nonzero means the target map is one of the Shader Textures).</param>
/// <param name="TextureSlot">The stored Texture Slot word (nif.xml TexType: 0 BASE_MAP .. 11 DECAL_3_MAP).</param>
/// <param name="Operation">The stored Operation word (nif.xml TransformMember: 0 TRANSLATE_U .. 4 SCALE_V).</param>
internal readonly record struct NifTextureTransformControllerView(byte ShaderMapByte, uint TextureSlot, uint Operation)
{
    /// <summary>True when the controller targets one of the property's Shader Textures rather than a named map.</summary>
    public bool ShaderMap => ShaderMapByte != 0;
}
