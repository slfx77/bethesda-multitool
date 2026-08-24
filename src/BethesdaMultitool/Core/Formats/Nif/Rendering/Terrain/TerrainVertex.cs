using System.Numerics;
using System.Runtime.InteropServices;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;

/// <summary>
///     The terrain slot-0 vertex: <b>12 bytes</b>, against the 72 of the shared
///     <c>GpuMeshUploader.GpuVertex</c> it replaces.
///     <para>
///         Terrain used the shared mesh vertex for layout stability, and paid 60 bytes a vertex for
///         it. Three of its six fields were never read: <c>terrain_textured.vert.hlsl</c> derives its
///         UV from the world position rather than the texture coordinate, and the fragment shader's
///         <c>TerrainTangentToWorld</c> builds the tangent frame analytically from the geometric
///         normal — which is why <c>TerrainMeshBuilder</c> could write literal
///         <c>Vector3.Zero</c> tangents and bitangents without anything noticing. Those three fields
///         are <b>deleted</b> rather than packed; the two that remain are narrowed to formats finer
///         than the data going into them (see <see cref="TerrainNormalPacking" /> for the normal,
///         and the colour note below).
///     </para>
///     <para>
///         <b>Only the height is stored, not the position.</b> A LAND cell is a regular grid: X and Y
///         are <c>origin + index × spacing</c>, which the vertex shader rebuilds from
///         <c>SV_VertexID</c> and four root constants. Eight bytes a vertex to carry a number the GPU
///         can derive in two instructions is the most expensive kind of redundancy — it is paid on
///         every cell, in every pass, for the whole session. See <see cref="TerrainCellGrid" /> for
///         why the reconstruction is bit-exact rather than merely close, which is what keeps cell
///         seams watertight.
///     </para>
///     <para>
///         <b>The colour narrowing is bit-exact, not approximate.</b> LAND <c>VCLR</c> is three bytes
///         per vertex and the builder's only other colour is opaque white, so
///         <c>R8G8B8A8_UNORM</c> stores the source values themselves — the previous
///         <c>Vector4</c> was four floats holding four eighths of a byte's worth of information.
///     </para>
///     <para>
///         Deliberately a terrain-only struct rather than a widening of the shared vertex: reference
///         geometry cannot narrow the same way (SpeedTree smuggles per-instance data in the
///         magnitudes of its normals and tangents), so one shared layout would have to be the union
///         of both needs.
///     </para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TerrainVertex
{
    /// <summary>Bytes per vertex on the wire. Pinned by test; the input layout and the residency policy both depend on it.</summary>
    public const int SizeInBytes = 12;

    /// <summary>
    ///     World Z. <c>R32_FLOAT</c> at offset 0 — the only part of the position that is not a
    ///     function of the vertex's index within its cell.
    /// </summary>
    public float Height;

    /// <summary>Octahedral normal, X component. <c>R16G16_SNORM</c> at offset 4.</summary>
    public short NormalOctX;

    /// <summary>Octahedral normal, Y component.</summary>
    public short NormalOctY;

    /// <summary>
    ///     Vertex colour, <c>R8G8B8A8_UNORM</c> at offset 8 — R in the low byte, matching the
    ///     little-endian memory order the input assembler reads.
    /// </summary>
    public uint Color;

    public TerrainVertex(float height, Vector3 normal, uint packedColor)
    {
        Height = height;
        TerrainNormalPacking.Encode(normal, out NormalOctX, out NormalOctY);
        Color = packedColor;
    }

    /// <summary>
    ///     The normal as the shader will reconstruct it. A property, not a field: the stored form is
    ///     the packed pair, and exposing a settable <c>Vector3</c> would invite a caller to believe
    ///     the full-precision value survives.
    /// </summary>
    public readonly Vector3 Normal => TerrainNormalPacking.Decode(NormalOctX, NormalOctY);

    /// <summary>The colour as the shader will reconstruct it. Round-trips the source bytes exactly.</summary>
    public readonly Vector4 VertexColor => UnpackColor(Color);

    /// <summary>
    ///     Packs straight from the source bytes, so the LAND <c>VCLR</c> path never passes through a
    ///     float. Alpha is opaque: terrain vertex colour is a tint, and LAND carries no alpha.
    /// </summary>
    public static uint PackColor(byte r, byte g, byte b) => PackColor(r, g, b, 255);

    public static uint PackColor(byte r, byte g, byte b, byte a) =>
        r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24);

    /// <summary>
    ///     Packs a float colour, clamping and rounding to the nearest representable byte. Used only
    ///     where a caller already holds floats; the <c>VCLR</c> path uses the byte overload.
    /// </summary>
    public static uint PackColor(Vector4 color) => PackColor(
        ToByte(color.X), ToByte(color.Y), ToByte(color.Z), ToByte(color.W));

    public static Vector4 UnpackColor(uint packed) => new(
        (packed & 0xFF) / 255f,
        ((packed >> 8) & 0xFF) / 255f,
        ((packed >> 16) & 0xFF) / 255f,
        ((packed >> 24) & 0xFF) / 255f);

    private static byte ToByte(float value) => (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
}
