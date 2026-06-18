using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Collision;

/// <summary>
///     A decoded Havok collision mesh as a flat triangle soup, in the NIF's root-local
///     (<c>treatRootsAsIdentity</c>) frame — the same coordinate space the visual mesh's collision
///     soup uses, so the walk-mode raycast can apply the placement world matrix identically.
///     <see cref="Positions" /> are world-unit vertex positions (Havok units already scaled ×7 and
///     placed by the collision object's node transform); <see cref="Triangles" /> are index triples
///     into <see cref="Positions" /> (length is a multiple of 3).
/// </summary>
internal readonly record struct HavokTriangleSoup(Vector3[] Positions, int[] Triangles);
