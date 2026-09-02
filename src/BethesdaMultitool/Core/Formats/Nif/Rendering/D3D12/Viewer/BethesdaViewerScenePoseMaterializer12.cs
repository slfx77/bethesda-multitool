using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;

internal sealed record BethesdaViewerUnsupportedMeshPart12(
    int MeshPartIndex,
    string Name,
    string Reason);

/// <summary>
///     Current assembled scene pose, ready for the shared reference GPU materializer. Geometry is
///     expressed in one scene space: rigid parts have their node world transform baked, while
///     skinned parts use inverse-bind × current joint-world matrices. Boundary groups are applied
///     only after every part reaches that common space.
/// </summary>
internal sealed record BethesdaViewerPosedScene12(
    DecodedBethesdaViewerScene12 Source,
    DecodedNifMesh12 Mesh,
    BethesdaViewerBounds? Bounds,
    IReadOnlyList<string> WaterNormalTexturePaths,
    IReadOnlyList<BethesdaViewerUnsupportedMeshPart12> UnsupportedMeshParts,
    IReadOnlyList<string> Warnings,
    int StitchedVertexCount);

internal static class BethesdaViewerScenePoseMaterializer12
{
    private const float DirectionEpsilon = 1e-12f;

    internal static BethesdaViewerPosedScene12 Materialize(DecodedBethesdaViewerScene12 scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var unsupported = new List<BethesdaViewerUnsupportedMeshPart12>();
        var warnings = new List<string>();
        var verticesByPart = new GpuMeshUploader.GpuVertex[scene.MeshParts.Count][];
        var supported = new bool[scene.MeshParts.Count];
        var waterNormalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var linearSkinningFallbackPartCount = 0;
        var liveParticleSnapshotPartCount = 0;

        for (var partIndex = 0; partIndex < scene.MeshParts.Count; partIndex++)
        {
            var part = scene.MeshParts[partIndex];
            var submesh = part.Submesh;
            if (string.Equals(
                    submesh.DiffuseTexturePath,
                    RenderableSubmesh.WaterSurfaceTexturePath,
                    StringComparison.Ordinal))
            {
                AddPath(waterNormalPaths, submesh.NormalMapTexturePath);
                AddPath(waterNormalPaths, part.NativeSemantics.ShaderMetadata?.NormalMapPath);
            }

            if (submesh.Vertices.Length == 0 || submesh.Indices.Length == 0)
            {
                verticesByPart[partIndex] = [];
                supported[partIndex] = true;
                continue;
            }

            if (submesh.Indices.Any(index => index >= submesh.Vertices.Length))
            {
                Reject(partIndex, part.Name, "triangle indices address vertices outside the mesh part");
                continue;
            }

            try
            {
                var vertices = (GpuMeshUploader.GpuVertex[])submesh.Vertices.Clone();
                if (part.Skin is { } skin)
                {
                    if (SkinCurrentPose(scene, partIndex, part.Name, skin, vertices))
                    {
                        linearSkinningFallbackPartCount++;
                    }
                }
                else
                {
                    var world = ResolveRigidWorld(scene, partIndex, part.Name, part.NodeIndex);
                    TransformRigid(vertices, world);
                }

                ApplyNativeTint(
                    vertices,
                    part.NativeSemantics.TintColor,
                    submesh.StarfieldMaterialColor.IsVertexLerp);
                verticesByPart[partIndex] = vertices;
                supported[partIndex] = true;
                if (submesh.ParticleRuntime is not null)
                {
                    liveParticleSnapshotPartCount++;
                }
            }
            catch (InvalidDataException ex)
            {
                Reject(partIndex, part.Name, ex.Message);
            }
        }

        var eyeEnvmapPartCount = 0;
        var faceGenSubsurfacePartCount = 0;
        var nonDedicatedSkyPartCount = 0;
        for (var partIndex = 0; partIndex < scene.MeshParts.Count; partIndex++)
        {
            if (!supported[partIndex] || verticesByPart[partIndex].Length == 0)
            {
                continue;
            }

            var native = scene.MeshParts[partIndex].NativeSemantics;
            if (native.IsEyeEnvmap)
            {
                eyeEnvmapPartCount++;
            }
            if (native.IsFaceGen || native.SubsurfaceColor != default)
            {
                faceGenSubsurfacePartCount++;
            }
            if (native.SkyType is not null &&
                !BethesdaViewerNativeSkyPolicy.IsDedicatedRawNifLayer(
                    scene.Purpose,
                    native.SkyType))
            {
                nonDedicatedSkyPartCount++;
            }
        }
        if (eyeEnvmapPartCount > 0)
        {
            warnings.Add(
                $"{eyeEnvmapPartCount} eye-environment part(s) retain authored textures and material state, but the shared reference shader has no dedicated eye-reflection lane; eye env-map scale is approximate.");
        }
        if (faceGenSubsurfacePartCount > 0)
        {
            warnings.Add(
                $"{faceGenSubsurfacePartCount} FaceGen/subsurface part(s) use the shared lit-material shader; dedicated skin light transmission is not yet represented.");
        }
        if (nonDedicatedSkyPartCount > 0)
        {
            warnings.Add(
                $"{nonDedicatedSkyPartCount} sky-tagged part(s) are outside the exact raw-NIF Sky/Stars/Clouds route and remain assembled scene geometry; camera centering is disabled.");
        }
        if (linearSkinningFallbackPartCount > 0)
        {
            warnings.Add(
                $"{linearSkinningFallbackPartCount} skinned part(s) contained non-rigid bone transforms and used the established linear-skinning fallback.");
        }
        if (liveParticleSnapshotPartCount > 0)
        {
            warnings.Add(
                $"{liveParticleSnapshotPartCount} live-particle part(s) render their decoded static snapshot; particle simulation is not active in this session.");
        }

        var stitchedVertexCount = ApplyBoundaryStitchGroups(
            scene,
            verticesByPart,
            supported,
            warnings);
        // A camera-centred dome has no scene-space extent. Including its authored radius here makes
        // FrameScene pull an ordinary mesh (or an otherwise-empty Stars.nif) kilometres away. The
        // same narrow policy that selects the dedicated renderer therefore owns the bounds exclusion;
        // NPC/creature parts carrying a sky tag remain ordinary assembled geometry and stay bounded.
        var posedBounds = ResolveAggregateBounds(scene, verticesByPart, supported);

        var posedSubmeshes = new DecodedSubmesh12[scene.MeshParts.Count];
        for (var partIndex = 0; partIndex < posedSubmeshes.Length; partIndex++)
        {
            var source = scene.MeshParts[partIndex].Submesh;
            if (!supported[partIndex])
            {
                posedSubmeshes[partIndex] = source with
                {
                    Vertices = [],
                    Indices = [],
                    LocalBoundsCenter = Vector3.Zero,
                    LocalBoundsRadius = 0f,
                };
                continue;
            }

            var vertices = verticesByPart[partIndex];
            ResolveBounds(vertices, out var center, out var radius);
            var effectTint = ResolveEffectTint(
                source.EffectTint,
                scene.MeshParts[partIndex].NativeSemantics.TintColor);
            posedSubmeshes[partIndex] = source with
            {
                Vertices = vertices,
                LocalBoundsCenter = center,
                LocalBoundsRadius = radius,
                EffectTint = effectTint,
                EffectTintSpecified = source.EffectTintSpecified ||
                                      scene.MeshParts[partIndex].NativeSemantics.TintColor is not null,
                // The persistent upload is the assembled rest/current pose. Viewer animation keeps
                // its node-indexed skin beside this payload and supplies a frame-ring VBV override;
                // the placed-world skinner's differently-indexed skin contract stays unset here.
                Skin = null,
            };
        }

        return new BethesdaViewerPosedScene12(
            scene,
            new DecodedNifMesh12(
                posedSubmeshes,
                ContainsParticleSource: scene.Mesh.ContainsParticleSource),
            posedBounds,
            waterNormalPaths.ToArray(),
            unsupported,
            warnings,
            stitchedVertexCount);

        void Reject(int index, string name, string reason)
        {
            verticesByPart[index] = [];
            supported[index] = false;
            unsupported.Add(new BethesdaViewerUnsupportedMeshPart12(index, name, reason));
        }
    }

    private static Matrix4x4 ResolveRigidWorld(
        DecodedBethesdaViewerScene12 scene,
        int partIndex,
        string partName,
        int? nodeIndex)
    {
        if (nodeIndex is null)
        {
            return Matrix4x4.Identity;
        }

        if ((uint)nodeIndex.Value >= (uint)scene.Nodes.Count)
        {
            throw new InvalidDataException(
                $"node index {nodeIndex.Value} is invalid for mesh part {partIndex} ('{partName}')");
        }

        var world = scene.Nodes[nodeIndex.Value].WorldTransform;
        if (!IsFinite(world))
        {
            throw new InvalidDataException("node world transform contains non-finite values");
        }

        return world;
    }

    /// <returns>True when non-rigid matrices required the established linear fallback.</returns>
    private static bool SkinCurrentPose(
        DecodedBethesdaViewerScene12 scene,
        int partIndex,
        string partName,
        DecodedBethesdaViewerSkinBinding12 skin,
        Span<GpuMeshUploader.GpuVertex> vertices)
    {
        if (skin.JointNodeIndices.Length == 0 ||
            skin.JointNodeIndices.Length != skin.InverseBindMatrices.Length)
        {
            throw new InvalidDataException(
                "skin joint and inverse-bind counts are empty or inconsistent");
        }

        if (skin.PerVertexInfluences.Length != vertices.Length)
        {
            throw new InvalidDataException(
                $"skin influence count {skin.PerVertexInfluences.Length} does not match {vertices.Length} vertices");
        }

        var skinMatrices = new Matrix4x4[skin.JointNodeIndices.Length];
        for (var boneIndex = 0; boneIndex < skinMatrices.Length; boneIndex++)
        {
            var jointNodeIndex = skin.JointNodeIndices[boneIndex];
            if ((uint)jointNodeIndex >= (uint)scene.Nodes.Count)
            {
                throw new InvalidDataException(
                    $"skin joint {boneIndex} addresses invalid node {jointNodeIndex} on part {partIndex} ('{partName}')");
            }

            var inverseBind = skin.InverseBindMatrices[boneIndex];
            var jointWorld = scene.Nodes[jointNodeIndex].WorldTransform;
            if (!IsFinite(inverseBind) || !IsFinite(jointWorld))
            {
                throw new InvalidDataException(
                    $"skin joint {boneIndex} contains a non-finite transform");
            }

            // System.Numerics uses row-vector composition: source * inverseBind * jointWorld.
            skinMatrices[boneIndex] = inverseBind * jointWorld;
        }

        (int BoneIdx, float Weight)[][]? filteredInfluences = null;
        for (var vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
        {
            var influences = skin.PerVertexInfluences[vertexIndex];
            var positiveCount = 0;
            foreach (var (boneIndex, weight) in influences)
            {
                if ((uint)boneIndex >= (uint)skinMatrices.Length || !float.IsFinite(weight) || weight < 0f)
                {
                    throw new InvalidDataException(
                        $"vertex {vertexIndex} has an invalid bone influence");
                }
                if (weight > 0f)
                {
                    positiveCount++;
                }
            }

            if (positiveCount != influences.Length)
            {
                filteredInfluences ??=
                    ((int BoneIdx, float Weight)[][])skin.PerVertexInfluences.Clone();
                var positive = new (int BoneIdx, float Weight)[positiveCount];
                var destination = 0;
                foreach (var influence in influences)
                {
                    if (influence.Weight > 0f)
                    {
                        positive[destination++] = influence;
                    }
                }
                filteredInfluences[vertexIndex] = positive;
            }
        }
        var effectiveInfluences = filteredInfluences ?? skin.PerVertexInfluences;

        var positions = new float[vertices.Length * 3];
        var normals = new float[vertices.Length * 3];
        var tangents = new float[vertices.Length * 3];
        var bitangents = new float[vertices.Length * 3];
        for (var vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
        {
            var offset = vertexIndex * 3;
            var vertex = vertices[vertexIndex];
            WriteVector(positions, offset, vertex.Position);
            WriteVector(normals, offset, vertex.Normal);
            WriteVector(tangents, offset, vertex.Tangent);
            WriteVector(bitangents, offset, vertex.Bitangent);
        }

        var compatibility = NifSkinningMath.AnalyzeDualQuaternionCompatibility(skinMatrices);
        var useDualQuaternionSkinning = compatibility.CanUse;
        var posedPositions = useDualQuaternionSkinning
            ? NifSkinningMath.ApplySkinningPositionsDqs(
                positions, effectiveInfluences, skinMatrices)
            : NifSkinningMath.ApplySkinningPositions(
                positions, effectiveInfluences, skinMatrices);
        var posedNormals = useDualQuaternionSkinning
            ? NifSkinningMath.ApplySkinningNormalsDqs(
                normals, effectiveInfluences, skinMatrices)
            : NifSkinningMath.ApplySkinningNormals(
                normals, effectiveInfluences, skinMatrices);
        var posedTangents = useDualQuaternionSkinning
            ? NifSkinningMath.ApplySkinningNormalsDqs(
                tangents, effectiveInfluences, skinMatrices)
            : NifSkinningMath.ApplySkinningNormals(
                tangents, effectiveInfluences, skinMatrices);
        var posedBitangents = useDualQuaternionSkinning
            ? NifSkinningMath.ApplySkinningNormalsDqs(
                bitangents, effectiveInfluences, skinMatrices)
            : NifSkinningMath.ApplySkinningNormals(
                bitangents, effectiveInfluences, skinMatrices);

        for (var vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
        {
            var offset = vertexIndex * 3;
            var position = ReadVector(posedPositions, offset);
            var normal = ReadVector(posedNormals, offset);
            var tangent = ReadVector(posedTangents, offset);
            var bitangent = ReadVector(posedBitangents, offset);
            if (!IsFinite(position) || !IsFinite(normal) || !IsFinite(tangent) || !IsFinite(bitangent))
            {
                throw new InvalidDataException(
                    $"vertex {vertexIndex} produced a non-finite skinned pose");
            }

            vertices[vertexIndex].Position = position;
            vertices[vertexIndex].Normal = normal;
            vertices[vertexIndex].Tangent = tangent;
            vertices[vertexIndex].Bitangent = bitangent;
        }

        return !useDualQuaternionSkinning;

        static void WriteVector(float[] target, int offset, Vector3 value)
        {
            target[offset] = value.X;
            target[offset + 1] = value.Y;
            target[offset + 2] = value.Z;
        }

        static Vector3 ReadVector(float[] source, int offset) =>
            new(source[offset], source[offset + 1], source[offset + 2]);
    }

    internal static void TransformRigid(
        Span<GpuMeshUploader.GpuVertex> vertices,
        in Matrix4x4 world)
    {
        // Preserve the exact authored N/T/B payload on the overwhelmingly common identity path.
        // SpeedTree and classic basic-bump shaders intentionally encode data in vector magnitude.
        if (world == Matrix4x4.Identity)
        {
            return;
        }

        for (var index = 0; index < vertices.Length; index++)
        {
            var vertex = vertices[index];
            vertex.Position = Vector3.Transform(vertex.Position, world);
            vertex.Normal = TransformDirectionPreservingMagnitude(vertex.Normal, world);
            vertex.Tangent = TransformDirectionPreservingMagnitude(vertex.Tangent, world);
            vertex.Bitangent = TransformDirectionPreservingMagnitude(vertex.Bitangent, world);
            if (!IsFinite(vertex.Position) ||
                !IsFinite(vertex.Normal) ||
                !IsFinite(vertex.Tangent) ||
                !IsFinite(vertex.Bitangent))
            {
                throw new InvalidDataException("rigid node transform produced non-finite geometry");
            }

            vertices[index] = vertex;
        }
    }

    private static Vector3 TransformDirectionPreservingMagnitude(
        Vector3 source,
        in Matrix4x4 transform)
    {
        var transformed = Vector3.TransformNormal(source, transform);
        var sourceLength = source.Length();
        var transformedLength = transformed.Length();
        if (float.IsFinite(sourceLength) &&
            float.IsFinite(transformedLength) &&
            transformedLength > 0.001f)
        {
            transformed *= sourceLength / transformedLength;
        }

        return transformed;
    }

    internal static int ApplyBoundaryStitchGroups(
        DecodedBethesdaViewerScene12 scene,
        GpuMeshUploader.GpuVertex[][] verticesByPart,
        bool[] supported,
        List<string>? warnings)
    {
        var stitched = 0;
        for (var groupIndex = 0; groupIndex < scene.BoundaryStitchGroups.Count; groupIndex++)
        {
            var group = scene.BoundaryStitchGroups[groupIndex];
            if (group.Vertices.Length < 2)
            {
                continue;
            }

            var valid = true;
            var average = Vector3.Zero;
            foreach (var address in group.Vertices)
            {
                if ((uint)address.MeshPartIndex >= (uint)verticesByPart.Length ||
                    !supported[address.MeshPartIndex] ||
                    (uint)address.VertexIndex >= (uint)verticesByPart[address.MeshPartIndex].Length)
                {
                    valid = false;
                    break;
                }

                average += verticesByPart[address.MeshPartIndex][address.VertexIndex].Position;
            }

            if (!valid)
            {
                warnings?.Add(
                    $"Boundary stitch group {groupIndex} was skipped because it addresses an unsupported mesh vertex.");
                continue;
            }

            average /= group.Vertices.Length;
            foreach (var address in group.Vertices)
            {
                verticesByPart[address.MeshPartIndex][address.VertexIndex].Position = average;
            }

            stitched += group.Vertices.Length;
        }

        return stitched;
    }

    internal static void ApplyNativeTint(
        Span<GpuMeshUploader.GpuVertex> vertices,
        (float R, float G, float B)? tint,
        bool preserveRawVertexColor = false)
    {
        if (tint is null || preserveRawVertexColor)
        {
            return;
        }

        // NPC tint-receiving shader families substitute tint × texture and ignore authored vertex
        // colour/AO. This is the same rule as NpcGlbTintColorEncoder, but stays in native material
        // constants instead of baking an archive texture.
        for (var index = 0; index < vertices.Length; index++)
        {
            // Preserve authored coverage alpha exactly. Starfield vertex-Lerp bypasses this method
            // entirely because all four color bytes are shader material data on that route.
            vertices[index].VertexColorRgba =
                (vertices[index].VertexColorRgba & 0xFF000000u) | 0x00FFFFFFu;
        }
    }

    private static Vector3 ResolveEffectTint(
        Vector3 effectTint,
        (float R, float G, float B)? nativeTint)
    {
        if (nativeTint is not { } tint)
        {
            return effectTint;
        }

        // The native NPC shader doubles HCLR without saturating the constant. Values above one are
        // intentional highlights; glTF clamps only because its base-color channel cannot represent
        // the separate Bethesda tint multiplier.
        var encoded = new Vector3(
            MathF.Max(tint.R * 2f, 0f),
            MathF.Max(tint.G * 2f, 0f),
            MathF.Max(tint.B * 2f, 0f));
        return effectTint * encoded;
    }

    private static void ResolveBounds(
        ReadOnlySpan<GpuMeshUploader.GpuVertex> vertices,
        out Vector3 center,
        out float radius)
    {
        if (vertices.Length == 0)
        {
            center = Vector3.Zero;
            radius = 0f;
            return;
        }

        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        foreach (var vertex in vertices)
        {
            minimum = Vector3.Min(minimum, vertex.Position);
            maximum = Vector3.Max(maximum, vertex.Position);
        }

        center = (minimum + maximum) * 0.5f;
        var radiusSquared = 0f;
        foreach (var vertex in vertices)
        {
            radiusSquared = MathF.Max(radiusSquared, Vector3.DistanceSquared(center, vertex.Position));
        }

        radius = MathF.Sqrt(radiusSquared);
    }

    private static BethesdaViewerBounds? ResolveAggregateBounds(
        DecodedBethesdaViewerScene12 scene,
        GpuMeshUploader.GpuVertex[][] verticesByPart,
        bool[] supported)
    {
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        var foundVertex = false;
        for (var partIndex = 0; partIndex < verticesByPart.Length; partIndex++)
        {
            if (!supported[partIndex])
            {
                continue;
            }

            if (BethesdaViewerNativeSkyPolicy.IsDedicatedRawNifLayer(
                    scene.Purpose,
                    scene.MeshParts[partIndex].NativeSemantics.SkyType))
            {
                continue;
            }

            foreach (var vertex in verticesByPart[partIndex])
            {
                minimum = Vector3.Min(minimum, vertex.Position);
                maximum = Vector3.Max(maximum, vertex.Position);
                foundVertex = true;
            }
        }

        return foundVertex ? new BethesdaViewerBounds(minimum, maximum) : null;
    }

    private static Vector3 NormalizeOrZero(Vector3 value)
    {
        var lengthSquared = value.LengthSquared();
        return float.IsFinite(lengthSquared) && lengthSquared > DirectionEpsilon
            ? value / MathF.Sqrt(lengthSquared)
            : Vector3.Zero;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
        float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
        float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) &&
        float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) &&
        float.IsFinite(value.M43) && float.IsFinite(value.M44);

    private static void AddPath(HashSet<string> paths, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            paths.Add(path);
        }
    }
}
