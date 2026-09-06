namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     One vertex of a Shadowkey <c>.zsk</c> sky mesh: three little-endian i16 coordinates in the
///     engine's integer units. The 12 outdoor zones share a 30-vertex dome whose bounding box is
///     x -252..254, y -290..40, z -254..252; the 9 interior zones share a 98-vertex closed box,
///     x +/-37, y -165..-90, z +/-37. Units relative to the cell grid are unresolved (the dome
///     radius is about 254).
/// </summary>
internal readonly record struct ShadowkeySkyVertex(short X, short Y, short Z);
