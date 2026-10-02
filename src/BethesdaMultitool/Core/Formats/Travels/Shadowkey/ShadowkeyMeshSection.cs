namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     One section of a walked mesh record, in stream order, as <see cref="ShadowkeyMesh.Parse" /> found it: the names
///     are <c>header</c>, <c>positions</c>, <c>uvs</c>, <c>faces</c>, <c>texture-header</c>, <c>texels</c>,
///     <c>sequence-count</c> and <c>sequences</c>, and consecutive sections tile the record to its last byte.
/// </summary>
/// <param name="Name">The section name.</param>
/// <param name="Offset">The byte offset of the section inside the record.</param>
/// <param name="Length">The section's byte length (zero for an empty block).</param>
internal readonly record struct ShadowkeyMeshSection(string Name, int Offset, int Length);
