namespace BethesdaMultitool.Core.Formats.Esm.Models.World;

/// <summary>
///     LAND texture layer kind from BTXT/ATXT subrecords.
/// </summary>
public enum LandTextureLayerKind
{
    /// <summary>Base texture layer (BTXT).</summary>
    Base,

    /// <summary>Alpha/blended texture layer (ATXT).</summary>
    Alpha
}

/// <summary>
///     LAND VTXT blend entry associated with the preceding ATXT layer.
/// </summary>
public record LandTextureBlendEntry(
    ushort Position,
    byte Unused0,
    byte Unused1,
    float Opacity);

/// <summary>
///     Texture layer information from ATXT/BTXT subrecords.
/// </summary>
public record LandTextureLayer
{
    /// <summary>
    ///     Classic VTXT quadrant grid edge: positions are <c>qy*17 + qx</c> on a 17×17 vertex grid.
    ///     Every layer parsed from a real LAND record uses this convention.
    /// </summary>
    public const int VtxtGridEdge = 17;

    public required LandTextureLayerKind Kind { get; init; }

    public uint TextureFormId { get; init; }

    public byte Quadrant { get; init; }

    public byte PlatformFlag { get; init; }

    public ushort Layer { get; init; }

    public List<LandTextureBlendEntry> BlendEntries { get; init; } = [];

    /// <summary>
    ///     Vertex-grid edge that <see cref="LandTextureBlendEntry.Position" /> is encoded on
    ///     (<c>Position = qy * BlendGridEdge + qx</c>, qy growing north from the quadrant's SW corner).
    ///     Defaults to the classic 17×17 VTXT convention; the BTD terrain injector emits Starfield
    ///     layers at the native 65 (64 alpha-map pixels per quadrant + the shared edge) so the 129-grid
    ///     renderer keeps full blend fidelity. Consumers that operate on the classic 17 grid must read
    ///     entries through <see cref="EnumerateVtxt17Entries" /> rather than assuming the convention.
    /// </summary>
    public int BlendGridEdge { get; init; } = VtxtGridEdge;

    public long Offset { get; init; }

    public string SubrecordSignature => Kind == LandTextureLayerKind.Base ? "BTXT" : "ATXT";

    /// <summary>
    ///     Blend entries projected onto the classic 17×17 VTXT grid. Identity for classic layers; for a
    ///     finer grid (e.g. the injector's 65) only lattice positions that coincide with 17-grid
    ///     vertices survive (stride <c>(BlendGridEdge-1)/16</c>), which is exactly the classic sampling
    ///     of the same map. A grid edge that is not <c>16n+1</c> yields nothing rather than garbage.
    /// </summary>
    public IEnumerable<LandTextureBlendEntry> EnumerateVtxt17Entries()
    {
        if (BlendGridEdge == VtxtGridEdge)
        {
            return BlendEntries;
        }

        var stride = (BlendGridEdge - 1) / (VtxtGridEdge - 1);
        if (stride < 1 || (BlendGridEdge - 1) % (VtxtGridEdge - 1) != 0)
        {
            return [];
        }

        return Project(BlendEntries, BlendGridEdge, stride);

        static IEnumerable<LandTextureBlendEntry> Project(
            List<LandTextureBlendEntry> entries, int edge, int stride)
        {
            foreach (var entry in entries)
            {
                var sx = entry.Position % edge;
                var sy = entry.Position / edge;
                if (sy >= edge || sx % stride != 0 || sy % stride != 0)
                {
                    continue;
                }

                yield return entry with { Position = (ushort)(((sy / stride) * VtxtGridEdge) + (sx / stride)) };
            }
        }
    }
}
