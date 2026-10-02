// Original to this repository: AweMultitool (slfx77) has no glTF/SharpGLTF exporter for its
// Granny reader, so nothing here is ported; this consumes the Gr2File the ported reader produces.

using System.Globalization;
using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Transforms;

namespace BethesdaMultitool.Core.Formats.Granny;

/// <summary>
///     Writes a Granny 2 file's models to GLB: every bone becomes a node, a skinned mesh binds to its
///     bones through the file's own inverse-bind matrices, and an unskinned mesh sits under the model's
///     placement node.
///     <para>
///         ⚑ Coordinate basis: the exporter records the authoring tool's Right/Up/Back vectors in
///         <c>ArtToolInfo</c>, which is exactly what Granny's own <c>ComputeBasisConversion</c>
///         consumes (F3.exe imports it beside <c>TransformFile</c> — the game rebases every file at
///         load). glTF is right-handed Y-up, so a file vector <c>v</c> lands at
///         <c>(v·Right, v·Up, v·Back)</c>; a left-handed tool basis has a negative determinant and
///         the triangle winding is reversed to compensate. Bone transforms are conjugated by the
///         same matrix so skinning stays exact.
///     </para>
///     <para>
///         ⚑ Animation: an SDK 2.2 curve of degree 0 is exported as STEP keys and degree 1 as LINEAR
///         keys, both exactly at the file's knots; a higher degree is refused rather than approximated.
///         Tracks bind by the identities Granny records — the track group's name must equal the
///         skeleton's, and each transform track's name must equal one bone's name.
///     </para>
/// </summary>
internal static class Gr2ModelGlbExporter
{
    public static Gr2ExportSummary Write(Gr2File file, string outputPath,
        IReadOnlyList<Gr2AnimationClip>? animations = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(outputPath);
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var (scene, summary) = Build(file, animations);
        scene.ToGltf2().SaveGLB(outputPath);
        return summary;
    }

    public static byte[] WriteToBytes(Gr2File file, IReadOnlyList<Gr2AnimationClip>? animations = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        var (scene, _) = Build(file, animations);
        using var stream = new MemoryStream();
        scene.ToGltf2().WriteGLB(stream);
        return stream.ToArray();
    }

    /// <summary>The row-vector basis matrix taking file coordinates to glTF's right-handed Y-up frame.</summary>
    internal static Matrix4x4 BasisMatrix(Gr2ArtToolInfo? artTool)
    {
        if (artTool is null || !IsOrthonormalBasis(artTool.Right, artTool.Up, artTool.Back))
        {
            return Matrix4x4.Identity;
        }

        return new Matrix4x4(
            artTool.Right.X, artTool.Up.X, artTool.Back.X, 0,
            artTool.Right.Y, artTool.Up.Y, artTool.Back.Y, 0,
            artTool.Right.Z, artTool.Up.Z, artTool.Back.Z, 0,
            0, 0, 0, 1);
    }

    private static (SceneBuilder Scene, Gr2ExportSummary Summary) Build(Gr2File file,
        IReadOnlyList<Gr2AnimationClip>? animations)
    {
        var basis = BasisMatrix(file.ArtToolInfo);
        var inverseBasis = Matrix4x4.Transpose(basis);
        var leftHanded = basis.GetDeterminant() < 0;
        var scene = new SceneBuilder(file.Name);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var models = 0;
        var meshes = 0;
        var skinned = 0;
        var bones = 0;
        var triangles = 0;
        var clips = 0;
        var animatedTracks = 0;
        var unwritten = 0;

        foreach (var model in file.Models)
        {
            if (model.Meshes.Count == 0 && model.Skeleton is null)
            {
                continue;
            }

            models++;
            var placement = new NodeBuilder(Unique(names, model.Name));
            placement.LocalMatrix = Conjugate(model.InitialPlacement, basis, inverseBasis);

            NodeBuilder[]? boneNodes = null;
            Matrix4x4[]? inverseBind = null;
            if (model.Skeleton is { } skeleton)
            {
                boneNodes = new NodeBuilder[skeleton.Bones.Count];
                inverseBind = new Matrix4x4[skeleton.Bones.Count];
                for (var index = 0; index < skeleton.Bones.Count; index++)
                {
                    var bone = skeleton.Bones[index];
                    var parent = bone.ParentIndex >= 0 ? boneNodes[bone.ParentIndex] : placement;
                    var node = parent.CreateNode(Unique(names, bone.Name));
                    node.LocalTransform = LocalTransform(bone, basis, inverseBasis);
                    boneNodes[index] = node;
                    if (!Matrix4x4.Invert(skeleton.WorldMatrices[index], out var inverseWorld))
                    {
                        throw new InvalidDataException($"{file.Name}: bone '{bone.Name}' has a singular bind matrix.");
                    }

                    inverseBind[index] = Conjugate(inverseWorld, basis, inverseBasis);
                }

                bones += skeleton.Bones.Count;
                if (animations is not null)
                {
                    foreach (var clip in animations)
                    {
                        var bound = BindAnimation(clip, skeleton, boneNodes, basis, inverseBasis, file.Name);
                        if (bound > 0)
                        {
                            clips++;
                            animatedTracks += bound;
                        }
                    }
                }
            }

            foreach (var mesh in model.Meshes)
            {
                if (mesh.Indices.Length == 0)
                {
                    if (mesh.TopologyDefect is not null)
                    {
                        unwritten++;
                    }

                    continue;
                }

                meshes++;
                triangles += mesh.TriangleCount;
                var palette = BonePalette(mesh, model.Skeleton);
                var canSkin = mesh.Influences is not null && boneNodes is not null && palette is not null;
                if (canSkin)
                {
                    skinned++;
                    var built = BuildSkinnedMesh(mesh, palette!, basis, leftHanded, names);
                    var joints = new (NodeBuilder, Matrix4x4)[boneNodes!.Length];
                    for (var index = 0; index < joints.Length; index++)
                    {
                        joints[index] = (boneNodes[index], inverseBind![index]);
                    }

                    scene.AddSkinnedMesh(built, joints);
                    continue;
                }

                var rigid = BuildRigidMesh(mesh, basis, leftHanded, names);
                var host = placement;
                if (boneNodes is not null && palette is { Length: 1 } && palette[0] >= 0)
                {
                    // A rigid mesh bound to exactly one bone rides that bone; its vertices are in
                    // model space, so the bone's inverse bind cancels the bone at the rest pose.
                    host = boneNodes[palette[0]];
                    scene.AddRigidMesh(rigid, host, new AffineTransform(inverseBind![palette[0]]));
                    continue;
                }

                scene.AddRigidMesh(rigid, host);
            }

            if (model.Meshes.Count == 0)
            {
                // A skeleton-only model: nothing above has put its nodes in the scene, so add the
                // placement (and with it the bone hierarchy) explicitly.
                scene.AddNode(placement);
            }
        }

        return (scene,
            new Gr2ExportSummary(models, meshes, skinned, bones, triangles, clips, animatedTracks, leftHanded,
                unwritten));
    }

    private static int[]? BonePalette(Gr2Mesh mesh, Gr2Skeleton? skeleton)
    {
        if (mesh.BoneBindings.Count == 0)
        {
            return [];
        }

        if (skeleton is null)
        {
            return null;
        }

        var palette = new int[mesh.BoneBindings.Count];
        for (var index = 0; index < palette.Length; index++)
        {
            palette[index] = skeleton.IndexOf(mesh.BoneBindings[index]);
            if (palette[index] < 0)
            {
                return null;
            }
        }

        return palette;
    }

    private static MeshBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4> BuildSkinnedMesh(
        Gr2Mesh mesh, int[] palette, Matrix4x4 basis, bool reverseWinding, HashSet<string> names)
    {
        var builder = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>(Unique(names, mesh.Name));
        var material = DefaultMaterial(mesh);
        var primitive = builder.UsePrimitive(material);
        for (var triangle = 0; triangle < mesh.TriangleCount; triangle++)
        {
            var a = mesh.Indices[triangle * 3];
            var b = mesh.Indices[triangle * 3 + 1];
            var c = mesh.Indices[triangle * 3 + 2];
            if (reverseWinding)
            {
                (b, c) = (c, b);
            }

            primitive.AddTriangle(SkinnedVertex(mesh, a, palette, basis), SkinnedVertex(mesh, b, palette, basis),
                SkinnedVertex(mesh, c, palette, basis));
        }

        return builder;
    }

    private static MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> BuildRigidMesh(
        Gr2Mesh mesh, Matrix4x4 basis, bool reverseWinding, HashSet<string> names)
    {
        var builder = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(Unique(names, mesh.Name));
        var material = DefaultMaterial(mesh);
        var primitive = builder.UsePrimitive(material);
        for (var triangle = 0; triangle < mesh.TriangleCount; triangle++)
        {
            var a = mesh.Indices[triangle * 3];
            var b = mesh.Indices[triangle * 3 + 1];
            var c = mesh.Indices[triangle * 3 + 2];
            if (reverseWinding)
            {
                (b, c) = (c, b);
            }

            primitive.AddTriangle(RigidVertex(mesh, a, basis), RigidVertex(mesh, b, basis),
                RigidVertex(mesh, c, basis));
        }

        return builder;
    }

    private static MaterialBuilder DefaultMaterial(Gr2Mesh mesh)
    {
        var name = mesh.MaterialNames.FirstOrDefault(static material => material is not null) ??
                   mesh.Name + "_material";
        return new MaterialBuilder(name).WithDoubleSide(true).WithMetallicRoughnessShader()
            .WithMetallicRoughness(0f, 1f);
    }

    private static (VertexPositionNormal, VertexTexture1, VertexJoints4) SkinnedVertex(Gr2Mesh mesh, int index,
        int[] palette, Matrix4x4 basis)
    {
        var (geometry, uv) = Geometry(mesh, index, basis);
        var lanes = mesh.Influences![index];
        Span<(int Joint, float Weight)> bindings = stackalloc (int, float)[4];
        var count = 0;
        var total = 0f;
        foreach (var lane in lanes)
        {
            if (lane.Weight <= 0 || lane.BoneBinding >= palette.Length)
            {
                continue;
            }

            var joint = palette[lane.BoneBinding];
            var existing = -1;
            for (var slot = 0; slot < count; slot++)
            {
                if (bindings[slot].Joint == joint)
                {
                    existing = slot;
                    break;
                }
            }

            if (existing >= 0)
            {
                bindings[existing].Weight += lane.Weight;
            }
            else if (count < 4)
            {
                bindings[count++] = (joint, lane.Weight);
            }

            total += lane.Weight;
        }

        VertexJoints4 joints;
        if (count == 0 || total <= 0)
        {
            joints = new VertexJoints4((palette.Length > 0 ? palette[0] : 0, 1f));
        }
        else
        {
            var normalised = new (int, float)[count];
            for (var slot = 0; slot < count; slot++)
            {
                normalised[slot] = (bindings[slot].Joint, bindings[slot].Weight / total);
            }

            joints = new VertexJoints4(normalised);
        }

        return (geometry, uv, joints);
    }

    private static (VertexPositionNormal, VertexTexture1) RigidVertex(Gr2Mesh mesh, int index, Matrix4x4 basis)
    {
        return Geometry(mesh, index, basis);
    }

    private static (VertexPositionNormal, VertexTexture1) Geometry(Gr2Mesh mesh, int index, Matrix4x4 basis)
    {
        var position = Vector3.Transform(mesh.Positions[index], basis);
        var normal = mesh.Normals is null ? Vector3.UnitY : Vector3.TransformNormal(mesh.Normals[index], basis);
        if (normal.LengthSquared() <= 1e-12f || !float.IsFinite(normal.LengthSquared()))
        {
            normal = Vector3.UnitY;
        }
        else
        {
            normal = Vector3.Normalize(normal);
        }

        var uv = mesh.TextureCoordinates is null ? Vector2.Zero : mesh.TextureCoordinates[index];
        return (new VertexPositionNormal(position, normal), new VertexTexture1(uv));
    }

    private static AffineTransform LocalTransform(Gr2Bone bone, Matrix4x4 basis, Matrix4x4 inverseBasis)
    {
        var local = Conjugate(bone.LocalMatrix, basis, inverseBasis);
        if (Matrix4x4.Decompose(local, out var scale, out var rotation, out var translation))
        {
            return new AffineTransform(scale, Quaternion.Normalize(rotation), translation);
        }

        // Shear cannot be expressed as a glTF node; keep rotation and translation and the diagonal scale.
        var scaleShear = Conjugate(bone.ScaleShear, basis, inverseBasis);
        var rotationMatrix = Conjugate(Matrix4x4.CreateFromQuaternion(bone.Rotation), basis, inverseBasis);
        return new AffineTransform(
            new Vector3(scaleShear.M11, scaleShear.M22, scaleShear.M33),
            Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rotationMatrix)),
            Vector3.Transform(bone.Translation, basis));
    }

    /// <summary>Re-expresses a row-vector transform in the glTF basis: <c>B⁻¹ · T · B</c>.</summary>
    private static Matrix4x4 Conjugate(Matrix4x4 transform, Matrix4x4 basis, Matrix4x4 inverseBasis)
    {
        return inverseBasis * transform * basis;
    }

    private static int BindAnimation(Gr2AnimationClip clip, Gr2Skeleton skeleton, NodeBuilder[] boneNodes,
        Matrix4x4 basis, Matrix4x4 inverseBasis, string fileName)
    {
        var bound = 0;
        foreach (var group in clip.TrackGroups)
        {
            if (!string.Equals(group.Name, skeleton.Name, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var track in group.TransformTracks)
            {
                var bone = skeleton.IndexOf(track.Name);
                if (bone < 0)
                {
                    continue;
                }

                var node = boneNodes[bone];
                var any = false;
                if (!track.Position.IsEmpty)
                {
                    RequireExportableCurve(track.Position, 3, clip, track, fileName);
                    var curve = node.UseTranslation(clip.Name);
                    var linear = track.Position.Degree == 1;
                    for (var knot = 0; knot < track.Position.Knots.Length; knot++)
                    {
                        var value = new Vector3(track.Position.Controls[knot * 3],
                            track.Position.Controls[knot * 3 + 1], track.Position.Controls[knot * 3 + 2]);
                        curve.SetPoint(track.Position.Knots[knot], Vector3.Transform(value, basis), linear);
                    }

                    any = true;
                }

                if (!track.Orientation.IsEmpty)
                {
                    RequireExportableCurve(track.Orientation, 4, clip, track, fileName);
                    var curve = node.UseRotation(clip.Name);
                    var linear = track.Orientation.Degree == 1;
                    Quaternion? previous = null;
                    for (var knot = 0; knot < track.Orientation.Knots.Length; knot++)
                    {
                        var raw = new Quaternion(
                            track.Orientation.Controls[knot * 4],
                            track.Orientation.Controls[knot * 4 + 1],
                            track.Orientation.Controls[knot * 4 + 2],
                            track.Orientation.Controls[knot * 4 + 3]);
                        var rotation = raw.LengthSquared() > 1e-12f ? Quaternion.Normalize(raw) : Quaternion.Identity;
                        var conjugated = Quaternion.Normalize(
                            Quaternion.CreateFromRotationMatrix(Conjugate(Matrix4x4.CreateFromQuaternion(rotation),
                                basis, inverseBasis)));
                        if (previous is { } last && Quaternion.Dot(last, conjugated) < 0)
                        {
                            conjugated = -conjugated;
                        }

                        previous = conjugated;
                        curve.SetPoint(track.Orientation.Knots[knot], conjugated, linear);
                    }

                    any = true;
                }

                if (any)
                {
                    bound++;
                }
            }
        }

        return bound;
    }

    private static void RequireExportableCurve(Gr2Curve curve, int dimension, Gr2AnimationClip clip,
        Gr2TransformTrack track, string fileName)
    {
        if (curve.Degree is not (0 or 1))
        {
            throw new NotSupportedException(
                $"{fileName}: animation '{clip.Name}' track '{track.Name}' is a degree-{curve.Degree} curve; only degree 0 (step) and 1 (linear) are exported.");
        }

        if (curve.Dimension != dimension)
        {
            throw new InvalidDataException(
                $"{fileName}: animation '{clip.Name}' track '{track.Name}' has {curve.Dimension} values per knot, expected {dimension}.");
        }
    }

    private static bool IsOrthonormalBasis(Vector3 right, Vector3 up, Vector3 back)
    {
        const float tolerance = 1e-3f;
        return MathF.Abs(right.Length() - 1) < tolerance && MathF.Abs(up.Length() - 1) < tolerance &&
               MathF.Abs(back.Length() - 1) < tolerance
               && MathF.Abs(Vector3.Dot(right, up)) < tolerance && MathF.Abs(Vector3.Dot(up, back)) < tolerance &&
               MathF.Abs(Vector3.Dot(right, back)) < tolerance;
    }

    private static string Unique(HashSet<string> names, string requested)
    {
        var stem = string.IsNullOrWhiteSpace(requested) ? "node" : requested;
        if (names.Add(stem))
        {
            return stem;
        }

        for (var suffix = 2; suffix < int.MaxValue; suffix++)
        {
            var candidate = stem + "_" + suffix.ToString(CultureInfo.InvariantCulture);
            if (names.Add(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not allocate a unique node name for {stem}.");
    }

    /// <summary>Counts of what the export did, for the CLI to report.</summary>
    internal sealed record Gr2ExportSummary(
        int Models,
        int Meshes,
        int SkinnedMeshes,
        int Bones,
        int Triangles,
        int AnimationClips,
        int AnimatedTracks,
        bool LeftHandedSource,
        int UnwrittenMeshes);
}
