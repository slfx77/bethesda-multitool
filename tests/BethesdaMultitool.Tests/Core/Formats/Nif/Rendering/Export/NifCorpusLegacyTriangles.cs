using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>Accounts for the legacy mesh builder's exact repeated-position rejection without changing normalized geometry.</summary>
internal static class NifCorpusLegacyTriangles
{
    /// <summary>Counts drawable source triangles with two exactly equal positions; distinct collinear points remain included.</summary>
    internal static int RepeatedPositions(GlbScene scene, NifTextureResolver resolver) => scene.MeshParts
        .Where(part => !GlbWriter.ShouldSkipStarfieldNoDrawSubmesh(part.Submesh, resolver))
        .Sum(static part => Count(Enumerable.Range(0, part.Submesh.VertexCount).Select(index =>
                new Vector3(part.Submesh.Positions[index * 3], part.Submesh.Positions[index * 3 + 1],
                    part.Submesh.Positions[index * 3 + 2])).ToArray(),
            part.Submesh.Triangles.Select(static index => (int)index).ToArray()));

    /// <summary>Counts the same authored representational triangles retained by the immutable neutral snapshot.</summary>
    internal static int RepeatedPositions(ModelDocument scene) => scene.Meshes
        .SelectMany(static mesh => mesh.Primitives)
        .Sum(static part => Count(part.Vertices.Select(static vertex => vertex.Position).ToArray(), part.Indices));

    /// <summary>Matches only exact position equality, never an epsilon or a general zero-area predicate.</summary>
    private static int Count(Vector3[] positions, IReadOnlyList<int> indices)
    {
        var count = 0;
        for (var index = 0; index < indices.Count; index += 3)
        {
            var a = positions[indices[index]];
            var b = positions[indices[index + 1]];
            var c = positions[indices[index + 2]];
            if (a.Equals(b) || b.Equals(c) || c.Equals(a)) count++;
        }
        return count;
    }
}
