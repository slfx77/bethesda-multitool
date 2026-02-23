using FalloutXbox360Utils.Core.Formats.Esm.Analysis.Helpers;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Space-filling curve functions, Pearson correlation, and shared record types for OFST commands.
/// </summary>
internal static class OfstMathUtils
{
    internal const double ZeroEpsilon = 1e-9;

    internal static bool IsNearlyZero(double value)
    {
        return Math.Abs(value) < ZeroEpsilon;
    }

    internal static uint Morton2D(uint x, uint y)
    {
        return (Part1By1(y) << 1) | Part1By1(x);
    }

    internal static uint Part1By1(uint x)
    {
        x &= 0x0000FFFF;
        x = (x | (x << 8)) & 0x00FF00FF;
        x = (x | (x << 4)) & 0x0F0F0F0F;
        x = (x | (x << 2)) & 0x33333333;
        x = (x | (x << 1)) & 0x55555555;
        return x;
    }

    internal static int NextPow2(int value)
    {
        var p = 1;
        while (p < value)
        {
            p <<= 1;
        }

        return p;
    }

    internal static int HilbertIndex(int n, int x, int y)
    {
        var d = 0;
        for (var s = n / 2; s > 0; s /= 2)
        {
            var rx = (x & s) > 0 ? 1 : 0;
            var ry = (y & s) > 0 ? 1 : 0;
            d += s * s * ((3 * rx) ^ ry);
            Rot(n, ref x, ref y, rx, ry);
        }

        return d;
    }

    internal static double RowMajorSerp(int row, int col, int columns)
    {
        return (row & 1) == 0 ? (row * columns) + col : (row * columns) + (columns - 1 - col);
    }

    internal static double TiledRowMajor(int row, int col, int columns, int tile, bool serpOuter)
    {
        var tilesX = (columns + tile - 1) / tile;
        var tileX = col / tile;
        var tileY = row / tile;
        var innerX = col % tile;
        var innerY = row % tile;
        var tileIndex = serpOuter && (tileY & 1) == 1
            ? (tileY * tilesX) + (tilesX - 1 - tileX)
            : (tileY * tilesX) + tileX;
        var inner = (innerY * tile) + innerX;
        return (tileIndex * tile * tile) + inner;
    }

    internal static double TiledMorton(int row, int col, int columns, int tile, bool serpOuter)
    {
        var tilesX = (columns + tile - 1) / tile;
        var tileX = col / tile;
        var tileY = row / tile;
        var innerX = col % tile;
        var innerY = row % tile;
        var tileIndex = serpOuter && (tileY & 1) == 1
            ? (tileY * tilesX) + (tilesX - 1 - tileX)
            : (tileY * tilesX) + tileX;
        var inner = Morton2D((uint)innerX, (uint)innerY);
        return (tileIndex * tile * tile) + inner;
    }

    internal static double TiledHilbert(int row, int col, int columns, int tile, bool serpOuter)
    {
        var tilesX = (columns + tile - 1) / tile;
        var tileX = col / tile;
        var tileY = row / tile;
        var innerX = col % tile;
        var innerY = row % tile;
        var tileIndex = serpOuter && (tileY & 1) == 1
            ? (tileY * tilesX) + (tilesX - 1 - tileX)
            : (tileY * tilesX) + tileX;
        var inner = HilbertIndex(tile, innerX, innerY);
        return (tileIndex * tile * tile) + inner;
    }

    internal static double Pearson(IReadOnlyList<OfstLayoutEntry> ordered, Func<OfstLayoutEntry, double> selector)
    {
        var n = ordered.Count;
        if (n == 0)
        {
            return 0;
        }

        double sumX = 0;
        double sumY = 0;
        double sumXY = 0;
        double sumX2 = 0;
        double sumY2 = 0;

        for (var i = 0; i < n; i++)
        {
            var x = selector(ordered[i]);
            var y = i;
            sumX += x;
            sumY += y;
            sumXY += x * y;
            sumX2 += x * x;
            sumY2 += y * y;
        }

        var num = (n * sumXY) - (sumX * sumY);
        var den = Math.Sqrt(((n * sumX2) - (sumX * sumX)) * ((n * sumY2) - (sumY * sumY)));
        return IsNearlyZero(den) ? 0 : num / den;
    }

    internal static double Pearson(double[] x, double[] y)
    {
        if (x.Length != y.Length || x.Length < 2)
        {
            return 0;
        }

        var n = x.Length;
        var sumX = 0.0;
        var sumY = 0.0;
        var sumX2 = 0.0;
        var sumY2 = 0.0;
        var sumXy = 0.0;

        for (var i = 0; i < n; i++)
        {
            sumX += x[i];
            sumY += y[i];
            sumX2 += x[i] * x[i];
            sumY2 += y[i] * y[i];
            sumXy += x[i] * y[i];
        }

        var denom = Math.Sqrt(((n * sumX2) - (sumX * sumX)) * ((n * sumY2) - (sumY * sumY)));
        return denom < ZeroEpsilon ? 0 : ((n * sumXy) - (sumX * sumY)) / denom;
    }

    private static void Rot(int n, ref int x, ref int y, int rx, int ry)
    {
        if (ry != 0)
        {
            return;
        }

        if (rx == 1)
        {
            x = n - 1 - x;
            y = n - 1 - y;
        }

        (x, y) = (y, x);
    }

    // ─── Shared record types ───────────────────────────────────────────────────

    internal sealed record OfstLayoutEntry(
        int Index,
        int Row,
        int Col,
        int GridX,
        int GridY,
        uint OfstEntry,
        uint ResolvedOffset,
        uint FormId,
        uint Morton,
        uint RecordOffset);

    internal sealed record WorldContext(
        AnalyzerRecordInfo WrldRecord,
        byte[] WrldData,
        List<uint> Offsets,
        int MinX,
        int MinY,
        int MaxX,
        int MaxY,
        int Columns,
        int Rows,
        string BoundsText);

    internal sealed record WorldEntries(
        uint WorldFormId,
        WorldContext Context,
        List<OfstLayoutEntry> Ordered,
        byte[] Data,
        bool BigEndian);

    internal sealed record ResolveOfstCellResult(
        uint WorldFormId,
        uint CellFormId,
        int GridX,
        int GridY,
        int Index,
        uint Entry,
        uint ResolvedOffset,
        AnalyzerRecordInfo? Match);

    internal sealed record OfstValidationResult(int GridX, int GridY, string ResolvedLabel, string? Issue);

    internal sealed record DeltaEntry(
        int Order,
        int DeltaX,
        int DeltaY,
        string Direction,
        int GridX1,
        int GridY1,
        int GridX2,
        int GridY2);

    internal sealed record TileEntry(
        int Order,
        int InnerX,
        int InnerY,
        int GridX,
        int GridY,
        uint FormId);

    internal sealed record TileOrderEntry(int TileIndex, int FirstOrder, int Count);

    internal sealed record TileSummary(
        List<TileOrderEntry> TileOrder,
        Dictionary<int, List<(int X, int Y)>> TileInner);
}
