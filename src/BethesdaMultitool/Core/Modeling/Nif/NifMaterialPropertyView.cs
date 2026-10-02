using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     A typed view of NiMaterialProperty at 20.2.0.7 (nif.xml:10873-10906). Field presence follows the decoder, which
///     follows nif.xml: Ambient Color and Diffuse Color exist below BS 26, Emissive Mult above BS 21 (vercond
///     <c>#BSVER# #GT# 21</c>, not BMT's legacy <c>&gt; 26</c>). Values are exactly as stored, possibly non-finite; the
///     material reader decides what a non-finite value means.
/// </summary>
internal sealed class NifMaterialPropertyView
{
    private NifMaterialPropertyView(Vector3? ambient, Vector3? diffuse, Vector3 specular, Vector3 emissive,
        float glossiness, float alpha, float? emissiveMultiplier)
    {
        AmbientColor = ambient;
        DiffuseColor = diffuse;
        SpecularColor = specular;
        EmissiveColor = emissive;
        Glossiness = glossiness;
        Alpha = alpha;
        EmissiveMultiplier = emissiveMultiplier;
    }

    /// <summary>Ambient Color, present below BS 26.</summary>
    public Vector3? AmbientColor { get; }

    /// <summary>Diffuse Color, present below BS 26.</summary>
    public Vector3? DiffuseColor { get; }

    /// <summary>Specular Color.</summary>
    public Vector3 SpecularColor { get; }

    /// <summary>Emissive Color, never multiplied by <see cref="EmissiveMultiplier" />.</summary>
    public Vector3 EmissiveColor { get; }

    /// <summary>Glossiness.</summary>
    public float Glossiness { get; }

    /// <summary>Alpha.</summary>
    public float Alpha { get; }

    /// <summary>Emissive Mult, present above BS 21.</summary>
    public float? EmissiveMultiplier { get; }

    /// <summary>Reads the view.</summary>
    public static NifMaterialPropertyView Read(NifDecodedBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var root = block.Root;
        return new NifMaterialPropertyView(
            NifModelPropertyFields.OptionalColor3(block, root, "Ambient Color"),
            NifModelPropertyFields.OptionalColor3(block, root, "Diffuse Color"),
            NifModelPropertyFields.Color3(block, root, "Specular Color"),
            NifModelPropertyFields.Color3(block, root, "Emissive Color"),
            NifModelPropertyFields.Float(block, root, "Glossiness"),
            NifModelPropertyFields.Float(block, root, "Alpha"),
            NifModelPropertyFields.OptionalFloat(block, root, "Emissive Mult"));
    }

    /// <summary>The stored values for native state (non-finite floats as their IEEE bits).</summary>
    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["ambientColor"] = AmbientColor is { } ambient ? Color(ambient) : null,
            ["diffuseColor"] = DiffuseColor is { } diffuse ? Color(diffuse) : null,
            ["specularColor"] = Color(SpecularColor),
            ["emissiveColor"] = Color(EmissiveColor),
            ["glossiness"] = NifModelNativeValues.Float(Glossiness),
            ["alpha"] = NifModelNativeValues.Float(Alpha),
            ["emissiveMult"] = EmissiveMultiplier is { } mult ? NifModelNativeValues.Float(mult) : null
        };
    }

    private static JsonArray Color(Vector3 value)
    {
        return new JsonArray(NifModelNativeValues.Float(value.X), NifModelNativeValues.Float(value.Y),
            NifModelNativeValues.Float(value.Z));
    }
}
