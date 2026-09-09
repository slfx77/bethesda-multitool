using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Rasterization;

namespace BethesdaMultitool.Core.Formats.Xngine.Mesh;

/// <summary>
///     Rasterises an assembled classic scene — many placed <see cref="XnGineMeshInstance" />s — to
///     an RGBA image through the repo's software sprite renderer, so a level export can leave a
///     visual oracle beside its GLB and a test can look at a level without a GPU.
///     <para>
///         Each instance is BAKED: its mesh's vertices are transformed by the instance matrix in the
///         meshes' own Y-down space and then rotated into the renderer's Z-up basis by
///         <see cref="XnGineViewerSceneAdapter.ToViewerSpace" /> — the same route the GUI's level
///         pane takes, so what this draws is what the viewer places. Untextured: shape is the oracle.
///     </para>
/// </summary>
internal static class XnGineScenePreviewRenderer
{
    /// <summary>Elevation that looks straight down: the plan view.</summary>
    public const float TopDownElevationDegrees = 90f;

    /// <summary>
    ///     Renders the scene from an azimuth and elevation (degrees; 90 elevation is straight down),
    ///     the longest image edge at most <paramref name="maxSize" /> pixels. Null when nothing has
    ///     geometry.
    /// </summary>
    public static SpriteResult? Render(
        IEnumerable<XnGineMeshInstance> instances, float azimuthDegrees, float elevationDegrees, int maxSize)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSize, 16);

        var model = Bake(instances);
        if (!model.HasGeometry)
        {
            return null;
        }

        return NifSpriteRenderer.Render(model, null, 1f, 32, maxSize, azimuthDegrees, elevationDegrees, maxSize);
    }

    /// <summary>Bakes the instances into one model in the renderer's Z-up space.</summary>
    public static NifRenderableModel Bake(IEnumerable<XnGineMeshInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);

        var model = new NifRenderableModel();
        foreach (var instance in instances)
        {
            foreach (var subMesh in instance.Mesh.SubMeshes)
            {
                var count = subMesh.Vertices.Count;
                if (count == 0 || count > ushort.MaxValue || subMesh.Indices.Count < 3)
                {
                    continue;
                }

                var positions = new float[count * 3];
                var normals = new float[count * 3];
                for (var v = 0; v < count; v++)
                {
                    var vertex = subMesh.Vertices[v];
                    var position = XnGineViewerSceneAdapter.ToViewerSpace(
                        Vector3.Transform(vertex.Position, instance.Transform));
                    var normal = XnGineViewerSceneAdapter.ToViewerSpace(
                        Vector3.TransformNormal(vertex.Normal, instance.Transform));
                    if (normal.LengthSquared() > 0)
                    {
                        normal = Vector3.Normalize(normal);
                    }

                    positions[v * 3] = position.X;
                    positions[v * 3 + 1] = position.Y;
                    positions[v * 3 + 2] = position.Z;
                    normals[v * 3] = normal.X;
                    normals[v * 3 + 1] = normal.Y;
                    normals[v * 3 + 2] = normal.Z;
                }

                var triangles = new ushort[subMesh.Indices.Count];
                for (var i = 0; i < triangles.Length; i++)
                {
                    triangles[i] = (ushort)subMesh.Indices[i];
                }

                var submesh = new RenderableSubmesh
                {
                    ShapeName = instance.Name,
                    Positions = positions,
                    Triangles = triangles,
                    Normals = normals
                };
                model.Submeshes.Add(submesh);
                model.ExpandBounds(positions);
            }
        }

        return model;
    }
}
