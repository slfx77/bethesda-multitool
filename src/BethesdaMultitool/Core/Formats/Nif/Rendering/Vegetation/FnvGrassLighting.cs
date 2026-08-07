using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Vegetation;

/// <summary>
///     CPU half of FO3/FNV's recovered grass lighting model — the terrain data the engine bakes
///     into each grass instance at placement time, which the GRASS vertex shaders then light with.
///     <para>
///         Sources: <c>TESTerrainLODManager::CreateGrass</c> (0x823BA528) and
///         <c>TallGrassShaderProperty::AddGrass</c> (0x82A456B8) in
///         <c>tools/GhidraProject/grass_session_g_fnv_decompiled.txt</c>; the land queries they call
///         (<c>TES::GetLandColor</c> 0x822A5B50 → <c>TESObjectLAND::GetLandColor</c> 0x82375748,
///         <c>TESObjectLAND::GetCoordData</c> 0x82372C78) in
///         <c>tools/GhidraProject/land_query_decompiled.txt</c>; the consuming shaders
///         <c>GRASS2000/2002.vso</c> in <c>shaderpackage019.sdp</c>.
///     </para>
///     <para>
///         The engine packs two payloads per instance: the terrain normal (as
///         <c>min((N + 1) / 2, 0.97)</c> in the fractional XYZ, decoded by the shader as
///         <c>min(N, 0.94)</c>) and a baked light scalar in the fractional W, which the shader
///         remaps to <c>L = 0.25 + 0.75 * frac(W)</c>. Both feed the vertex lighting equation:
///         <c>ambient = L * AmbientColor</c> (never vertex-color modulated, never shadowed) and
///         <c>sun = 1.5 * L * saturate(dot(SunDir, N)) * vertexColor * SunColor</c>.
///     </para>
/// </summary>
internal static class FnvGrassLighting
{
    /// <summary>
    ///     Land-color luminance weights from <c>AddGrass</c> (decompile constants 0.31/0.37/0.32 at
    ///     lines 571-573, applied at line 785-787). Deliberately not the usual Rec.601 triple.
    /// </summary>
    internal const float LuminanceRed = 0.31f;

    /// <inheritdoc cref="LuminanceRed" />
    internal const float LuminanceGreen = 0.37f;

    /// <inheritdoc cref="LuminanceRed" />
    internal const float LuminanceBlue = 0.32f;

    /// <summary>GRASS2000/2002 remap of the packed fraction: <c>L = 0.25 + 0.75 * frac(W)</c>.</summary>
    internal const float LightBase = 0.25f;

    /// <inheritdoc cref="LightBase" />
    internal const float LightRange = 0.75f;

    /// <summary>
    ///     Fallback for the grass sun-term multiplier. The engine's value is NOT a shader literal:
    ///     the shipped FNV <c>GRASS2002.vso</c> declares <c>c7 AddlParams</c> as a CPU-driven
    ///     constant (its only defs are <c>c8 = 0.75, 0.25, 0.0078125, 0</c> — the light remap and
    ///     wind scale) and applies the sun term as <c>mul oT3.xyz, r1, c7.x</c>. The record contains
    ///     no 1.5 anywhere. <c>TallGrassShader::SetupGeometryConstants</c> writes that register from
    ///     the imagespace <c>GrassDimmer</c> under HDR, so the live value is weather-blended and
    ///     reaches the shader through the atmosphere CB; this constant is only the fallback for
    ///     hosts that do not publish that lane, set to the shipped FNV exterior value.
    /// </summary>
    internal const float SunBoost = 1.3f;

    /// <summary>
    ///     <c>AddGrass</c> clamps the fractional payload to [0, 0.99] (constant 0.99 at decompile
    ///     line 2208) so the integer height component stays recoverable by <c>floor</c>.
    /// </summary>
    internal const float BakedLightMaximum = 0.99f;

    /// <summary>Land vertex grid pitch: 33x33 vertices spanning the 4096-unit cell.</summary>
    private const int LandGridSize = 33;

    /// <summary>Quad size in world units — <c>GetCoordData</c> resolves the quad with <c>&gt;&gt; 7</c>.</summary>
    private const float QuadWorldSize = 128f;

    /// <summary>
    ///     Replays the fractional payload from <c>AddGrass</c> (decompile lines 2241-2251):
    ///     <c>frac = clamp((ColorRange * 0.5 * signedRandom + (1 - ColorRange)) * landLuminance, 0, 0.99)</c>.
    ///     ColorRange therefore controls how much per-clump variance rides on the terrain's own
    ///     brightness; every shipped FO3/FNV GRAS record authors it in the 0.1-0.5 band.
    /// </summary>
    internal static float ComputeBakedLight(float random01, float colorRange, float landLuminance)
    {
        var signedRandom = random01 * 2f - 1f;
        var variance = colorRange * 0.5f * signedRandom + (1f - colorRange);
        var value = variance * landLuminance;
        if (!float.IsFinite(value)) return 0f;
        return value >= 1f ? BakedLightMaximum : MathF.Max(value, 0f);
    }

    /// <summary>
    ///     Land luminance at a cell-local position, following the engine's color query
    ///     (<c>TESObjectLAND::GetLandColor</c>): the point's 128-unit quad is split into two
    ///     triangles, and the three corner vertex colors are blended by the in-quad fractions —
    ///     barycentric interpolation, not a nearest-vertex lookup. Returns 1.0 (white) when the
    ///     cell authors no VCLR, matching <c>CreateGrass</c>'s white default.
    /// </summary>
    internal static float SampleLandLuminance(
        byte[]? vertexColors,
        float localX,
        float localY,
        float spacing)
    {
        if (!TrySampleLandColor(vertexColors, localX, localY, spacing, out var color)) return 1f;
        return color.X * LuminanceRed + color.Y * LuminanceGreen + color.Z * LuminanceBlue;
    }

    /// <summary>
    ///     Authored land normal (VNML) at a cell-local position, interpolated across the same
    ///     triangle the color query uses and renormalized. Returns null when the cell authors no
    ///     VNML or the interpolated vector is degenerate, so callers can fall back to the
    ///     heightmap-derived normal.
    /// </summary>
    internal static Vector3? SampleLandNormal(
        byte[]? vertexNormals,
        float localX,
        float localY,
        float spacing)
    {
        if (vertexNormals is null) return null;
        if (!TryResolveTriangle(localX, localY, spacing, out var triangle)) return null;

        var a = ReadNormal(vertexNormals, triangle.IndexA);
        var b = ReadNormal(vertexNormals, triangle.IndexB);
        var c = ReadNormal(vertexNormals, triangle.IndexC);
        if (a is null || b is null || c is null) return null;

        var blended = Blend(a.Value, b.Value, c.Value, triangle.WeightA, triangle.WeightB, triangle.WeightC);
        var lengthSquared = blended.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared < 1e-8f) return null;
        return Vector3.Normalize(blended);
    }

    private static bool TrySampleLandColor(
        byte[]? vertexColors,
        float localX,
        float localY,
        float spacing,
        out Vector3 color)
    {
        color = Vector3.One;
        if (vertexColors is null) return false;
        if (!TryResolveTriangle(localX, localY, spacing, out var triangle)) return false;

        var a = ReadColor(vertexColors, triangle.IndexA);
        var b = ReadColor(vertexColors, triangle.IndexB);
        var c = ReadColor(vertexColors, triangle.IndexC);
        if (a is null || b is null || c is null) return false;

        // The decompiled blend copies the anchor vertex's first channel verbatim while
        // interpolating the rest; that asymmetry reads as a decompiler artifact (the callee writes
        // four floats and only three are ever consumed in float math), so all three channels are
        // interpolated here. Revisit if grass tint diverges from retail on strongly painted terrain.
        color = Blend(a.Value, b.Value, c.Value, triangle.WeightA, triangle.WeightB, triangle.WeightC);
        return true;
    }

    /// <summary>
    ///     Resolves the LAND triangle under a cell-local position plus its barycentric weights,
    ///     mirroring <c>TESObjectLAND::GetCoordData</c>: the quad index comes from the 128-unit
    ///     grid, the diagonal orientation alternates with the quad's parity, and the in-quad
    ///     fractions select which of the two triangles the point falls in.
    /// </summary>
    private static bool TryResolveTriangle(float localX, float localY, float spacing, out Triangle triangle)
    {
        triangle = default;
        if (spacing <= 0f || !float.IsFinite(localX) || !float.IsFinite(localY)) return false;

        var quadSpan = spacing > 0f ? spacing : QuadWorldSize;
        var quadX = (int)MathF.Floor(localX / quadSpan);
        var quadY = (int)MathF.Floor(localY / quadSpan);
        if (quadX < 0 || quadY < 0 || quadX >= LandGridSize - 1 || quadY >= LandGridSize - 1)
        {
            return false;
        }

        var u = localX / quadSpan - quadX;
        var v = localY / quadSpan - quadY;
        u = Math.Clamp(u, 0f, 1f);
        v = Math.Clamp(v, 0f, 1f);

        var bottomLeft = Index(quadX, quadY);
        var bottomRight = Index(quadX + 1, quadY);
        var topLeft = Index(quadX, quadY + 1);
        var topRight = Index(quadX + 1, quadY + 1);

        // GetCoordData splits the quad along one of the two diagonals depending on the parity of
        // (quadX + quadY) — the same alternating-checkerboard topology the height sampler replays.
        if (((quadX + quadY) & 1) == 0)
        {
            // Diagonal from bottom-left to top-right.
            if (u >= v)
            {
                triangle = new Triangle(bottomLeft, bottomRight, topRight, 1f - u, u - v, v);
            }
            else
            {
                triangle = new Triangle(bottomLeft, topRight, topLeft, 1f - v, u, v - u);
            }
        }
        else
        {
            // Diagonal from bottom-right to top-left.
            if (u + v <= 1f)
            {
                triangle = new Triangle(bottomLeft, bottomRight, topLeft, 1f - u - v, u, v);
            }
            else
            {
                triangle = new Triangle(topRight, topLeft, bottomRight, u + v - 1f, 1f - u, 1f - v);
            }
        }

        return true;
    }

    private static int Index(int i, int j) => j * LandGridSize + i;

    private static Vector3? ReadColor(byte[] data, int vertexIndex)
    {
        var offset = vertexIndex * 3;
        if (offset < 0 || offset + 2 >= data.Length) return null;
        return new Vector3(data[offset] / 255f, data[offset + 1] / 255f, data[offset + 2] / 255f);
    }

    private static Vector3? ReadNormal(byte[] data, int vertexIndex)
    {
        // VNML components are signed bytes carried in a byte[] (same reinterpretation
        // TerrainMeshBuilder uses).
        var offset = vertexIndex * 3;
        if (offset < 0 || offset + 2 >= data.Length) return null;
        return new Vector3(
            unchecked((sbyte)data[offset]) / 127f,
            unchecked((sbyte)data[offset + 1]) / 127f,
            unchecked((sbyte)data[offset + 2]) / 127f);
    }

    private static Vector3 Blend(Vector3 a, Vector3 b, Vector3 c, float wa, float wb, float wc) =>
        a * wa + b * wb + c * wc;

    private readonly record struct Triangle(
        int IndexA,
        int IndexB,
        int IndexC,
        float WeightA,
        float WeightB,
        float WeightC);
}
