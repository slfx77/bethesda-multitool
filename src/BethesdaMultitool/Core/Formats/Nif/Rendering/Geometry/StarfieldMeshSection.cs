namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;

/// <summary>
///     One field or count-prefixed section of a Starfield <c>.mesh</c> stream, as <see cref="StarfieldMeshFile.Decode" />
///     walked it: its name, where it starts, how many bytes it spans (the count dword included) and how many elements it
///     declares. The list a parse records tiles the stream from byte 0 to the last byte it consumed, in stream order.
/// </summary>
/// <param name="Name">
///     <c>version</c>, <c>indices</c>, <c>scale</c>, <c>weightsPerVertex</c>, <c>positions</c>, <c>uv0</c>, <c>uv1</c>,
///     <c>colors</c>, <c>normals</c>, <c>tangents</c>, <c>weights</c>, <c>lods</c> (the LOD count and every list, version
///     1 and above only), <c>lod:k</c> for LOD list k counted from 1, <c>meshlets</c> and <c>cull</c> (the optional tail).
/// </param>
/// <param name="Offset">The byte offset of the section's first byte (its count dword for a count-prefixed section).</param>
/// <param name="Length">The bytes the section spans, the count dword included.</param>
/// <param name="Count">
///     The element count the section declares (indices, vertices, UV pairs, colors, packed vectors, weight pairs, LOD
///     lists, a LOD list's indices, meshlets, cull records); 1 for a fixed field (<c>version</c>, <c>scale</c>,
///     <c>weightsPerVertex</c>).
/// </param>
internal sealed record StarfieldMeshSection(string Name, int Offset, int Length, uint Count);
