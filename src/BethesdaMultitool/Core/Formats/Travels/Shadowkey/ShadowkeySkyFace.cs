namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     One triangle of a Shadowkey <c>.zsk</c> sky mesh: three vertex indices then three corner
///     indices, six little-endian u16 in that order. Verified on all 21 retail files — every index
///     is in range and every vertex and every corner is referenced by at least one face, which is
///     also what pins the vertex block to +14 rather than +12 or +16 (at any other origin the
///     6-word records rotate and the vertex words run past the vertex count).
/// </summary>
internal readonly record struct ShadowkeySkyFace(
    ushort V0,
    ushort V1,
    ushort V2,
    ushort C0,
    ushort C1,
    ushort C2);
