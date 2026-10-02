using System.Numerics;
using System.Text.Json.Nodes;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>Identifies one generated neutral skin placement separately from the retained source-node hierarchy.</summary>
/// <param name="MeshPartOrdinal">The original unfiltered mesh-part ordinal, independent of duplicate names or objects.</param>
/// <param name="Part">The original source occurrence whose buffers and optional node identity remain unmodified.</param>
/// <param name="MeshIndex">The independently emitted one-primitive mesh.</param>
/// <param name="SkinIndex">The exact palette and inverse binds belonging to this occurrence.</param>
internal readonly record struct NifNeutralSkinPlacement(int MeshPartOrdinal, GlbMeshPart Part, int MeshIndex, int SkinIndex)
{
    /// <summary>Builds an identity-transform placement with explicit source provenance, without inventing a source node.</summary>
    /// <returns>A neutral node whose skin supplies world placement, matching the legacy writer's independent skin instance.</returns>
    /// <remarks>The node label is presentation only. The scoped original part ordinal supplies stable generated identity;
    /// source-node and block references are optional provenance, not additional mesh transforms.</remarks>
    internal SceneNode CreateNode()
    {
        var provenance = new JsonObject
        {
            ["meshPartOrdinal"] = MeshPartOrdinal,
            ["sourceNodeIndex"] = Part.NodeIndex,
            ["sourceBlockIndex"] = Part.Submesh.SourceBlockIndex >= 0 ? (int?)Part.Submesh.SourceBlockIndex : null
        };
        var extras = new JsonObject { ["bethesdaNifSkinPlacement"] = provenance };
        return new SceneNode(Part.Name, Matrix4x4.Identity, meshIndex: MeshIndex,
            extrasJson: extras.ToJsonString(), skinIndex: SkinIndex);
    }
}
