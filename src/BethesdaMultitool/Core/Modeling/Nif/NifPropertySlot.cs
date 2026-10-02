namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The Gamebryo property type a NiProperty block fills in a geometry's effective property state (plan section 3,
///     "Materials and properties"). Each placed geometry sees at most one property per slot: the nearest one on its path
///     to the root. Every BSShaderProperty subclass fills <see cref="Shade" />, because BSShaderProperty inherits
///     NiShadeProperty (nif.xml:13966-13980), so a shader on a shape replaces one inherited from a node.
/// </summary>
internal enum NifPropertySlot
{
    /// <summary>NiAlphaProperty: blending, alpha test and sorting.</summary>
    Alpha,

    /// <summary>NiMaterialProperty: colors, glossiness, alpha and emission.</summary>
    Material,

    /// <summary>NiShadeProperty and every BSShaderProperty subclass (the Bethesda shaders, water included).</summary>
    Shade,

    /// <summary>NiStencilProperty: stencil test and face draw mode.</summary>
    Stencil,

    /// <summary>NiTexturingProperty: the fixed-function texture maps (FO3 effects).</summary>
    Texturing,

    /// <summary>NiVertexColorProperty: vertex-color routing.</summary>
    VertexColor,

    /// <summary>NiZBufferProperty: depth test, write and comparison.</summary>
    ZBuffer,

    /// <summary>NiFogProperty (no typed vocabulary).</summary>
    Fog,

    /// <summary>NiSpecularProperty (not typed in cut 1a).</summary>
    Specular,

    /// <summary>NiWireframeProperty (not typed in cut 1a).</summary>
    Wireframe,

    /// <summary>NiDitherProperty (not typed in cut 1a).</summary>
    Dither,

    /// <summary>Any other NiProperty subclass; each such type is its own slot, keyed by its type name.</summary>
    Other
}
