#if WINDOWS_GUI
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;

/// <summary>
///     Per-frame native-viewer pose streamer. Static material/index resources remain in the scene
///     mesh; only parts affected by the selected node clip (plus seam-connected parts) are rewritten
///     into the shared frame ring and exposed through <see cref="CachedSubmesh12.AnimatedVertexBufferView" />.
/// </summary>
internal sealed class BethesdaViewerAnimatedPose12
{
    private readonly BethesdaViewerAnimationPoseEvaluator _evaluator;
    private readonly CachedNifMesh12 _mesh;
    private readonly Matrix4x4[] _nodeWorlds;
    private readonly uint[] _partByteOffsets;
    private readonly PartScratch[] _parts;
    private readonly uint _totalUploadBytes;
    private readonly GpuMeshUploader.GpuVertex[][] _verticesByPart;
    private readonly bool[] _supportedParts;
    private readonly DecodedBethesdaViewerScene12 _scene;

    internal BethesdaViewerAnimatedPose12(
        DecodedBethesdaViewerScene12 scene,
        CachedNifMesh12 mesh,
        BethesdaViewerAnimationClip clip)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(clip);

        _scene = scene;
        _mesh = mesh;
        _nodeWorlds = new Matrix4x4[scene.Nodes.Count];
        _evaluator = new BethesdaViewerAnimationPoseEvaluator(
            scene.Nodes.Select(static node => node.LocalTransform).ToArray(),
            scene.Nodes.Select(static node => node.ParentIndex).ToArray(),
            clip);

        var affectedNodes = ResolveAffectedNodes(scene, clip);
        var affectedParts = ResolveAffectedParts(scene, affectedNodes);
        ExpandBoundaryClosure(scene, affectedParts);

        var cachedBySource = new CachedSubmesh12?[scene.MeshParts.Count];
        foreach (var cached in mesh.Submeshes)
        {
            var sourceIndex = cached.MaterializationSourceIndex;
            if ((uint)sourceIndex < (uint)cachedBySource.Length && cached.IndexCount > 0)
            {
                cachedBySource[sourceIndex] = cached;
            }
        }

        var parts = new List<PartScratch>();
        _verticesByPart = new GpuMeshUploader.GpuVertex[scene.MeshParts.Count][];
        _supportedParts = new bool[scene.MeshParts.Count];
        for (var partIndex = 0; partIndex < scene.MeshParts.Count; partIndex++)
        {
            var source = scene.MeshParts[partIndex];
            var cached = cachedBySource[partIndex];
            if (!affectedParts[partIndex] ||
                cached is null ||
                BethesdaViewerNativeSkyPolicy.IsDedicatedRawNifLayer(
                    scene.Purpose,
                    source.NativeSemantics.SkyType) ||
                source.Submesh.Vertices.Length == 0 ||
                source.Submesh.Indices.Length == 0)
            {
                // Dedicated raw sky copies its own camera-centred geometry and never consumes the
                // cached submesh VBV. Do not claim playback for a clip whose only affected geometry
                // is on that route. Water and other diverted geometry have no cached source entry.
                _verticesByPart[partIndex] = [];
                continue;
            }

            var scratch = new PartScratch(
                partIndex,
                source.NodeIndex,
                cached,
                source.Submesh.Vertices,
                source.Skin,
                source.NativeSemantics.TintColor,
                source.Submesh.StarfieldMaterialColor.IsVertexLerp,
                scene.Nodes.Count);
            parts.Add(scratch);
            _verticesByPart[partIndex] = scratch.WorkingVertices;
            _supportedParts[partIndex] = true;
        }

        _parts = parts.ToArray();
        _partByteOffsets = new uint[_parts.Length];
        var uploadBytes = 0u;
        for (var index = 0; index < _parts.Length; index++)
        {
            uploadBytes = AlignUp(uploadBytes, 16u);
            _partByteOffsets[index] = uploadBytes;
            uploadBytes = checked(uploadBytes + VertexByteCount(_parts[index]));
        }
        _totalUploadBytes = uploadBytes;
    }

    internal BethesdaViewerAnimationClip Clip => _evaluator.Clip;

    internal bool HasAnimatedGeometry => _parts.Length > 0;

    /// <summary>
    ///     Clears stale ring addresses, evaluates one pose, and publishes all affected VBVs only
    ///     after every ring allocation succeeds. A tight ring therefore falls back coherently to
    ///     the static pose instead of tearing seam-connected parts across two times.
    /// </summary>
    internal bool Update(
        int frameIndex,
        GpuRingBuffer12 ring,
        float clockSeconds,
        bool enabled)
    {
        foreach (var cached in _mesh.Submeshes)
        {
            cached.AnimatedVertexBufferView = null;
        }

        if (!enabled || _parts.Length == 0)
        {
            return false;
        }

        _evaluator.EvaluateNodeWorlds(clockSeconds, _nodeWorlds);
        foreach (var part in _parts)
        {
            part.Pose(_nodeWorlds);
        }

        BethesdaViewerScenePoseMaterializer12.ApplyBoundaryStitchGroups(
            _scene,
            _verticesByPart,
            _supportedParts,
            warnings: null);

        // One batch reservation makes ring admission atomic. If it cannot fit, TryAllocate leaves
        // the bump pointer untouched so the static material passes keep their constant-buffer
        // budget; a series of successful part allocations followed by one failure would not.
        if (!ring.TryAllocate(
                frameIndex,
                _totalUploadBytes,
                out var upload,
                alignment: 16))
        {
            return false;
        }

        for (var index = 0; index < _parts.Length; index++)
        {
            var part = _parts[index];
            var byteOffset = _partByteOffsets[index];
            var byteCount = VertexByteCount(part);
            unsafe
            {
                var destination = new Span<GpuMeshUploader.GpuVertex>(
                    (void*)(upload.CpuPtr + checked((int)byteOffset)),
                    part.WorkingVertices.Length);
                part.WorkingVertices.AsSpan().CopyTo(destination);
            }

            part.CachedSubmesh.AnimatedVertexBufferView = new VertexBufferView
            {
                BufferLocation = upload.GpuAddress + byteOffset,
                SizeInBytes = byteCount,
                StrideInBytes = (uint)GpuMeshUploader.GpuVertexSize
            };
        }

        return true;
    }

    private static uint VertexByteCount(PartScratch part)
    {
        return checked((uint)(part.WorkingVertices.Length * GpuMeshUploader.GpuVertexSize));
    }

    private static uint AlignUp(uint value, uint alignment)
    {
        return checked(value + alignment - 1u) & ~(alignment - 1u);
    }

    private static bool[] ResolveAffectedNodes(
        DecodedBethesdaViewerScene12 scene,
        BethesdaViewerAnimationClip clip)
    {
        var affected = new bool[scene.Nodes.Count];
        foreach (var track in clip.NodeTracks)
        {
            if ((uint)track.NodeIndex < (uint)affected.Length)
            {
                affected[track.NodeIndex] = true;
            }
        }

        for (var nodeIndex = 0; nodeIndex < scene.Nodes.Count; nodeIndex++)
        {
            var parentIndex = scene.Nodes[nodeIndex].ParentIndex;
            if (parentIndex is int parent && (uint)parent < (uint)nodeIndex && affected[parent])
            {
                affected[nodeIndex] = true;
            }
        }

        return affected;
    }

    private static bool[] ResolveAffectedParts(
        DecodedBethesdaViewerScene12 scene,
        bool[] affectedNodes)
    {
        var affected = new bool[scene.MeshParts.Count];
        for (var partIndex = 0; partIndex < scene.MeshParts.Count; partIndex++)
        {
            var part = scene.MeshParts[partIndex];
            if (part.Skin is { } skin)
            {
                affected[partIndex] = skin.JointNodeIndices.Any(
                    nodeIndex => (uint)nodeIndex < (uint)affectedNodes.Length && affectedNodes[nodeIndex]);
            }
            else
            {
                var nodeIndex = part.NodeIndex ?? BethesdaViewerScene.RootNodeIndex;
                affected[partIndex] = (uint)nodeIndex < (uint)affectedNodes.Length &&
                                      affectedNodes[nodeIndex];
            }
        }

        return affected;
    }

    private static void ExpandBoundaryClosure(
        DecodedBethesdaViewerScene12 scene,
        bool[] affectedParts)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var group in scene.BoundaryStitchGroups)
            {
                var touchesAnimation = group.Vertices.Any(address =>
                    (uint)address.MeshPartIndex < (uint)affectedParts.Length &&
                    affectedParts[address.MeshPartIndex]);
                if (!touchesAnimation)
                {
                    continue;
                }

                foreach (var address in group.Vertices)
                {
                    if ((uint)address.MeshPartIndex < (uint)affectedParts.Length &&
                        !affectedParts[address.MeshPartIndex])
                    {
                        affectedParts[address.MeshPartIndex] = true;
                        changed = true;
                    }
                }
            }
        }
    }

    private sealed class PartScratch
    {
        private readonly int? _nodeIndex;
        private readonly GpuMeshUploader.GpuVertex[] _sourceVertices;
        private readonly SkinScratch? _skin;
        private readonly (float R, float G, float B)? _tint;
        private readonly bool _preserveRawVertexColor;

        internal PartScratch(
            int meshPartIndex,
            int? nodeIndex,
            CachedSubmesh12 cachedSubmesh,
            GpuMeshUploader.GpuVertex[] sourceVertices,
            DecodedBethesdaViewerSkinBinding12? skin,
            (float R, float G, float B)? tint,
            bool preserveRawVertexColor,
            int nodeCount)
        {
            MeshPartIndex = meshPartIndex;
            _nodeIndex = nodeIndex;
            CachedSubmesh = cachedSubmesh;
            _sourceVertices = sourceVertices;
            _tint = tint;
            _preserveRawVertexColor = preserveRawVertexColor;
            WorkingVertices = new GpuMeshUploader.GpuVertex[sourceVertices.Length];
            _skin = skin is null ? null : new SkinScratch(skin, sourceVertices, nodeCount);
        }

        internal int MeshPartIndex { get; }

        internal CachedSubmesh12 CachedSubmesh { get; }

        internal GpuMeshUploader.GpuVertex[] WorkingVertices { get; }

        internal void Pose(ReadOnlySpan<Matrix4x4> nodeWorlds)
        {
            _sourceVertices.AsSpan().CopyTo(WorkingVertices);
            if (_skin is not null)
            {
                _skin.Pose(nodeWorlds, WorkingVertices);
            }
            else
            {
                var nodeIndex = _nodeIndex ?? BethesdaViewerScene.RootNodeIndex;
                if ((uint)nodeIndex >= (uint)nodeWorlds.Length)
                {
                    throw new InvalidDataException(
                        $"Animated mesh part {MeshPartIndex} addresses invalid node {nodeIndex}.");
                }

                BethesdaViewerScenePoseMaterializer12.TransformRigid(
                    WorkingVertices,
                    nodeWorlds[nodeIndex]);
            }

            BethesdaViewerScenePoseMaterializer12.ApplyNativeTint(
                WorkingVertices,
                _tint,
                _preserveRawVertexColor);
            for (var vertexIndex = 0; vertexIndex < WorkingVertices.Length; vertexIndex++)
            {
                var vertex = WorkingVertices[vertexIndex];
                if (!IsFinite(vertex.Position) ||
                    !IsFinite(vertex.Normal) ||
                    !IsFinite(vertex.Tangent) ||
                    !IsFinite(vertex.Bitangent))
                {
                    throw new InvalidDataException(
                        $"Animated mesh part {MeshPartIndex}, vertex {vertexIndex} produced non-finite geometry.");
                }
            }
        }

        private static bool IsFinite(Vector3 value)
        {
            return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
        }
    }

    private sealed class SkinScratch
    {
        private readonly float[] _baseBitangents;
        private readonly float[] _baseNormals;
        private readonly float[] _basePositions;
        private readonly float[] _baseTangents;
        private readonly NifSkinningMath.DualQuaternion[] _dualQuaternions;
        private readonly (int BoneIdx, float Weight)[][] _influences;
        private readonly Matrix4x4[] _inverseBindMatrices;
        private readonly int[] _jointNodeIndices;
        private readonly float[] _posedBitangents;
        private readonly float[] _posedNormals;
        private readonly float[] _posedPositions;
        private readonly float[] _posedTangents;
        private readonly Matrix4x4[] _skinMatrices;

        internal SkinScratch(
            DecodedBethesdaViewerSkinBinding12 skin,
            GpuMeshUploader.GpuVertex[] vertices,
            int nodeCount)
        {
            if (skin.JointNodeIndices.Length == 0 ||
                skin.JointNodeIndices.Length != skin.InverseBindMatrices.Length)
            {
                throw new InvalidDataException("Animation skin has inconsistent joint/inverse-bind counts.");
            }

            if (skin.PerVertexInfluences.Length != vertices.Length)
            {
                throw new InvalidDataException("Animation skin influence count does not match its vertices.");
            }

            _jointNodeIndices = skin.JointNodeIndices;
            _inverseBindMatrices = skin.InverseBindMatrices;
            foreach (var nodeIndex in _jointNodeIndices)
            {
                if ((uint)nodeIndex >= (uint)nodeCount)
                {
                    throw new InvalidDataException($"Animation skin addresses invalid joint node {nodeIndex}.");
                }
            }

            _influences = FilterInfluences(skin.PerVertexInfluences, _jointNodeIndices.Length);
            _skinMatrices = new Matrix4x4[_jointNodeIndices.Length];
            _dualQuaternions = new NifSkinningMath.DualQuaternion[_jointNodeIndices.Length];
            var streamLength = vertices.Length * 3;
            _basePositions = new float[streamLength];
            _baseNormals = new float[streamLength];
            _baseTangents = new float[streamLength];
            _baseBitangents = new float[streamLength];
            _posedPositions = new float[streamLength];
            _posedNormals = new float[streamLength];
            _posedTangents = new float[streamLength];
            _posedBitangents = new float[streamLength];
            for (var vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
            {
                Write(_basePositions, vertexIndex, vertices[vertexIndex].Position);
                Write(_baseNormals, vertexIndex, vertices[vertexIndex].Normal);
                Write(_baseTangents, vertexIndex, vertices[vertexIndex].Tangent);
                Write(_baseBitangents, vertexIndex, vertices[vertexIndex].Bitangent);
            }
        }

        internal void Pose(
            ReadOnlySpan<Matrix4x4> nodeWorlds,
            Span<GpuMeshUploader.GpuVertex> vertices)
        {
            for (var jointIndex = 0; jointIndex < _jointNodeIndices.Length; jointIndex++)
            {
                _skinMatrices[jointIndex] =
                    _inverseBindMatrices[jointIndex] * nodeWorlds[_jointNodeIndices[jointIndex]];
            }

            if (NifSkinningMath.AnalyzeDualQuaternionCompatibility(_skinMatrices).CanUse)
            {
                NifSkinningMath.BuildDualQuaternions(_skinMatrices, _dualQuaternions);
                NifSkinningMath.ApplySkinningPositionsDqs(
                    _basePositions, _influences, _dualQuaternions, _posedPositions);
                NifSkinningMath.ApplySkinningNormalsDqs(
                    _baseNormals, _influences, _dualQuaternions, _posedNormals);
                NifSkinningMath.ApplySkinningNormalsDqs(
                    _baseTangents, _influences, _dualQuaternions, _posedTangents);
                NifSkinningMath.ApplySkinningNormalsDqs(
                    _baseBitangents, _influences, _dualQuaternions, _posedBitangents);
            }
            else
            {
                NifSkinningMath.ApplySkinningPositions(
                    _basePositions, _influences, _skinMatrices, _posedPositions);
                NifSkinningMath.ApplySkinningNormals(
                    _baseNormals, _influences, _skinMatrices, _posedNormals);
                NifSkinningMath.ApplySkinningNormals(
                    _baseTangents, _influences, _skinMatrices, _posedTangents);
                NifSkinningMath.ApplySkinningNormals(
                    _baseBitangents, _influences, _skinMatrices, _posedBitangents);
            }

            for (var vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
            {
                var position = Read(_posedPositions, vertexIndex);
                var normal = Read(_posedNormals, vertexIndex);
                var tangent = Read(_posedTangents, vertexIndex);
                var bitangent = Read(_posedBitangents, vertexIndex);
                if (!IsFinite(position) || !IsFinite(normal) ||
                    !IsFinite(tangent) || !IsFinite(bitangent))
                {
                    throw new InvalidDataException(
                        $"Animated vertex {vertexIndex} produced a non-finite pose.");
                }

                vertices[vertexIndex].Position = position;
                vertices[vertexIndex].Normal = normal;
                vertices[vertexIndex].Tangent = tangent;
                vertices[vertexIndex].Bitangent = bitangent;
            }
        }

        private static (int BoneIdx, float Weight)[][] FilterInfluences(
            (int BoneIdx, float Weight)[][] source,
            int jointCount)
        {
            var result = new (int BoneIdx, float Weight)[source.Length][];
            for (var vertexIndex = 0; vertexIndex < source.Length; vertexIndex++)
            {
                var positive = new List<(int BoneIdx, float Weight)>(source[vertexIndex].Length);
                foreach (var influence in source[vertexIndex])
                {
                    if ((uint)influence.BoneIdx >= (uint)jointCount ||
                        !float.IsFinite(influence.Weight) ||
                        influence.Weight < 0f)
                    {
                        throw new InvalidDataException(
                            $"Animated vertex {vertexIndex} has an invalid bone influence.");
                    }

                    if (influence.Weight > 0f)
                    {
                        positive.Add(influence);
                    }
                }

                result[vertexIndex] = positive.ToArray();
            }

            return result;
        }

        private static void Write(float[] stream, int vertexIndex, Vector3 value)
        {
            var offset = vertexIndex * 3;
            stream[offset] = value.X;
            stream[offset + 1] = value.Y;
            stream[offset + 2] = value.Z;
        }

        private static Vector3 Read(float[] stream, int vertexIndex)
        {
            var offset = vertexIndex * 3;
            return new Vector3(stream[offset], stream[offset + 1], stream[offset + 2]);
        }

        private static bool IsFinite(Vector3 value)
        {
            return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
        }
    }
}
#endif
