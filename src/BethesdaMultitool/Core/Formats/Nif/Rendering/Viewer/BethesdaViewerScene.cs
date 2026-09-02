using System.Numerics;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;

/// <summary>
///     Why a Bethesda scene was assembled. The native viewer can use this to choose presentation
///     defaults without inferring intent from mesh names or from an export format.
/// </summary>
internal enum BethesdaViewerScenePurpose
{
    Unspecified,
    RawNif,
    NpcAppearance,
    CreatureAppearance,
    WorldReference
}

/// <summary>The semantic role of a node in a native Bethesda viewer scene.</summary>
internal enum BethesdaViewerNodeRole
{
    Root,
    Skeleton,
    Attachment
}

/// <summary>Finite model-space bounds supplied by the scene assembler.</summary>
internal readonly record struct BethesdaViewerBounds(Vector3 Minimum, Vector3 Maximum)
{
    internal Vector3 Center => (Minimum + Maximum) * 0.5f;

    internal Vector3 Size => Maximum - Minimum;

    internal bool IsFinite =>
        float.IsFinite(Minimum.X) &&
        float.IsFinite(Minimum.Y) &&
        float.IsFinite(Minimum.Z) &&
        float.IsFinite(Maximum.X) &&
        float.IsFinite(Maximum.Y) &&
        float.IsFinite(Maximum.Z);
}

/// <summary>One stable, index-addressed node in a native Bethesda viewer scene.</summary>
internal sealed class BethesdaViewerNode
{
    internal required string Name { get; init; }

    /// <summary>
    ///     Optional source name used to resolve skin joints and animation targets. It remains
    ///     separate from <see cref="Name" /> because source graphs can contain duplicate names.
    /// </summary>
    internal string? LookupName { get; init; }

    /// <summary>Originating NIF object block, when the source graph provides exact identity.</summary>
    internal int? SourceBlockIndex { get; init; }

    internal int? ParentIndex { get; init; }

    internal required Matrix4x4 LocalTransform { get; init; }

    internal required Matrix4x4 WorldTransform { get; init; }

    internal required BethesdaViewerNodeRole Role { get; init; }
}

/// <summary>
///     Skinning data for a mesh part. Joint indices address <see cref="BethesdaViewerScene.Nodes" />;
///     each influence's <c>BoneIdx</c> addresses the parallel joint/inverse-bind arrays.
/// </summary>
internal sealed class BethesdaViewerSkinBinding
{
    internal required int[] JointNodeIndices { get; init; }

    internal required Matrix4x4[] InverseBindMatrices { get; init; }

    internal required (int BoneIdx, float Weight)[][] PerVertexInfluences { get; init; }
}

/// <summary>
///     One render primitive attached to a scene node. <see cref="Submesh" /> is deliberately the
///     native renderer model, not a PBR/glTF material projection: all Bethesda shader flags,
///     texture lanes, tint state, controller state, water/sky routes, and vertex semantics remain
///     available to the direct renderer.
/// </summary>
internal sealed class BethesdaViewerMeshPart
{
    internal required string Name { get; init; }

    internal int? NodeIndex { get; init; }

    internal required RenderableSubmesh Submesh { get; init; }

    internal BethesdaViewerSkinBinding? Skin { get; init; }
}

/// <summary>
///     Stable address of one vertex in <see cref="BethesdaViewerScene.MeshParts" />. The native
///     viewer resolves this address against each frame's skinned position buffers; it does not
///     depend on transient GPU resources or on source shape names being unique.
/// </summary>
internal readonly record struct BethesdaViewerMeshVertexIndex(int MeshPartIndex, int VertexIndex);

/// <summary>
///     Vertices that share one authored bind-pose boundary across different source NIFs. After
///     skinning, the renderer averages the addressed positions and writes that average back to each
///     member, matching the assembled NPC raster path without destroying animation-ready bind data.
/// </summary>
internal sealed class BethesdaViewerBoundaryStitchGroup
{
    internal required BethesdaViewerMeshVertexIndex[] Vertices { get; init; }
}

/// <summary>
///     Versioned, renderer-neutral input to a direct Bethesda mesh/NPC viewer. The contract owns
///     scene semantics only; D3D resources, camera state, archive handles, and GLB objects belong to
///     adapters outside this type.
/// </summary>
internal sealed class BethesdaViewerScene
{
    internal const int CurrentContractVersion = 1;

    private readonly Dictionary<string, int> _namedNodes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, DecodedTexture> _generatedTextures =
        new(StringComparer.OrdinalIgnoreCase);

    internal BethesdaViewerScene(
        string sourceLabel,
        BethesdaViewerScenePurpose purpose,
        BethesdaViewerBounds? bounds = null,
        BethesdaGame game = BethesdaGame.Unknown,
        IEnumerable<string>? textureSourcePaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLabel);

        SourceLabel = sourceLabel;
        Purpose = purpose;
        Bounds = bounds;
        Game = game;
        TextureSourcePaths = SnapshotTextureSourcePaths(textureSourcePaths);
        Nodes.Add(new BethesdaViewerNode
        {
            Name = "SceneRoot",
            ParentIndex = null,
            LocalTransform = Matrix4x4.Identity,
            WorldTransform = Matrix4x4.Identity,
            Role = BethesdaViewerNodeRole.Root
        });
    }

    internal int ContractVersion => CurrentContractVersion;

    internal string SourceLabel { get; }

    internal BethesdaViewerScenePurpose Purpose { get; }

    /// <summary>
    ///     Game identity selected by the source parser/assembler. Shader and unit-system policy must
    ///     consume this value rather than attempting to infer a game from material paths or names.
    /// </summary>
    internal BethesdaGame Game { get; }

    /// <summary>
    ///     Immutable archive/directory names from which authored texture payloads can be resolved.
    ///     These are resource descriptors, not open archive handles: the native viewer owns the
    ///     resolver and GPU-cache lifetime that it creates from this snapshot.
    /// </summary>
    internal IReadOnlyList<string> TextureSourcePaths { get; }

    internal BethesdaViewerBounds? Bounds { get; set; }

    internal List<BethesdaViewerNode> Nodes { get; } = [];

    internal List<BethesdaViewerMeshPart> MeshParts { get; } = [];

    internal List<BethesdaViewerAnimationClip> AnimationClips { get; } = [];

    /// <summary>
    ///     Bind-pose boundary matches captured after NPC/creature composition. Mesh-part and vertex
    ///     indices remain stable for the scene lifetime, allowing the native renderer to reapply the
    ///     same seam correction after every animated skinning update.
    /// </summary>
    internal List<BethesdaViewerBoundaryStitchGroup> BoundaryStitchGroups { get; } = [];

    /// <summary>
    ///     Scene-owned RGBA resources that have no archive payload, including NPC FaceGen/EGT
    ///     composites. Keys use the same canonical, case-insensitive paths as
    ///     <see cref="NifTextureResolver" />. Values are private snapshots made by
    ///     <see cref="AddGeneratedTexture" />; consumers may read them for upload but must not
    ///     mutate their pixel arrays. No resolver, archive, GPU resource, or device lifetime leaks
    ///     into the scene contract.
    /// </summary>
    internal IReadOnlyDictionary<string, DecodedTexture> GeneratedTextures => _generatedTextures;

    internal static int RootNodeIndex => 0;

    /// <summary>Appends a node without changing any source transform or index semantics.</summary>
    internal int AddNode(
        string name,
        int? parentIndex,
        Matrix4x4 localTransform,
        Matrix4x4 worldTransform,
        BethesdaViewerNodeRole role,
        string? lookupName = null,
        int? sourceBlockIndex = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var nodeIndex = Nodes.Count;
        Nodes.Add(new BethesdaViewerNode
        {
            Name = name,
            LookupName = lookupName,
            ParentIndex = parentIndex,
            LocalTransform = localTransform,
            WorldTransform = worldTransform,
            Role = role,
            SourceBlockIndex = sourceBlockIndex
        });

        if (!string.IsNullOrWhiteSpace(lookupName) && !_namedNodes.ContainsKey(lookupName))
        {
            _namedNodes.Add(lookupName, nodeIndex);
        }

        return nodeIndex;
    }

    internal bool TryGetNodeIndex(string lookupName, out int nodeIndex)
    {
        return _namedNodes.TryGetValue(lookupName, out nodeIndex);
    }

    /// <summary>
    ///     Captures a deep copy of a generated texture under its canonical renderer lookup path.
    ///     Copying makes the scene independent of the resolver cache that produced the texture and
    ///     allows that cache entry to be evicted as soon as NPC assembly completes.
    /// </summary>
    internal void AddGeneratedTexture(string texturePath, DecodedTexture texture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(texturePath);
        ArgumentNullException.ThrowIfNull(texture);
        if (texture.MipLevels.Count == 0)
        {
            throw new ArgumentException("A generated texture must contain at least one mip level.", nameof(texture));
        }

        _generatedTextures[NifTexturePathUtility.Normalize(texturePath)] = CloneTexture(texture);
    }

    /// <summary>Resolves a generated texture using the same path normalization as archive textures.</summary>
    internal bool TryGetGeneratedTexture(string texturePath, out DecodedTexture? texture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(texturePath);
        return _generatedTextures.TryGetValue(NifTexturePathUtility.Normalize(texturePath), out texture);
    }

    private static DecodedTexture CloneTexture(DecodedTexture source)
    {
        var mipLevels = new DecodedTextureMipLevel[source.MipLevels.Count];
        for (var index = 0; index < source.MipLevels.Count; index++)
        {
            var mip = source.MipLevels[index];
            mipLevels[index] = new DecodedTextureMipLevel
            {
                Width = mip.Width,
                Height = mip.Height,
                Pixels = (byte[])mip.Pixels.Clone()
            };
        }

        return new DecodedTexture { MipLevels = mipLevels };
    }

    private static string[] SnapshotTextureSourcePaths(IEnumerable<string>? sourcePaths)
    {
        return sourcePaths?
                   .Where(static path => !string.IsNullOrWhiteSpace(path))
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .ToArray() ?? [];
    }
}
