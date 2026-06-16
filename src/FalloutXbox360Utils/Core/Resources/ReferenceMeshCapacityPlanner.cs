namespace FalloutXbox360Utils.Core.Resources;

/// <summary>
///     Plans the resident-mesh LRU capacity for the 3D viewer's reference cache from a worldspace's
///     distinct-mesh count, so the whole working set stays resident instead of thrashing the cache.
///     Pure + GUI-free so it is unit-testable (the renderer/cache that consume it are
///     <c>#if WINDOWS_GUI</c>). See plan: auto-size the reference mesh cache.
/// </summary>
internal static class ReferenceMeshCapacityPlanner
{
    /// <summary>Never size below the shipped default — small worldspaces keep current behavior.</summary>
    public const int FloorCapacity = 2048;

    /// <summary>Slack over the counted unique meshes: refs whose model resolves after load
    /// (persistent-cell refs redistributed by position, late MODL resolution) + path-normalization
    /// rounding. Cheap — residency is VRAM-bounded, and the geometry arena grows on demand.</summary>
    public const int Headroom = 512;

    /// <summary>Upper bound. Above game-wide ~7,431 distinct REFR'd models so any single worldspace
    /// fits with margin, while bounding a corrupt/oversized input. The cap itself costs trivial RAM;
    /// resident GPU meshes are paced by the per-frame upload budget and bounded by physical VRAM.</summary>
    public const int CeilingCapacity = 12_288;

    /// <summary>
    ///     <c>Clamp(max(0, uniqueMeshCount) + Headroom, FloorCapacity, CeilingCapacity)</c>.
    /// </summary>
    public static int Plan(int uniqueMeshCount)
    {
        if (uniqueMeshCount < 0)
        {
            uniqueMeshCount = 0;
        }

        return Math.Clamp(uniqueMeshCount + Headroom, FloorCapacity, CeilingCapacity);
    }
}
