using Slfx77.Multitool.Core.Documents;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Starfield;

/// <summary>
///     The document diagnostics the Starfield <c>.mesh</c> reader raises (cut-2 plan section 3.5). Every message is raw
///     text; each code is raised at most once per document.
/// </summary>
internal static class StarfieldMeshModelDiagnostics
{
    /// <summary>Always raised: a lone <c>.mesh</c> carries no material; the referencing NIF names it.</summary>
    public const string MaterialInNif = "bmt.starfield.mesh.material-in-nif";

    /// <summary>Weights are present: the joint palette they index lives in the referencing NIF's BSSkin::Instance.</summary>
    public const string SkinPaletteInNif = "bmt.starfield.mesh.skin-palette-in-nif";

    /// <summary>Colors are present: whether they tint is decided by the referencing NIF's material (plan decision D4).</summary>
    public const string ColorInterpretation = "bmt.starfield.mesh.color-interpretation";

    /// <summary>A normal or tangent carries the Dec4 zero code (511, 511, 511), kept as decoded (plan decision D6).</summary>
    public const string Dec4Sentinel = "bmt.starfield.mesh.dec4-sentinel";

    /// <summary>A UV component is not finite: it reads 0 in the portable set and its bits are kept in a raw stream.</summary>
    public const string UvNonFinite = "bmt.starfield.mesh.uv-nonfinite";

    /// <summary>
    ///     The normals' W does not follow the tail rule's measured pairing (W 0 with the tail, a W other than the first
    ///     normal's, or a W outside 0 and 1); no retail file does.
    /// </summary>
    public const string TailWithoutNormalW = "bmt.starfield.mesh.tail-without-normal-w";

    /// <summary>A LOD index list is empty: its node carries no mesh.</summary>
    public const string EmptyLod = "bmt.starfield.mesh.lod-empty";

    /// <summary>The stream stores no UV0, normals or tangents (every retail file stores all three).</summary>
    public const string StreamAbsent = "bmt.starfield.mesh.stream-absent";

    /// <summary>Vertices no triangle of the main list references; they are kept (44 retail files have some).</summary>
    public const string UnusedVertices = "bmt.starfield.mesh.unused-vertices";

    /// <summary>A raw-text diagnostic.</summary>
    public static SceneDiagnostic Create(string code, string message)
    {
        return new SceneDiagnostic(code, DocumentText.Raw(message));
    }
}
