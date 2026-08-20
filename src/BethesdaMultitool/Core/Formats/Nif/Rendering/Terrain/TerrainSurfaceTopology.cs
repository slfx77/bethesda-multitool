using System.Numerics;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;

/// <summary>
///     Triangle topology used to turn a LAND height grid into a renderable surface.
///     The legacy/default path keeps one south-east-to-north-west diagonal in every quad;
///     Fallout: New Vegas alternates diagonals in a checkerboard.
/// </summary>
internal enum TerrainTriangleTopology
{
    FixedSouthEastNorthWest,
    AlternatingCheckerboard
}

/// <summary>
///     Shared terrain-triangle selection and plane sampling. Keeping mesh indices, CPU raycasts,
///     and engine-generated placements on this one policy prevents the three surfaces from
///     disagreeing inside non-planar heightfield quads.
/// </summary>
internal static class TerrainSurfaceTopology
{
    internal static TerrainTriangleTopology ForGame(BethesdaGame game)
    {
        return game switch
        {
            // TESObjectLAND::GetCoordData in the recovered FNV executable alternates the diagonal by
            // (quadX + quadY) parity. The 16-quad LAND quadrants have even width, so quadrant-local and
            // cell-local parity are identical. FO3 parity 2026-08-10: FO3 retail x86 (0x007220D0)
            // implements the SAME checkerboard — identical parity test, index sets, and 1/2048, 1/128,
            // 17-stride constants (TestOutput/fo3-parity-2026-08/census/fo3-decompile-spotchecks.md).
            BethesdaGame.FalloutNewVegas or BethesdaGame.Fallout3 =>
                TerrainTriangleTopology.AlternatingCheckerboard,
            _ => TerrainTriangleTopology.FixedSouthEastNorthWest
        };
    }

    internal static bool UsesSouthwestNortheastDiagonal(
        TerrainTriangleTopology topology,
        int quadX,
        int quadY)
    {
        return topology == TerrainTriangleTopology.AlternatingCheckerboard &&
               ((quadX + quadY) & 1) == 0;
    }

    /// <summary>
    ///     Samples the exact plane of the selected heightfield triangle at a cell-local XY point.
    ///     Returns the same +Z-facing triangle normal used by the rendered index topology.
    /// </summary>
    internal static bool TrySampleTriangle(
        ReadOnlySpan<float> heights,
        int gridSize,
        float localX,
        float localY,
        float spacing,
        TerrainTriangleTopology topology,
        out float height,
        out Vector3 normal)
    {
        height = 0f;
        normal = Vector3.UnitZ;
        if (gridSize < 2 || heights.Length < gridSize * gridSize ||
            !float.IsFinite(localX) || !float.IsFinite(localY) ||
            !float.IsFinite(spacing) || spacing <= 0f)
        {
            return false;
        }

        var span = spacing * (gridSize - 1);
        if (localX < 0f || localY < 0f || localX > span || localY > span)
        {
            return false;
        }

        var vertexX = Math.Clamp(localX / spacing, 0f, gridSize - 1f);
        var vertexY = Math.Clamp(localY / spacing, 0f, gridSize - 1f);
        var quadX = Math.Min((int)vertexX, gridSize - 2);
        var quadY = Math.Min((int)vertexY, gridSize - 2);
        var fractionX = vertexX - quadX;
        var fractionY = vertexY - quadY;

        var row0 = quadY * gridSize;
        var row1 = (quadY + 1) * gridSize;
        var h00 = heights[row0 + quadX];
        var h10 = heights[row0 + quadX + 1];
        var h01 = heights[row1 + quadX];
        var h11 = heights[row1 + quadX + 1];
        if (!float.IsFinite(h00) || !float.IsFinite(h10) ||
            !float.IsFinite(h01) || !float.IsFinite(h11))
        {
            return false;
        }

        Vector3 v0;
        Vector3 v1;
        Vector3 v2;
        if (UsesSouthwestNortheastDiagonal(topology, quadX, quadY))
        {
            // Even FNV quad: v00-v11 diagonal. GetCoordData selects the west triangle for
            // fractionX <= fractionY and the south triangle otherwise.
            if (fractionX <= fractionY)
            {
                height = h00 * (1f - fractionY) +
                         h01 * (fractionY - fractionX) +
                         h11 * fractionX;
                v0 = new Vector3(0f, 0f, h00);
                v1 = new Vector3(spacing, spacing, h11);
                v2 = new Vector3(0f, spacing, h01);
            }
            else
            {
                height = h00 * (1f - fractionX) +
                         h10 * (fractionX - fractionY) +
                         h11 * fractionY;
                v0 = new Vector3(0f, 0f, h00);
                v1 = new Vector3(spacing, 0f, h10);
                v2 = new Vector3(spacing, spacing, h11);
            }
        }
        else if (fractionX + fractionY <= 1f)
        {
            // Fixed/default diagonal, or an odd FNV quad: south-west triangle.
            height = h00 * (1f - fractionX - fractionY) +
                     h10 * fractionX +
                     h01 * fractionY;
            v0 = new Vector3(0f, 0f, h00);
            v1 = new Vector3(spacing, 0f, h10);
            v2 = new Vector3(0f, spacing, h01);
        }
        else
        {
            // Fixed/default diagonal, or an odd FNV quad: north-east triangle.
            height = h01 * (1f - fractionX) +
                     h10 * (1f - fractionY) +
                     h11 * (fractionX + fractionY - 1f);
            v0 = new Vector3(0f, spacing, h01);
            v1 = new Vector3(spacing, 0f, h10);
            v2 = new Vector3(spacing, spacing, h11);
        }

        var faceNormal = Vector3.Cross(v1 - v0, v2 - v0);
        var lengthSquared = faceNormal.LengthSquared();
        if (!float.IsFinite(height) || !float.IsFinite(lengthSquared) || lengthSquared <= 1e-20f)
        {
            height = 0f;
            normal = Vector3.UnitZ;
            return false;
        }

        normal = faceNormal / MathF.Sqrt(lengthSquared);
        return true;
    }
}
