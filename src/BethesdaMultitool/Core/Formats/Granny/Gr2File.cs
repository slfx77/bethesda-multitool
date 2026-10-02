// Ported from AweMultitool (slfx77)
// (the mesh/skeleton side of src/AweMultitool/Core/Formats/Granny/Gr2ModelBuilder.cs), reshaped for
// the 2003 Granny SDK 2.2 files this repository reads: the vertex layout is read from the file's
// own type record instead of being pinned to one stride, and animation curves are the SDK 2.2
// plain form (Degree + Knots + Controls) rather than the later CurveData variants.

using System.Buffers.Binary;
using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Granny;

/// <summary>One bone of a Granny 2 skeleton, with its local transform and the stored inverse world matrix.</summary>
internal sealed record Gr2Bone(
    string Name,
    int ParentIndex,
    Vector3 Translation,
    Quaternion Rotation,
    Matrix4x4 ScaleShear,
    Matrix4x4 LocalMatrix,
    Matrix4x4 StoredInverseWorld);

/// <summary>A Granny 2 skeleton: a name and its bones in file order (parents precede children).</summary>
internal sealed class Gr2Skeleton
{
    public Gr2Skeleton(string name, IReadOnlyList<Gr2Bone> bones)
    {
        Name = name;
        Bones = bones;
        WorldMatrices = ComputeWorld(bones);
    }

    public string Name { get; }

    public IReadOnlyList<Gr2Bone> Bones { get; }

    /// <summary>Bone-to-model matrices composed from the local transforms up the parent chain.</summary>
    public IReadOnlyList<Matrix4x4> WorldMatrices { get; }

    /// <summary>The index of the bone with that exact name, or -1.</summary>
    public int IndexOf(string boneName)
    {
        for (var index = 0; index < Bones.Count; index++)
        {
            if (string.Equals(Bones[index].Name, boneName, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    ///     How many bones' stored <c>InverseWorldTransform</c> is the inverse of the composed world
    ///     matrix (to a 2e-3 tolerance) — the check that the transform decode and the parent chain
    ///     agree with what the exporter wrote.
    /// </summary>
    public int CountConsistentInverses()
    {
        var consistent = 0;
        for (var index = 0; index < Bones.Count; index++)
        {
            if (ApproximatelyIdentity(WorldMatrices[index] * Bones[index].StoredInverseWorld))
            {
                consistent++;
            }
        }

        return consistent;
    }

    private static Matrix4x4[] ComputeWorld(IReadOnlyList<Gr2Bone> bones)
    {
        var world = new Matrix4x4[bones.Count];
        var state = new byte[bones.Count];
        for (var index = 0; index < bones.Count; index++)
        {
            Compute(index);
        }

        return world;

        Matrix4x4 Compute(int index)
        {
            if (state[index] == 2)
            {
                return world[index];
            }

            if (state[index] == 1)
            {
                throw new InvalidDataException("Skeleton parent indices form a cycle.");
            }

            state[index] = 1;
            var parent = bones[index].ParentIndex;
            world[index] = parent >= 0 ? bones[index].LocalMatrix * Compute(parent) : bones[index].LocalMatrix;
            state[index] = 2;
            return world[index];
        }
    }

    private static bool ApproximatelyIdentity(Matrix4x4 matrix)
    {
        const float tolerance = 2e-3f;
        var difference = matrix - Matrix4x4.Identity;
        return MathF.Abs(difference.M11) <= tolerance && MathF.Abs(difference.M12) <= tolerance &&
               MathF.Abs(difference.M13) <= tolerance && MathF.Abs(difference.M14) <= tolerance
               && MathF.Abs(difference.M21) <= tolerance && MathF.Abs(difference.M22) <= tolerance &&
               MathF.Abs(difference.M23) <= tolerance && MathF.Abs(difference.M24) <= tolerance
               && MathF.Abs(difference.M31) <= tolerance && MathF.Abs(difference.M32) <= tolerance &&
               MathF.Abs(difference.M33) <= tolerance && MathF.Abs(difference.M34) <= tolerance
               && MathF.Abs(difference.M41) <= tolerance && MathF.Abs(difference.M42) <= tolerance &&
               MathF.Abs(difference.M43) <= tolerance && MathF.Abs(difference.M44) <= tolerance;
    }
}

/// <summary>One vertex's bone influences: palette indices into the mesh's bone bindings, and weights summing to ~1.</summary>
internal readonly record struct Gr2VertexInfluence(byte BoneBinding, float Weight);

/// <summary>A triangle range of a mesh that shares one material slot.</summary>
internal readonly record struct Gr2TriangleGroup(int MaterialIndex, int TriangleFirst, int TriangleCount);

/// <summary>A Granny 2 mesh: positions, optional normals/UVs, optional skin weights, and an index buffer.</summary>
internal sealed class Gr2Mesh
{
    public required string Name { get; init; }

    /// <summary>The vertex type as the file describes it: <c>Name:Type[width]@offset</c>, comma-joined, then the stride.</summary>
    public required string VertexLayout { get; init; }

    public required int VertexStride { get; init; }

    public required Vector3[] Positions { get; init; }

    public required Vector3[]? Normals { get; init; }

    public required Vector2[]? TextureCoordinates { get; init; }

    /// <summary>Per-vertex bone influences (up to the layout's width), or null for an unskinned layout.</summary>
    public required Gr2VertexInfluence[][]? Influences { get; init; }

    /// <summary>Three indices per triangle, each in <c>[0, Positions.Length)</c>.</summary>
    public required int[] Indices { get; init; }

    /// <summary>Whether the indices came from <c>Indices16</c> (true) or the 32-bit <c>Indices</c>.</summary>
    public required bool SixteenBitIndices { get; init; }

    /// <summary>
    ///     Null when the triangle list was written; otherwise why <see cref="Indices" /> and
    ///     <see cref="Groups" /> are empty. ⚑ Measured 2026-09-08: six Van Buren door-box meshes
    ///     (Props.grp <c>DS_Vault_Elevator:ElDoorsBox</c>, <c>DS_City1_Doors:B1..B4Doorbox</c>,
    ///     <c>DS_Junktown1_Doors:Layer2</c>) carry a topology whose every index word AND every
    ///     <c>Groups</c> word is the MSVC debug-heap fill <c>0xBAADF00D</c> (<c>0xF00D</c> per
    ///     16-bit index) — the exporter wrote uninitialised memory. The 52 vertices are real.
    /// </summary>
    public required string? TopologyDefect { get; init; }

    public required IReadOnlyList<Gr2TriangleGroup> Groups { get; init; }

    /// <summary>Material names per binding slot; null where the binding's material pointer is null.</summary>
    public required IReadOnlyList<string?> MaterialNames { get; init; }

    /// <summary>The bone names the skin palette indexes, in palette order.</summary>
    public required IReadOnlyList<string> BoneBindings { get; init; }

    public int TriangleCount => Indices.Length / 3;
}

/// <summary>A Granny 2 model: a skeleton plus the meshes bound to it, with its initial placement.</summary>
internal sealed class Gr2Model
{
    public required string Name { get; init; }

    public required Gr2Skeleton? Skeleton { get; init; }

    public required Matrix4x4 InitialPlacement { get; init; }

    public required IReadOnlyList<Gr2Mesh> Meshes { get; init; }
}

/// <summary>An SDK 2.2 curve: a degree, a knot vector and <c>Knots × dimension</c> control values.</summary>
internal sealed record Gr2Curve(int Degree, float[] Knots, float[] Controls)
{
    public bool IsEmpty => Knots.Length == 0;

    /// <summary>Values per knot, or 0 for an empty curve.</summary>
    public int Dimension => Knots.Length == 0 ? 0 : Controls.Length / Knots.Length;
}

/// <summary>One bone's animation: position, orientation and scale/shear curves.</summary>
internal sealed record Gr2TransformTrack(string Name, Gr2Curve Position, Gr2Curve Orientation, Gr2Curve ScaleShear);

/// <summary>A track group: named after the skeleton it animates, with one transform track per bone.</summary>
internal sealed record Gr2TrackGroup(string Name, IReadOnlyList<Gr2TransformTrack> TransformTracks);

/// <summary>A Granny 2 animation clip.</summary>
internal sealed record Gr2AnimationClip(
    string Name,
    float Duration,
    float TimeStep,
    float Oversampling,
    IReadOnlyList<Gr2TrackGroup> TrackGroups);

/// <summary>What the exporter recorded about the authoring tool, including its coordinate basis.</summary>
internal sealed record Gr2ArtToolInfo(
    string ToolName,
    int MajorRevision,
    int MinorRevision,
    float UnitsPerMeter,
    Vector3 Origin,
    Vector3 Right,
    Vector3 Up,
    Vector3 Back);

/// <summary>
///     The semantic contents of one Granny 2 file, read off its type tree by member NAME — the file
///     describes its own layout, so nothing here is addressed by offset.
///     <para>
///         ⚑ Measured on the Van Buren prototype (547 payloads, 2026-09-08): every file is a
///         <c>granny_file_info</c> root with the members <c>ArtToolInfo</c>, <c>ExporterInfo</c>
///         (546 — one lacks it), <c>FromFileName</c>, <c>Textures</c>, <c>Materials</c>,
///         <c>Skeletons</c>, <c>VertexDatas</c>, <c>TriTopologies</c>, <c>Meshes</c>,
///         <c>Models</c>, <c>TrackGroups</c>, <c>Animations</c>. The population splits into
///         <b>448 animation-only files</b> (no skeleton, no mesh), <b>74 skeleton files</b> without a
///         mesh (3 of which also carry a clip, so there are 451 clips in all) and <b>25 model files</b>
///         carrying exactly ONE mesh each —
///         18 collision hulls (<c>CR_Bat_Coll</c>, <c>Female_Collision</c> ×5, <c>Male_Collision</c>
///         ×4), one render mesh (<c>deathclaw</c>) and six Props.grp door boxes
///         (<c>DS_City1_Doors:B1Doorbox</c>…) whose 52 vertices are real but whose triangle list
///         and groups are unwritten debug-heap memory (<see cref="Gr2Mesh.TopologyDefect" />).
///         Indices are 32-bit on 20 meshes and 16-bit on 5 (all five among the door boxes).
///         No file carries a texture or a material (0 of 547).
///     </para>
/// </summary>
internal sealed class Gr2File
{
    private const int TransformSize = 68;

    public required string Name { get; init; }

    public required Gr2ArtToolInfo? ArtToolInfo { get; init; }

    public required string? ExporterName { get; init; }

    public required string? FromFileName { get; init; }

    /// <summary>The <c>FromFileName</c> of every texture object — the only texture reference this reader surfaces.</summary>
    public required IReadOnlyList<string?> TextureFileNames { get; init; }

    public required IReadOnlyList<string?> MaterialNames { get; init; }

    public required IReadOnlyList<Gr2Skeleton> Skeletons { get; init; }

    public required IReadOnlyList<Gr2Model> Models { get; init; }

    /// <summary>Every mesh object in the file, whether or not a model binds it.</summary>
    public required IReadOnlyList<Gr2Mesh> Meshes { get; init; }

    public required IReadOnlyList<Gr2AnimationClip> Animations { get; init; }

    /// <summary>Reads a file's semantic contents from its parsed type tree.</summary>
    public static Gr2File Read(Gr2TypeTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var root = tree.RootObject;
        var name = tree.File.Container.Name;

        var artTool = root.FindMember("ArtToolInfo")?.ReadObject() is { } tool ? ReadArtToolInfo(tool) : null;
        var exporterName = root.FindMember("ExporterInfo")?.ReadObject()?.FindMember("ExporterName")?.ReadString();
        var fromFileName = root.FindMember("FromFileName")?.ReadString();

        var textures = new List<string?>();
        foreach (var texture in Objects(root, "Textures"))
        {
            textures.Add(texture?.FindMember("FromFileName")?.ReadString());
        }

        var materialNames = new List<string?>();
        foreach (var material in Objects(root, "Materials"))
        {
            materialNames.Add(material?.FindMember("Name")?.ReadString());
        }

        var skeletonsByAddress = new Dictionary<Gr2Address, Gr2Skeleton>();
        var skeletons = new List<Gr2Skeleton>();
        foreach (var skeleton in Objects(root, "Skeletons"))
        {
            if (skeleton is null)
            {
                continue;
            }

            var read = ReadSkeleton(skeleton, name);
            skeletonsByAddress[skeleton.Address] = read;
            skeletons.Add(read);
        }

        var meshesByAddress = new Dictionary<Gr2Address, Gr2Mesh>();
        var meshes = new List<Gr2Mesh>();
        foreach (var mesh in Objects(root, "Meshes"))
        {
            if (mesh is null)
            {
                continue;
            }

            var read = ReadMesh(mesh, name, meshes.Count);
            meshesByAddress[mesh.Address] = read;
            meshes.Add(read);
        }

        var models = new List<Gr2Model>();
        foreach (var model in Objects(root, "Models"))
        {
            if (model is null)
            {
                continue;
            }

            Gr2Skeleton? skeleton = null;
            if (model.FindMember("Skeleton")?.ReadObject() is { } skeletonObject
                && !skeletonsByAddress.TryGetValue(skeletonObject.Address, out skeleton))
            {
                skeleton = ReadSkeleton(skeletonObject, name);
            }

            var bound = new List<Gr2Mesh>();
            foreach (var binding in Objects(model, "MeshBindings"))
            {
                if (binding?.FindMember("Mesh")?.ReadObject() is not { } meshObject)
                {
                    continue;
                }

                if (!meshesByAddress.TryGetValue(meshObject.Address, out var mesh))
                {
                    mesh = ReadMesh(meshObject, name, meshes.Count);
                }

                bound.Add(mesh);
            }

            models.Add(new Gr2Model
            {
                Name = model.FindMember("Name")?.ReadString() ?? $"model{models.Count}",
                Skeleton = skeleton,
                InitialPlacement = model.FindMember("InitialPlacement") is { } placement
                    ? ReadTransform(placement, name).Matrix
                    : Matrix4x4.Identity,
                Meshes = bound
            });
        }

        var animations = new List<Gr2AnimationClip>();
        foreach (var animation in Objects(root, "Animations"))
        {
            if (animation is null)
            {
                continue;
            }

            var groups = new List<Gr2TrackGroup>();
            foreach (var group in Objects(animation, "TrackGroups"))
            {
                if (group is not null)
                {
                    groups.Add(ReadTrackGroup(group, name));
                }
            }

            animations.Add(new Gr2AnimationClip(
                animation.FindMember("Name")?.ReadString() ?? $"animation{animations.Count}",
                animation.FindMember("Duration")?.ReadSingle() ?? 0f,
                animation.FindMember("TimeStep")?.ReadSingle() ?? 0f,
                animation.FindMember("Oversampling")?.ReadSingle() ?? 0f,
                groups));
        }

        return new Gr2File
        {
            Name = name,
            ArtToolInfo = artTool,
            ExporterName = exporterName,
            FromFileName = fromFileName,
            TextureFileNames = textures,
            MaterialNames = materialNames,
            Skeletons = skeletons,
            Models = models,
            Meshes = meshes,
            Animations = animations
        };
    }

    private static Gr2ArtToolInfo ReadArtToolInfo(Gr2Object tool)
    {
        return new Gr2ArtToolInfo(
            tool.FindMember("FromArtToolName")?.ReadString() ?? string.Empty,
            tool.FindMember("ArtToolMajorRevision")?.ReadInt32() ?? 0,
            tool.FindMember("ArtToolMinorRevision")?.ReadInt32() ?? 0,
            tool.FindMember("UnitsPerMeter")?.ReadSingle() ?? 1f,
            ReadVector3(tool, "Origin"),
            ReadVector3(tool, "RightVector"),
            ReadVector3(tool, "UpVector"),
            ReadVector3(tool, "BackVector"));
    }

    private static Vector3 ReadVector3(Gr2Object owner, string member)
    {
        var value = owner.FindMember(member);
        if (value is null || value.Definition.Type != Gr2MemberType.Real32 || value.Definition.ElementCount < 3)
        {
            return Vector3.Zero;
        }

        return new Vector3(value.ReadSingle(), value.ReadSingle(1), value.ReadSingle(2));
    }

    private static Gr2Skeleton ReadSkeleton(Gr2Object skeleton, string fileName)
    {
        var bonesSource = Objects(skeleton, "Bones");
        var bones = new Gr2Bone[bonesSource.Count];
        for (var index = 0; index < bones.Length; index++)
        {
            var bone = bonesSource[index] ?? throw new InvalidDataException($"{fileName}: skeleton has a null bone.");
            var boneName = bone.FindMember("Name")?.ReadString() ?? $"bone{index}";
            var parent = bone.FindMember("ParentIndex")?.ReadInt32() ?? -1;
            if (parent == index)
            {
                parent = -1;
            }

            if (parent < -1 || parent >= bones.Length)
            {
                throw new InvalidDataException($"{fileName}: bone '{boneName}' has parent {parent} of {bones.Length}.");
            }

            var transform = ReadTransform(bone["Transform"], fileName);
            var inverse = ReadMatrix(bone["InverseWorldTransform"], fileName);
            bones[index] = new Gr2Bone(boneName, parent, transform.Translation, transform.Rotation,
                transform.ScaleShear, transform.Matrix, inverse);
        }

        return new Gr2Skeleton(skeleton.FindMember("Name")?.ReadString() ?? "skeleton", bones);
    }

    private static Gr2Mesh ReadMesh(Gr2Object mesh, string fileName, int ordinal)
    {
        var meshName = mesh.FindMember("Name")?.ReadString();
        if (string.IsNullOrWhiteSpace(meshName))
        {
            meshName = $"mesh{ordinal}";
        }

        var vertexData = mesh.FindMember("PrimaryVertexData")?.ReadObject() ??
                         throw new InvalidDataException($"{fileName}: mesh '{meshName}' has no PrimaryVertexData.");
        var topology = mesh.FindMember("PrimaryTopology")?.ReadObject() ??
                       throw new InvalidDataException($"{fileName}: mesh '{meshName}' has no PrimaryTopology.");
        var vertices = Objects(vertexData, "Vertices");
        var type = vertices.ElementType ??
                   throw new InvalidDataException($"{fileName}: mesh '{meshName}' has an untyped vertex array.");

        var positionMember = RequireVertexMember(type, "Position", 3, fileName, meshName);
        var normalMember = OptionalVertexMember(type, "Normal", 3);
        var uvMember = OptionalVertexMember(type, "TextureCoordinates0", 2);
        var weightsMember = type.FindMember("BoneWeights");
        var indicesMember = type.FindMember("BoneIndices");
        if (weightsMember is null != indicesMember is null)
        {
            throw new InvalidDataException(
                $"{fileName}: mesh '{meshName}' declares bone weights without indices or vice versa.");
        }

        if (weightsMember is not null && (weightsMember.ElementCount != indicesMember!.ElementCount ||
                                          weightsMember.ElementCount is < 1 or > 4))
        {
            throw new InvalidDataException(
                $"{fileName}: mesh '{meshName}' has a {weightsMember.ElementCount}-wide bone palette; 1-4 is supported.");
        }

        var bytes = vertices.ReadRaw().Span;
        var stride = type.Size;
        var positions = new Vector3[vertices.Count];
        var normals = normalMember is null ? null : new Vector3[vertices.Count];
        var uvs = uvMember is null ? null : new Vector2[vertices.Count];
        var influences = weightsMember is null ? null : new Gr2VertexInfluence[vertices.Count][];
        for (var vertex = 0; vertex < vertices.Count; vertex++)
        {
            var row = bytes.Slice(vertex * stride, stride);
            positions[vertex] = new Vector3(ReadScalar(row, positionMember, 0), ReadScalar(row, positionMember, 1),
                ReadScalar(row, positionMember, 2));
            if (normals is not null)
            {
                normals[vertex] = new Vector3(ReadScalar(row, normalMember!, 0), ReadScalar(row, normalMember!, 1),
                    ReadScalar(row, normalMember!, 2));
            }

            if (uvs is not null)
            {
                uvs[vertex] = new Vector2(ReadScalar(row, uvMember!, 0), ReadScalar(row, uvMember!, 1));
            }

            if (influences is not null)
            {
                var lanes = new Gr2VertexInfluence[weightsMember!.ElementCount];
                for (var lane = 0; lane < lanes.Length; lane++)
                {
                    lanes[lane] = new Gr2VertexInfluence(
                        (byte)ReadScalar(row, indicesMember!, lane),
                        ReadScalar(row, weightsMember, lane));
                }

                influences[vertex] = lanes;
            }

            if (!float.IsFinite(positions[vertex].X) || !float.IsFinite(positions[vertex].Y) ||
                !float.IsFinite(positions[vertex].Z))
            {
                throw new InvalidDataException(
                    $"{fileName}: mesh '{meshName}' vertex {vertex} has a non-finite position.");
            }
        }

        var (indices, sixteenBit, topologyDefect) = ReadIndices(topology, vertices.Count, fileName, meshName);

        var groups = new List<Gr2TriangleGroup>();
        foreach (var group in Objects(topology, "Groups"))
        {
            if (group is null)
            {
                continue;
            }

            var read = new Gr2TriangleGroup(group["MaterialIndex"].ReadInt32(), group["TriFirst"].ReadInt32(),
                group["TriCount"].ReadInt32());
            if (topologyDefect is not null)
            {
                // An unwritten index buffer comes with unwritten groups (6/6 measured); anything
                // else is a combination that has not been seen and must not be guessed at.
                if (read.MaterialIndex != DebugHeapFill || read.TriangleFirst != DebugHeapFill ||
                    read.TriangleCount != DebugHeapFill)
                {
                    throw new InvalidDataException(
                        $"{fileName}: mesh '{meshName}' has an unwritten index buffer but a written triangle group.");
                }

                continue;
            }

            if (read.TriangleFirst < 0 || read.TriangleCount < 0 ||
                (long)(read.TriangleFirst + read.TriangleCount) * 3 > indices.Length)
            {
                throw new InvalidDataException(
                    $"{fileName}: mesh '{meshName}' has a triangle group outside its index buffer.");
            }

            groups.Add(read);
        }

        var materialNames = new List<string?>();
        foreach (var binding in Objects(mesh, "MaterialBindings"))
        {
            materialNames.Add(binding?.FindMember("Material")?.ReadObject()?.FindMember("Name")?.ReadString());
        }

        var boneBindings = new List<string>();
        foreach (var binding in Objects(mesh, "BoneBindings"))
        {
            boneBindings.Add(binding?.FindMember("BoneName")?.ReadString() ?? string.Empty);
        }

        if (influences is not null)
        {
            foreach (var lanes in influences)
            {
                foreach (var lane in lanes)
                {
                    if (lane.Weight > 0 && lane.BoneBinding >= boneBindings.Count)
                    {
                        throw new InvalidDataException(
                            $"{fileName}: mesh '{meshName}' weights bone slot {lane.BoneBinding} of {boneBindings.Count}.");
                    }
                }
            }
        }

        var layout = string.Join(",", type.Members.Select(member =>
            member.ArrayWidth > 0
                ? $"{member.Name}:{member.Type}[{member.ArrayWidth}]@{member.ObjectOffset}"
                : $"{member.Name}:{member.Type}@{member.ObjectOffset}"));

        return new Gr2Mesh
        {
            Name = meshName,
            VertexLayout = layout,
            VertexStride = stride,
            Positions = positions,
            Normals = normals,
            TextureCoordinates = uvs,
            Influences = influences,
            Indices = indices,
            SixteenBitIndices = sixteenBit,
            TopologyDefect = topologyDefect,
            Groups = groups,
            MaterialNames = materialNames,
            BoneBindings = boneBindings
        };
    }

    /// <summary>
    ///     The 32-bit pattern the MSVC debug heap writes into a fresh allocation. A topology whose
    ///     every index word (and every group word) is this pattern was never written by the exporter.
    /// </summary>
    internal const int DebugHeapFill = unchecked((int)0xBAADF00D);

    /// <summary>
    ///     Whether every element of a 2- or 4-byte little-endian integer array is the debug-heap fill
    ///     (<c>0xBAADF00D</c>, whose low half <c>0xF00D</c> is what a 16-bit array shows). False for an
    ///     empty array, another element size, or any element that differs.
    /// </summary>
    internal static bool IsDebugHeapFill(ReadOnlySpan<byte> bytes, int elementSize)
    {
        if (bytes.Length == 0 || elementSize is not (2 or 4) || bytes.Length % elementSize != 0)
        {
            return false;
        }

        for (var offset = 0; offset < bytes.Length; offset += elementSize)
        {
            var word = elementSize == 4
                ? BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..])
                : BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
            if (word != (elementSize == 4 ? DebugHeapFill : unchecked((ushort)DebugHeapFill)))
            {
                return false;
            }
        }

        return true;
    }

    private static (int[] Indices, bool SixteenBit, string? Defect) ReadIndices(Gr2Object topology, int vertexCount,
        string fileName, string meshName)
    {
        var shortIndices = topology.FindMember("Indices16")?.ReadObjects();
        var wideIndices = topology.FindMember("Indices")?.ReadObjects();
        var sixteenBit = shortIndices is { Count: > 0 };
        var source = sixteenBit
            ? shortIndices!
            : wideIndices ?? throw new InvalidDataException($"{fileName}: mesh '{meshName}' has no index buffer.");
        if (source.Count == 0)
        {
            return ([], sixteenBit, null);
        }

        var elementType = source.ElementType ??
                          throw new InvalidDataException($"{fileName}: mesh '{meshName}' has an untyped index buffer.");
        if (elementType.Members.Count != 1 || elementType.Members[0].ElementCount != 1 ||
            elementType.Members[0].Size != elementType.Size)
        {
            throw new InvalidDataException($"{fileName}: mesh '{meshName}' has a non-scalar index element type.");
        }

        var member = elementType.Members[0];
        var bytes = source.ReadRaw().Span;
        if (IsDebugHeapFill(bytes, elementType.Size))
        {
            return ([], sixteenBit,
                $"the {source.Count}-entry {(sixteenBit ? 16 : 32)}-bit index buffer is unwritten exporter memory (every word is the 0x{unchecked((uint)DebugHeapFill):X8} debug-heap fill)");
        }

        var result = new int[source.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var row = bytes.Slice(index * elementType.Size, elementType.Size);
            result[index] = member.Type switch
            {
                Gr2MemberType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(row),
                Gr2MemberType.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(row),
                Gr2MemberType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(row),
                Gr2MemberType.UInt32 => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(row)),
                _ => throw new InvalidDataException(
                    $"{fileName}: mesh '{meshName}' has unsupported {member.Type} indices.")
            };

            if (result[index] < 0 || result[index] >= vertexCount)
            {
                throw new InvalidDataException(
                    $"{fileName}: mesh '{meshName}' index {index} addresses vertex {result[index]} of {vertexCount}.");
            }
        }

        if (result.Length % 3 != 0)
        {
            throw new InvalidDataException(
                $"{fileName}: mesh '{meshName}' has {result.Length} indices, not a multiple of three.");
        }

        return (result, sixteenBit, null);
    }

    private static Gr2TrackGroup ReadTrackGroup(Gr2Object group, string fileName)
    {
        var tracks = new List<Gr2TransformTrack>();
        foreach (var track in Objects(group, "TransformTracks"))
        {
            if (track is null)
            {
                continue;
            }

            tracks.Add(new Gr2TransformTrack(
                track.FindMember("Name")?.ReadString() ?? string.Empty,
                ReadCurve(track, "PositionCurve", fileName),
                ReadCurve(track, "OrientationCurve", fileName),
                ReadCurve(track, "ScaleShearCurve", fileName)));
        }

        return new Gr2TrackGroup(group.FindMember("Name")?.ReadString() ?? string.Empty, tracks);
    }

    /// <summary>
    ///     An SDK 2.2 curve is an inline <c>{ Degree, Knots[], Controls[] }</c> — the plain form
    ///     that predates the <c>CurveData</c> variant of later SDKs. The control count must be a
    ///     whole multiple of the knot count; the quotient is the dimension (3 position, 4 orientation).
    /// </summary>
    private static Gr2Curve ReadCurve(Gr2Object track, string member, string fileName)
    {
        var curve = track.FindMember(member)?.ReadObject();
        if (curve is null)
        {
            return new Gr2Curve(0, [], []);
        }

        var degree = curve.FindMember("Degree")?.ReadInt32() ?? 0;
        var knots = ReadFloats(curve, "Knots");
        var controls = ReadFloats(curve, "Controls");
        if ((knots.Length == 0 && controls.Length != 0) || (knots.Length != 0 && controls.Length % knots.Length != 0))
        {
            throw new InvalidDataException(
                $"{fileName}: {member} has {controls.Length} controls for {knots.Length} knots.");
        }

        return new Gr2Curve(degree, knots, controls);
    }

    private static float[] ReadFloats(Gr2Object owner, string member)
    {
        var list = owner.FindMember(member)?.ReadObjects();
        if (list is null || list.Count == 0)
        {
            return [];
        }

        var elementType = list.ElementType!;
        if (elementType.Size != 4 || elementType.Members.Count != 1 ||
            elementType.Members[0].Type != Gr2MemberType.Real32)
        {
            throw new InvalidDataException($"'{member}' is not an array of Real32.");
        }

        var bytes = list.ReadRaw().Span;
        var result = new float[list.Count];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(index * 4)..]);
        }

        return result;
    }

    private static Gr2ObjectList Objects(Gr2Object owner, string member)
    {
        var value = owner.FindMember(member);
        return value is null ? new Gr2ObjectList(owner.Tree, null, 0, null, false) : value.ReadObjects();
    }

    private static Gr2TypeMember RequireVertexMember(Gr2TypeDefinition type, string name, int count, string fileName,
        string meshName)
    {
        return OptionalVertexMember(type, name, count) ??
               throw new InvalidDataException(
                   $"{fileName}: mesh '{meshName}' vertex type has no {count}-wide numeric '{name}'.");
    }

    private static Gr2TypeMember? OptionalVertexMember(Gr2TypeDefinition type, string name, int count)
    {
        var member = type.FindMember(name);
        if (member is null || member.ElementCount < count || member.ObjectOffset < 0 ||
            member.Size > type.Size - member.ObjectOffset || !IsNumeric(member.Type))
        {
            return null;
        }

        return member;
    }

    private static bool IsNumeric(Gr2MemberType type)
    {
        return type is Gr2MemberType.Real32 or Gr2MemberType.Real16 or Gr2MemberType.Int8 or Gr2MemberType.UInt8
            or Gr2MemberType.BinormalInt8
            or Gr2MemberType.NormalUInt8 or Gr2MemberType.Int16 or Gr2MemberType.UInt16 or Gr2MemberType.BinormalInt16
            or Gr2MemberType.NormalUInt16
            or Gr2MemberType.Int32 or Gr2MemberType.UInt32;
    }

    /// <summary>
    ///     Reads one element of a numeric vertex member, normalising the Granny normalised integer kinds to [0,1] /
    ///     [-1,1].
    /// </summary>
    internal static float ReadScalar(ReadOnlySpan<byte> row, Gr2TypeMember member, int elementIndex)
    {
        if ((uint)elementIndex >= (uint)member.ElementCount)
        {
            throw new ArgumentOutOfRangeException(nameof(elementIndex));
        }

        var offset = checked(member.ObjectOffset + elementIndex * member.UnitSize);
        if (offset < 0 || member.UnitSize > row.Length - offset)
        {
            throw new InvalidDataException("A scalar lies outside its containing row.");
        }

        return member.Type switch
        {
            Gr2MemberType.Real32 => BinaryPrimitives.ReadSingleLittleEndian(row[offset..]),
            Gr2MemberType.Real16 => (float)BitConverter.UInt16BitsToHalf(
                BinaryPrimitives.ReadUInt16LittleEndian(row[offset..])),
            Gr2MemberType.Int8 => (sbyte)row[offset],
            Gr2MemberType.UInt8 => row[offset],
            Gr2MemberType.BinormalInt8 => MathF.Max(-1, (sbyte)row[offset] / 127f),
            Gr2MemberType.NormalUInt8 => row[offset] / 255f,
            Gr2MemberType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(row[offset..]),
            Gr2MemberType.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(row[offset..]),
            Gr2MemberType.BinormalInt16 => MathF.Max(-1,
                BinaryPrimitives.ReadInt16LittleEndian(row[offset..]) / 32767f),
            Gr2MemberType.NormalUInt16 => BinaryPrimitives.ReadUInt16LittleEndian(row[offset..]) / 65535f,
            Gr2MemberType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(row[offset..]),
            Gr2MemberType.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(row[offset..]),
            _ => throw new InvalidDataException($"{member.Type} is not a numeric scalar.")
        };
    }

    /// <summary>
    ///     A 68-byte Granny transform: flags, position (3), orientation quaternion (4), 3x3 scale/shear.
    ///     The composed matrix is scale/shear, then rotation, then translation (row-vector convention).
    /// </summary>
    internal static (Vector3 Translation, Quaternion Rotation, Matrix4x4 ScaleShear, Matrix4x4 Matrix) ReadTransform(
        Gr2ObjectMember member, string fileName)
    {
        if (member.Definition.Type != Gr2MemberType.Transform || member.Definition.Size != TransformSize)
        {
            throw new InvalidDataException(
                $"{fileName}: member '{member.Definition.Name}' is not a 68-byte transform.");
        }

        var bytes = member.ReadRaw().Span;
        var translation = new Vector3(F32(bytes, 4), F32(bytes, 8), F32(bytes, 12));
        var rawRotation = new Quaternion(F32(bytes, 16), F32(bytes, 20), F32(bytes, 24), F32(bytes, 28));
        var scaleShear = new Matrix4x4(
            F32(bytes, 32), F32(bytes, 36), F32(bytes, 40), 0,
            F32(bytes, 44), F32(bytes, 48), F32(bytes, 52), 0,
            F32(bytes, 56), F32(bytes, 60), F32(bytes, 64), 0,
            0, 0, 0, 1);
        var rotation = rawRotation.LengthSquared() > 1e-12f && float.IsFinite(rawRotation.LengthSquared())
            ? Quaternion.Normalize(rawRotation)
            : Quaternion.Identity;
        var matrix = scaleShear * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
        return (translation, rotation, scaleShear, matrix);
    }

    private static Matrix4x4 ReadMatrix(Gr2ObjectMember member, string fileName)
    {
        if (member.Definition.Type != Gr2MemberType.Real32 || member.Definition.ElementCount != 16)
        {
            throw new InvalidDataException($"{fileName}: member '{member.Definition.Name}' is not a 16-float matrix.");
        }

        var value = new float[16];
        for (var index = 0; index < value.Length; index++)
        {
            value[index] = member.ReadSingle(index);
        }

        return new Matrix4x4(
            value[0], value[1], value[2], value[3],
            value[4], value[5], value[6], value[7],
            value[8], value[9], value[10], value[11],
            value[12], value[13], value[14], value[15]);
    }

    private static float F32(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
    }
}
