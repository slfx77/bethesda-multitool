namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>
///     Pure upload-time self-check for the mesh cache's submesh packing: every draw binds a
///     per-submesh VBV/IBV window with <c>BaseVertexLocation = 0</c>, so each submesh's indices must
///     be LOCAL (reference only its own vertex window) and every window must sit inside the mesh's
///     arena allocation at the expected stride. A violation here is a deterministic packer/decoder
///     defect — an out-of-window index reads a neighboring submesh's floats as positions, producing
///     giant stretched triangles. Device-independent so tests cover it with plain spans.
/// </summary>
internal static class MeshPackingValidator12
{
    /// <summary>Returns a violation description, or null when the submesh's packed windows are
    /// internally consistent.</summary>
    public static string? ValidateSubmeshWindow(
        int submeshIndex,
        int vertexCount,
        ReadOnlySpan<ushort> indices,
        long vertexByteOffset,
        long vertexByteSize,
        long indexByteOffset,
        long indexByteSize,
        uint stride,
        uint allocationVertexBytes,
        uint allocationIndexBytes)
    {
        if (stride == 0)
        {
            return $"submesh {submeshIndex}: zero vertex stride";
        }

        if (vertexByteSize != (long)vertexCount * stride)
        {
            return $"submesh {submeshIndex}: vertex window {vertexByteSize} B != " +
                   $"{vertexCount} vertices × stride {stride}";
        }

        if (indexByteSize != (long)indices.Length * sizeof(ushort))
        {
            return $"submesh {submeshIndex}: index window {indexByteSize} B != " +
                   $"{indices.Length} R16 indices";
        }

        if (vertexByteOffset < 0 || vertexByteOffset + vertexByteSize > allocationVertexBytes)
        {
            return $"submesh {submeshIndex}: vertex window [{vertexByteOffset}, " +
                   $"{vertexByteOffset + vertexByteSize}) exceeds allocation ({allocationVertexBytes} B)";
        }

        if (indexByteOffset < 0 || indexByteOffset + indexByteSize > allocationIndexBytes)
        {
            return $"submesh {submeshIndex}: index window [{indexByteOffset}, " +
                   $"{indexByteOffset + indexByteSize}) exceeds allocation ({allocationIndexBytes} B)";
        }

        for (var i = 0; i < indices.Length; i++)
        {
            if (indices[i] >= vertexCount)
            {
                return $"submesh {submeshIndex}: index[{i}] = {indices[i]} outside its own " +
                       $"vertex window ({vertexCount} vertices; BaseVertexLocation is 0)";
            }
        }

        return null;
    }
}
