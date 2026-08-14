using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Collision;

/// <summary>
///     A decoded Havok collision mesh as a flat triangle soup, in the NIF's root-local
///     (<c>treatRootsAsIdentity</c>) frame — the same coordinate space the visual mesh's collision
///     soup uses, so the walk-mode raycast can apply the placement world matrix identically.
///     <see cref="Positions" /> are world-unit vertex positions (Havok units already scaled ×7 and
///     placed by the collision object's node transform); <see cref="Triangles" /> are index triples
///     into <see cref="Positions" /> (length is a multiple of 3).
/// </summary>
internal readonly record struct HavokTriangleSoup(Vector3[] Positions, int[] Triangles);

/// <summary>
///     Provenance of the collision verdict recovered from a NIF. <see cref="AbsentOrUnsupported" />
///     deliberately combines files with no collision object and files whose Havok shape/layout is not
///     decoded yet: both remain eligible for the existing visual-mesh fallback. Only an explicit
///     layer-15 body produces <see cref="AuthoredNoncollidable" />.
/// </summary>
internal enum HavokCollisionProvenance : byte
{
    AbsentOrUnsupported = 0,
    AuthoredNoncollidable = 1,
    AuthoredMesh = 2
}

/// <summary>A provenance-preserving Havok extraction result.</summary>
internal readonly record struct HavokCollisionExtractionResult(
    HavokCollisionProvenance Provenance,
    HavokTriangleSoup? Soup)
{
    public static HavokCollisionExtractionResult AbsentOrUnsupported =>
        new(HavokCollisionProvenance.AbsentOrUnsupported, null);

    public static HavokCollisionExtractionResult AuthoredNoncollidable =>
        new(HavokCollisionProvenance.AuthoredNoncollidable, null);

    public static HavokCollisionExtractionResult FromSoup(HavokTriangleSoup soup) =>
        new(HavokCollisionProvenance.AuthoredMesh, soup);
}
