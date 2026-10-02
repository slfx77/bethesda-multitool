using System.Numerics;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     The source basis an XnGine document declares (cut-1c plan section 6.4): up +Y, forward +Z, right-handed,
///     normalized by the reader, Assumed. The reader negates Y on every position and normal and reverses every face's
///     corner order (<see cref="WindingRuleId" />), so the GLB writer needs no rotation and the Blender writer rotates
///     +90 degrees about X; declaring the raw Y-down basis instead would make Blender degrade it (it degrades any
///     left-handed basis).
/// </summary>
internal static class XnGineModelBasis
{
    /// <summary>The stable identity of the reader's winding rule: Y negated, face corners reversed.</summary>
    public const string WindingRuleId = "bmt.xngine.faces-reversed/1";

    /// <summary>The evidence the declaration carries.</summary>
    public const string Evidence =
        "XnGine stores Y down (the legacy export, XnGineMeshGlbExporter, negates Y on every vertex and swaps the " +
        "second and third corner of every triangle); the reader negated Y and reversed each face's corner order to " +
        "(c0, c(n-1), ..., c1) (rule " + WindingRuleId + ", triangles by " + XnGineTriangulation.RuleId +
        "); forward and chirality not established (the legacy reflection assumes a left-handed native frame; Daggerfall " +
        "Unity's Y negation into Unity's frame, recalled, would make it right-handed)";

    /// <summary>The declaration: up +Y, forward +Z, right-handed, normalized by the reader, Assumed.</summary>
    public static SceneSourceBasis Basis { get; } = new(Vector3.UnitY, Vector3.UnitZ, SceneHandedness.RightHanded,
        normalizedByReader: true, SceneValueProvenance.Assumed, Evidence);
}
