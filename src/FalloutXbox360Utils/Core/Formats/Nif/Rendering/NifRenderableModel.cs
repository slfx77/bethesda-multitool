namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     All renderable geometry extracted from a single NIF file,
///     with node transforms applied so all positions are in model space.
/// </summary>
internal sealed class NifRenderableModel
{
    public List<RenderableSubmesh> Submeshes { get; } = [];
    public float MinX { get; set; } = float.MaxValue;
    public float MinY { get; set; } = float.MaxValue;
    public float MinZ { get; set; } = float.MaxValue;
    public float MaxX { get; set; } = float.MinValue;
    public float MaxY { get; set; } = float.MinValue;
    public float MaxZ { get; set; } = float.MinValue;

    public float Width => MaxX - MinX;
    public float Height => MaxY - MinY;
    public float Depth => MaxZ - MinZ;
    public bool HasGeometry => Submeshes.Count > 0;

    /// <summary>True if at least one submesh was skinned (had skin data + external bone transforms).</summary>
    public bool WasSkinned { get; set; }

    /// <summary>
    ///     Update bounding box from a submesh's positions.
    /// </summary>
    public void ExpandBounds(float[] positions)
    {
        for (var i = 0; i < positions.Length; i += 3)
        {
            var x = positions[i];
            var y = positions[i + 1];
            var z = positions[i + 2];
            if (x < MinX) MinX = x;
            if (y < MinY) MinY = y;
            if (z < MinZ) MinZ = z;
            if (x > MaxX) MaxX = x;
            if (y > MaxY) MaxY = y;
            if (z > MaxZ) MaxZ = z;
        }
    }
}