using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     A typed view of the FO3-era Bethesda shader properties at BS 34 and below (nif.xml:13966-14290): the NiShadeProperty
///     Flags, the BSShaderProperty run (Shader Type, Shader Flags, Shader Flags 2, Environment Map Scale), the
///     BSShaderLightingProperty Texture Clamp Mode (absent on TallGrassShaderProperty and WaterShaderProperty, which
///     inherit BSShaderProperty directly), and the subclass fields: the PPLighting texture set, refraction and parallax
///     values; the NoLighting, Sky, Tile and TallGrass File Name; the NoLighting falloff (above BS 26); the sky object
///     type.
/// </summary>
/// <remarks>
///     Flag members used by the reader (BSShaderFlags, nif.xml:13767-13871; BSShaderFlags2, nif.xml:13873-13964):
///     SF1 bit 0 Specular, bit 3 Vertex_Alpha, bit 31 ZBuffer_Test; SF2 bit 0 ZBuffer_Write, bit 5 Vertex_Colors.
/// </remarks>
internal sealed class NifShaderPropertyView
{
    /// <summary>The per-pixel lighting shaders whose texture set slots 0-5 become layers.</summary>
    public const string PerPixelLightingType = "BSShaderPPLightingProperty";

    /// <summary>The Lighting 3.0 shader, a BSShaderPPLightingProperty subclass.</summary>
    public const string Lighting30Type = "Lighting30ShaderProperty";

    /// <summary>The unlit shader.</summary>
    public const string NoLightingType = "BSShaderNoLightingProperty";

    /// <summary>The sky shader.</summary>
    public const string SkyType = "SkyShaderProperty";

    /// <summary>The tiled shader.</summary>
    public const string TileType = "TileShaderProperty";

    /// <summary>The tall grass shader.</summary>
    public const string TallGrassType = "TallGrassShaderProperty";

    /// <summary>The water shader (no typed vocabulary).</summary>
    public const string WaterType = "WaterShaderProperty";

    private NifShaderPropertyView(string type)
    {
        Type = type;
    }

    /// <summary>The block type.</summary>
    public string Type { get; }

    /// <summary>NiShadeProperty Flags (ShadeFlags), when decoded.</summary>
    public ushort? ShadeFlags { get; private init; }

    /// <summary>Shader Type (BSShaderType).</summary>
    public uint ShaderType { get; private init; }

    /// <summary>Shader Flags (BSShaderFlags, SF1).</summary>
    public uint ShaderFlags1 { get; private init; }

    /// <summary>Shader Flags 2 (BSShaderFlags2, SF2).</summary>
    public uint ShaderFlags2 { get; private init; }

    /// <summary>Environment Map Scale, exactly as stored.</summary>
    public float EnvironmentMapScale { get; private init; }

    /// <summary>Texture Clamp Mode (TexClampMode), or null for shaders without it.</summary>
    public uint? TextureClampMode { get; private init; }

    /// <summary>The PPLighting Texture Set link (-1 for none), or null for other shaders.</summary>
    public int? TextureSet { get; private init; }

    /// <summary>The inline File Name (NoLighting, Sky, Tile, TallGrass), or null.</summary>
    public NifSizedStringValue? FileName { get; private init; }

    /// <summary>Falloff Start Angle (a cosine), present on NoLighting above BS 26.</summary>
    public float? FalloffStartAngle { get; private init; }

    /// <summary>Falloff Stop Angle (a cosine).</summary>
    public float? FalloffStopAngle { get; private init; }

    /// <summary>Falloff Start Opacity.</summary>
    public float? FalloffStartOpacity { get; private init; }

    /// <summary>Falloff Stop Opacity.</summary>
    public float? FalloffStopOpacity { get; private init; }

    /// <summary>Sky Object Type (SkyShaderProperty).</summary>
    public uint? SkyObjectType { get; private init; }

    /// <summary>SF1 bit 0 Specular.</summary>
    public bool Specular => (ShaderFlags1 & 0x1u) != 0;

    /// <summary>SF1 bit 3 Vertex_Alpha.</summary>
    public bool VertexAlpha => (ShaderFlags1 & 0x8u) != 0;

    /// <summary>SF1 bit 31 ZBuffer_Test.</summary>
    public bool DepthTest => (ShaderFlags1 & 0x8000_0000u) != 0;

    /// <summary>SF2 bit 0 ZBuffer_Write.</summary>
    public bool DepthWrite => (ShaderFlags2 & 0x1u) != 0;

    /// <summary>SF2 bit 5 Vertex_Colors.</summary>
    public bool VertexColors => (ShaderFlags2 & 0x20u) != 0;

    /// <summary>True for PPLighting and Lighting30 (the texture-set shaders).</summary>
    public bool IsPerPixelLighting => TextureSet is not null;

    /// <summary>True when all four falloff fields were decoded.</summary>
    public bool HasFalloff => FalloffStartAngle is not null && FalloffStopAngle is not null &&
                              FalloffStartOpacity is not null && FalloffStopOpacity is not null;

    /// <summary>
    ///     True for the shader types the reader types: PPLighting, Lighting30, NoLighting, Sky, Tile and TallGrass.
    /// </summary>
    public static bool IsTypedShader(string type)
    {
        return type is PerPixelLightingType or Lighting30Type or NoLightingType or SkyType or TileType or TallGrassType;
    }

    /// <summary>Reads the view of one of <see cref="IsTypedShader" />'s types.</summary>
    public static NifShaderPropertyView Read(NifDecodedBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var root = block.Root;
        var hasTextureSet = block.Type is PerPixelLightingType or Lighting30Type;
        return new NifShaderPropertyView(block.Type)
        {
            ShadeFlags = NifModelPropertyFields.OptionalInteger(root, "Flags") is { } shade
                ? (ushort)shade.RawBits
                : null,
            ShaderType = (uint)NifModelPropertyFields.Integer(block, root, "Shader Type").RawBits,
            ShaderFlags1 = (uint)NifModelPropertyFields.Integer(block, root, "Shader Flags").RawBits,
            ShaderFlags2 = (uint)NifModelPropertyFields.Integer(block, root, "Shader Flags 2").RawBits,
            EnvironmentMapScale = NifModelPropertyFields.Float(block, root, "Environment Map Scale"),
            TextureClampMode = NifModelPropertyFields.OptionalInteger(root, "Texture Clamp Mode") is { } clamp
                ? (uint)clamp.RawBits
                : null,
            TextureSet = hasTextureSet ? NifModelPropertyFields.Ref(block, root, "Texture Set") : null,
            FileName = NifModelPropertyFields.OptionalSizedString(block, root, "File Name"),
            FalloffStartAngle = NifModelPropertyFields.OptionalFloat(block, root, "Falloff Start Angle"),
            FalloffStopAngle = NifModelPropertyFields.OptionalFloat(block, root, "Falloff Stop Angle"),
            FalloffStartOpacity = NifModelPropertyFields.OptionalFloat(block, root, "Falloff Start Opacity"),
            FalloffStopOpacity = NifModelPropertyFields.OptionalFloat(block, root, "Falloff Stop Opacity"),
            SkyObjectType = NifModelPropertyFields.OptionalInteger(root, "Sky Object Type") is { } sky
                ? (uint)sky.RawBits
                : null
        };
    }

    /// <summary>The decoded members for native state.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["type"] = Type,
            ["shadeFlags"] = ShadeFlags,
            ["shaderType"] = ShaderType,
            ["shaderFlags1"] = ShaderFlags1,
            ["shaderFlags2"] = ShaderFlags2,
            ["sf1Specular"] = Specular,
            ["sf1VertexAlpha"] = VertexAlpha,
            ["sf1ZBufferTest"] = DepthTest,
            ["sf2ZBufferWrite"] = DepthWrite,
            ["sf2VertexColors"] = VertexColors,
            ["environmentMapScale"] = NifModelNativeValues.Float(EnvironmentMapScale),
            ["textureClampMode"] = TextureClampMode
        };
        if (TextureSet is { } set)
        {
            json["textureSet"] = set;
        }

        if (FileName is { } file)
        {
            json["fileName"] = NifModelNativeValues.Text(file.RawBytes.Span);
        }

        if (HasFalloff)
        {
            json["falloff"] = new JsonArray(NifModelNativeValues.Float(FalloffStartAngle!.Value),
                NifModelNativeValues.Float(FalloffStopAngle!.Value),
                NifModelNativeValues.Float(FalloffStartOpacity!.Value),
                NifModelNativeValues.Float(FalloffStopOpacity!.Value));
        }

        if (SkyObjectType is { } sky)
        {
            json["skyObjectType"] = sky;
        }

        return json;
    }
}
