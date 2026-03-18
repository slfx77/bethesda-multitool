namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Scene graph information for an extracted mesh, discovered by walking
///     the NiTriShape → NiNode parent chain.
/// </summary>
public record SceneGraphInfo
{
    /// <summary>Name of the NiTriShape node (e.g., "Tri Mesh 0", "PipBoy").</summary>
    public string? NodeName { get; init; }

    /// <summary>Names from parent chain, leaf-to-root order (e.g., ["Building01", "Scene Root"]).</summary>
    public required string[] ParentNames { get; init; }

    /// <summary>Virtual address of the root NiNode (for grouping meshes into models).</summary>
    public uint RootNodeVa { get; init; }

    /// <summary>World-space X position from NiAVObject.m_kWorld transform.</summary>
    public float WorldX { get; init; }

    /// <summary>World-space Y position from NiAVObject.m_kWorld transform.</summary>
    public float WorldY { get; init; }

    /// <summary>World-space Z position from NiAVObject.m_kWorld transform.</summary>
    public float WorldZ { get; init; }

    /// <summary>File offset of the NiTriShape struct.</summary>
    public long NiTriShapeFileOffset { get; init; }

    /// <summary>
    ///     The best display name: first parent with a .NIF path, first non-generic parent,
    ///     the node name, or the root name as fallback.
    /// </summary>
    public string? ModelName
    {
        get
        {
            // Prefer a parent that contains a .NIF path (indicates the model file)
            var nifParent = ParentNames.FirstOrDefault(p =>
                p.Contains(".NIF", StringComparison.OrdinalIgnoreCase));
            if (nifParent != null)
            {
                return nifParent;
            }

            // Skip generic engine nodes, find first meaningful name
            // ParentNames is leaf-to-root, so earlier entries are more specific
            return ParentNames.FirstOrDefault(p => !IsGenericNodeName(p)) ?? NodeName;
        }
    }

    /// <summary>
    ///     Full path from root to leaf (e.g., "Scene Root/Building01/Tri Mesh 0").
    /// </summary>
    public string FullPath
    {
        get
        {
            var parts = new List<string>();
            for (var i = ParentNames.Length - 1; i >= 0; i--)
            {
                parts.Add(ParentNames[i]);
            }

            if (NodeName != null)
            {
                parts.Add(NodeName);
            }

            return parts.Count > 0 ? string.Join("/", parts) : "(unnamed)";
        }
    }

    private static bool IsGenericNodeName(string name)
    {
        return name is "WorldRoot Node" or "MenuRoot Node" or "shadow scene node"
            or "ObjectLODRoot" or "StaticNode" or "Scene Root";
    }
}
