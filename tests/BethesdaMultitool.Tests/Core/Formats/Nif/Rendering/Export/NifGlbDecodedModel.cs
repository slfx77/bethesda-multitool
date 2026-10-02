using System.Numerics;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>Everything <see cref="NifGlbParityOracle" /> reads from one parsed GLB, independent of table order.</summary>
internal sealed class NifGlbDecodedModel
{
    /// <summary>Every drawn triangle occurrence, including repeated placements of one logical mesh.</summary>
    internal List<NifGlbDecodedTriangle> Triangles { get; } = [];

    /// <summary>Every drawn primitive occurrence, indexed by <see cref="NifGlbDecodedTriangle.Primitive" />.</summary>
    internal List<NifGlbDecodedPrimitive> Primitives { get; } = [];

    /// <summary>The owning node's world matrix once per drawn primitive occurrence.</summary>
    internal List<Matrix4x4> Placements { get; } = [];

    /// <summary>Every nonempty logical mesh name.</summary>
    internal HashSet<string> MeshNames { get; } = new(StringComparer.Ordinal);

    /// <summary>Every primitive name recorded in the shared <c>multitoolPrimitiveName</c> extras key.</summary>
    internal HashSet<string> PrimitiveNames { get; } = new(StringComparer.Ordinal);

    /// <summary>Every nonempty logical material name.</summary>
    internal HashSet<string> MaterialNames { get; } = new(StringComparer.Ordinal);

    /// <summary>How many logical material rows carry each content signature.</summary>
    internal Dictionary<string, int> MaterialRows { get; } = new(StringComparer.Ordinal);

    /// <summary>The declared <c>extensionsUsed</c> names.</summary>
    internal SortedSet<string> ExtensionsUsed { get; } = new(StringComparer.Ordinal);

    /// <summary>The largest finite absolute world coordinate observed, which sizes the matching cells.</summary>
    internal double MaximumAbsoluteCoordinate { get; private set; }

    /// <summary>Widens <see cref="MaximumAbsoluteCoordinate" /> with a world position's finite components.</summary>
    /// <param name="position">A decoded world position.</param>
    internal void Observe(Vector3 position)
    {
        ReadOnlySpan<float> components = [position.X, position.Y, position.Z];
        foreach (var component in components)
        {
            if (float.IsFinite(component))
            {
                MaximumAbsoluteCoordinate = Math.Max(MaximumAbsoluteCoordinate, Math.Abs(component));
            }
        }
    }
}
