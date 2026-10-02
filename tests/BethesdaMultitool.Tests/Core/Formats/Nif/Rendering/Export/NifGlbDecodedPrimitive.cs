namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>One drawn occurrence of a decoded glTF primitive: its identity, material facts and pairing counts.</summary>
internal sealed class NifGlbDecodedPrimitive
{
    /// <summary>The logical mesh index in the GLB.</summary>
    internal required int MeshIndex { get; init; }

    /// <summary>The primitive's index inside its logical mesh.</summary>
    internal required int PrimitiveIndex { get; init; }

    /// <summary>The logical mesh name, when it has one.</summary>
    internal required string? MeshName { get; init; }

    /// <summary>The content signature of the bound material, from <see cref="NifGlbMaterialSignature" />.</summary>
    internal required string MaterialSignature { get; init; }

    /// <summary>Whether the bound material's base-color channel has a texture.</summary>
    internal required bool HasBaseColorTexture { get; init; }

    /// <summary>Whether the bound material's normal channel has a texture.</summary>
    internal required bool HasNormalTexture { get; init; }

    /// <summary>The bound material's glTF alpha mode name.</summary>
    internal required string AlphaMode { get; init; }

    /// <summary>Whether the bound material is double-sided.</summary>
    internal required bool DoubleSided { get; init; }

    /// <summary>Whether the bound material is lit and emits through a texture or a nonzero factor.</summary>
    internal required bool LitEmissive { get; init; }

    /// <summary>Whether the primitive carries a TANGENT accessor.</summary>
    internal required bool HasTangents { get; init; }

    /// <summary>The COLOR_0 accessor's component type and normalization, or <c>absent</c>.</summary>
    internal required string ColorAccessor { get; init; }

    /// <summary>Whether any decoded COLOR_0 value differs from opaque white.</summary>
    internal required bool VertexColored { get; init; }

    /// <summary>The owning node's depth below its scene root; a scene root is depth zero.</summary>
    internal required int NodeDepth { get; init; }

    /// <summary>How many of this occurrence's triangles were paired with the other GLB.</summary>
    internal int MatchedTriangles { get; set; }

    /// <summary>How many of this occurrence's triangles were removed as exactly repeated-position triangles.</summary>
    internal int RemovedTriangles { get; set; }
}
