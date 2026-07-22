using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;

/// <summary>
///     Stable per-particle depth ordering for the baked four-vertex billboard representation.
///     Whole-submesh sorting is insufficient for smoke/dust clouds whose quads overlap one another:
///     the retail particle path sorts those camera-facing sprites, not merely the owning NIF shape.
/// </summary>
internal static class ParticleDepthSort
{
    public const int IndicesPerQuad = 6;

    internal readonly record struct Entry(float DistanceSquared, int SourceIndex) : IComparable<Entry>
    {
        public int CompareTo(Entry other)
        {
            // Back-to-front. Preserve source order for equal depths so seeded captures remain
            // deterministic and do not shimmer when two sprites share a plane.
            var distanceOrder = other.DistanceSquared.CompareTo(DistanceSquared);
            return distanceOrder != 0 ? distanceOrder : SourceIndex.CompareTo(other.SourceIndex);
        }

        public static bool operator <(Entry left, Entry right) => left.CompareTo(right) < 0;

        public static bool operator <=(Entry left, Entry right) => left.CompareTo(right) <= 0;

        public static bool operator >(Entry left, Entry right) => left.CompareTo(right) > 0;

        public static bool operator >=(Entry left, Entry right) => left.CompareTo(right) >= 0;
    }

    /// <summary>
    ///     Writes six R16 indices per quad in stable far-to-near order. Local centers are transformed
    ///     with the absolute placement matrix; camera position therefore remains in absolute space
    ///     even when the GPU draw matrix has had a camera-relative render origin folded into it.
    /// </summary>
    public static void WriteBackToFrontQuadIndices(
        ReadOnlySpan<Vector3> localCenters,
        Matrix4x4 sourceWorld,
        Vector3 cameraPosition,
        Span<Entry> scratch,
        Span<ushort> destination)
    {
        if (scratch.Length < localCenters.Length)
        {
            throw new ArgumentException("Scratch span is smaller than the particle count.", nameof(scratch));
        }
        if (destination.Length < checked(localCenters.Length * IndicesPerQuad))
        {
            throw new ArgumentException("Destination span is smaller than the particle index count.", nameof(destination));
        }
        if (localCenters.Length > ushort.MaxValue / 4 + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(localCenters), "Particle quads exceed the R16 vertex range.");
        }

        for (var i = 0; i < localCenters.Length; i++)
        {
            var worldCenter = Vector3.Transform(localCenters[i], sourceWorld);
            var distanceSquared = Vector3.DistanceSquared(worldCenter, cameraPosition);
            // Non-finite authored geometry sorts last instead of poisoning the comparison order.
            if (!float.IsFinite(distanceSquared))
            {
                distanceSquared = float.NegativeInfinity;
            }
            scratch[i] = new Entry(distanceSquared, i);
        }

        scratch[..localCenters.Length].Sort();
        for (var sortedIndex = 0; sortedIndex < localCenters.Length; sortedIndex++)
        {
            var baseVertex = checked(scratch[sortedIndex].SourceIndex * 4);
            var output = sortedIndex * IndicesPerQuad;
            destination[output + 0] = checked((ushort)(baseVertex + 0));
            destination[output + 1] = checked((ushort)(baseVertex + 1));
            destination[output + 2] = checked((ushort)(baseVertex + 2));
            destination[output + 3] = checked((ushort)(baseVertex + 0));
            destination[output + 4] = checked((ushort)(baseVertex + 2));
            destination[output + 5] = checked((ushort)(baseVertex + 3));
        }
    }
}
