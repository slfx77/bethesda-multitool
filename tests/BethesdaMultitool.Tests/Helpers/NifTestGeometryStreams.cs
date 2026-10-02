namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     The NiGeometryData streams of a hand-laid fixture, written by <see cref="NifTestBlockLayouts.GeometryData" />.
///     Arrays are flat and element-major (xyz, rgba, uv); every array present must hold exactly one element per vertex.
///     Nothing here consults NifSchema.
/// </summary>
internal sealed class NifTestGeometryStreams
{
    /// <summary>Positions, three floats per vertex.</summary>
    public required float[] Vertices { get; init; }

    /// <summary>
    ///     Num Vertices as written; defaults to <c>Vertices.Length / 3</c>. Set it with <see cref="HasVertices" /> false
    ///     to lay out a block that declares vertices but stores none (the console packed form).
    /// </summary>
    public ushort? NumVertices { get; init; }

    /// <summary>Has Vertices; when false no positions are written.</summary>
    public bool HasVertices { get; init; } = true;

    /// <summary>Normals, three floats per vertex, or null for Has Normals = 0.</summary>
    public float[]? Normals { get; init; }

    /// <summary>
    ///     Tangents (nif.xml name), three floats per vertex; in retail files the array that runs along +dP/dv. Setting
    ///     them sets BS Data Flags bit 12; they are written (with <see cref="Bitangents" />) only when normals are
    ///     present, as nif.xml's condition requires.
    /// </summary>
    public float[]? Tangents { get; init; }

    /// <summary>
    ///     Bitangents (nif.xml name), three floats per vertex (required with <see cref="Tangents" />); in retail files the
    ///     array that runs along +dP/du, which the reader types as the glTF tangent.
    /// </summary>
    public float[]? Bitangents { get; init; }

    /// <summary>Vertex colors as float values, four per vertex, or null for Has Vertex Colors = 0.</summary>
    public float[]? Colors { get; init; }

    /// <summary>Vertex colors as exact IEEE bits (NaN payloads), four per vertex; overrides <see cref="Colors" />.</summary>
    public uint[]? ColorBits { get; init; }

    /// <summary>UV set 0, two floats per vertex; setting it sets BS Data Flags bit 0.</summary>
    public float[]? Uvs { get; init; }

    /// <summary>UV set 0 as exact IEEE bits (NaN payloads), two per vertex; overrides <see cref="Uvs" />.</summary>
    public uint[]? UvBits { get; init; }

    /// <summary>The Additional Data link (-1 for none).</summary>
    public int AdditionalData { get; init; } = -1;

    /// <summary>The Bounding Sphere: center xyz then radius.</summary>
    public float[] BoundingSphere { get; init; } = [0f, 0f, 0f, 1f];

    /// <summary>The Consistency Flags (CT_STATIC by default).</summary>
    public ushort ConsistencyFlags { get; init; } = 0x4000;

    /// <summary>The Num Vertices value written.</summary>
    public ushort VertexCount => NumVertices ?? (ushort)(Vertices.Length / 3);

    /// <summary>The BS Data Flags value written: bit 0 for UV set 0, bit 12 for tangents.</summary>
    public ushort BsDataFlags =>
        (ushort)((Uvs is null && UvBits is null ? 0 : 0x0001) | (Tangents is null ? 0 : 0x1000));
}
