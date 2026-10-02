using System.Globalization;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The NIF source census and its classification (plan section 3, "Common to every block", and the block tables).
///     The census is the header block table itself (<see cref="NifHeaderLayout.BlockTypeIndices" /> into
///     <see cref="NifHeaderLayout.BlockTypeNames" />), one element <c>block:{i}</c> per block with the type name as its
///     kind, independent of what the reader managed to type. Every block is classified exactly once, as Typed or
///     NativeOnly with a reason; the reader emits no Dropped rows.
/// </summary>
/// <remarks>
///     Precedence: a sub-reader's decision for a block it visited (<see cref="NifModelBlockDisposition" />) comes first,
///     because only it knows whether the block fed typed state (the geometry reader decides every placed geometry block,
///     its data and its morph data; the material reader every property and texture set a drawable placement inherits;
///     the texture source every NiSourceTexture it resolved; the skin reader every skin instance, skin data and skin
///     partition of placed geometry; the layer reader every LOD data block of a placed LOD node; the palette names every
///     NiDefaultAVObjectPalette that supplied a display name). Otherwise a block that produced no node and is not
///     reachable from the footer roots through block references (<c>Ref</c> links; back pointers are not followed) is
///     NativeOnly <see cref="UnreachableReason" />; an NiAVObject that is referenced but not placed through Children is
///     NativeOnly <see cref="OutsideHierarchyReason" />; the node types of the plan's node table (including
///     NiBillboardNode, NiSwitchNode and NiLODNode, whose billboards and layer sets slice 8 types) are Typed when they
///     produced a node; and every other type is NativeOnly with its category reason. A property, texture or skin block of
///     a typed kind that reaches this table fed nothing typed, so its reason says so. Since cut-1b slice 10 the animation
///     stage (<see cref="NifModelAnimationReader" />) is one of those sub-readers: its per-block decisions join the
///     others (Typed wins), and an animation block it made no decision about takes its plan 2.1 table row
///     (<see cref="NifModelAnimationCoverage.TableRow" />, particle reach included) instead of the retired
///     'later-cut(1b): animation' reason.
/// </remarks>
internal static class NifModelCoverage
{
    /// <summary>Reason for a block that feeds nothing reachable from the footer roots.</summary>
    public const string UnreachableReason = "not reachable from footer roots";

    /// <summary>Reason for an NiAVObject that is referenced but never placed through a Children link.</summary>
    public const string OutsideHierarchyReason = "not reachable from footer roots through Children links (no node placed)";

    /// <summary>Reason for a placed geometry, and its data, that yields no triangle or no vertex (no primitive).</summary>
    public const string EmptyGeometryReason = "no drawable triangles";

    /// <summary>
    ///     Reason for a console packed block whose stream table matches none of the six measured layouts
    ///     (<see cref="NifPackedGeometryLayout" />), the geometry whose streams live in it, and that geometry's skin
    ///     blocks: nothing is guessed from a vector length.
    /// </summary>
    public const string PackedLayoutUnknownReason = "packed layout unknown: console packed streams not decoded";

    /// <summary>
    ///     Reason for a console packed block (and its geometry and skin blocks) whose payload is empty or absent beside
    ///     a non-zero vertex count, whose data-block structure is not the measured one, whose vertex count is not Num
    ///     Vertices (static layouts) or the sum of the partition counts (skinned layouts), or that stores a shape vertex
    ///     repeated across partitions with differing packed positions (Shared requires one position per source point).
    ///     A packed block declaring zero vertices is <see cref="EmptyGeometryReason" />, as an inline block is.
    /// </summary>
    public const string PackedPayloadReason =
        "packed payload empty, absent or inconsistent with its counts: console packed streams not decoded";

    /// <summary>
    ///     Reason for a shape that names a skin instance but whose packed layout carries no weights: its packed vertex
    ///     order was not measured (no retail shape does this), so nothing is typed.
    /// </summary>
    public const string PackedSkinnedStaticLayoutReason =
        "packed vertex order not established: skinned shape with a layout carrying no weights";

    /// <summary>Reason for a geometry type outside the cut-1a geometry table (its node is still placed).</summary>
    public const string OtherGeometryReason = "geometry type outside the cut-1a geometry table; the node is placed";

    /// <summary>Reason for geometry or morph data that no placed geometry references.</summary>
    public const string UnusedGeometryDataReason = "unreferenced by placed geometry";

    /// <summary>Reason for morph data whose vertex count differs from its geometry's.</summary>
    public const string MorphVertexCountReason = "morph vertex count does not match its geometry";

    /// <summary>Reason for morph data holding a non-finite target vector.</summary>
    public const string MorphNonFiniteReason = "non-finite morph vectors";

    /// <summary>Reason for morph data whose Relative Targets value is neither 0 nor 1.</summary>
    public const string MorphRelativeTargetsReason = "Relative Targets value: semantics not established";

    /// <summary>Reason for morph data with no morph beyond the base (morph 0).</summary>
    public const string NoMorphTargetsReason = "no morph targets beyond the base (morph 0)";

    /// <summary>Reason for the morph data of a second morpher controller on one geometry.</summary>
    public const string ExtraMorpherReason = "second morpher controller on the same geometry: not typed";

    /// <summary>
    ///     Reason for a property of a typed kind that no drawable placed geometry inherits (it sits on a subtree without
    ///     drawable geometry, or every placement below it overrides it with a nearer property of the same slot).
    /// </summary>
    public const string UninheritedPropertyReason =
        "unreferenced by reachable geometry: no drawable placed geometry inherits this property";

    /// <summary>Reason for a texture set or NiSourceTexture that no typed material binds.</summary>
    public const string UnboundTextureReason = "unreferenced by reachable geometry: no typed material binds this texture";

    /// <summary>Reason for a skin instance, skin data or skin partition that no drawable placed geometry uses.</summary>
    public const string UnusedSkinReason = "unreferenced by drawable placed geometry: no typed skin uses this block";

    /// <summary>
    ///     Reason for the skin blocks of geometry whose skin has no vertex weights (NiSkinData stores none and no
    ///     partition carries them): influences are not established, so no skin is typed and the geometry stays unskinned.
    /// </summary>
    public const string SkinNoInfluencesReason =
        "no vertex weights in NiSkinData or its partitions: influences not established";

    /// <summary>Reason for the skin blocks of a skin whose inverse bind or weight is not finite (Shared rejects it).</summary>
    public const string SkinNonFiniteReason = "non-finite skin bind or weight: kept as native state";

    /// <summary>
    ///     Reason for the skin blocks of a skin with a null bone link or a bone block that is not placed in the scene
    ///     graph: its joints cannot all be established, so no skin is typed and the geometry stays unskinned (BMT's
    ///     renderer tolerates such bones by falling back to the bone block's own transform).
    /// </summary>
    public const string SkinUnplacedBoneReason =
        "a skin bone link is null or names a block outside the scene: joints not established";

    /// <summary>
    ///     Reason for the skin blocks of a skin whose influence table would exceed the reader's bound (more than
    ///     <see cref="NifModelSkinInfluences.MaximumStride" /> influences per vertex).
    /// </summary>
    public const string SkinStrideReason =
        "more influences per vertex than the reader types: kept as native state";

    /// <summary>Reason for the skin blocks of a skin whose joints lie under different scene roots.</summary>
    public const string SkinRootsReason =
        "skin joints span several scene roots: Shared requires one common ancestor";

    /// <summary>
    ///     Reason for a PC NiSkinPartition whose content feeds no typed state: the typed influences come from NiSkinData
    ///     and the instance is not a dismember instance (or its faces could not be typed); the partitions are checked
    ///     against the typed skin and kept as native state.
    /// </summary>
    public const string PartitionNativeReason =
        "hardware skin partitions: native state only; the typed influences come from NiSkinData";

    /// <summary>Reason for NiLODData blocks: their center and ranges (or proportions) have no typed vocabulary.</summary>
    public const string LodDataReason =
        "LOD selection data: no typed vocabulary (the LOD node's finest level is the default-on layer); native state";

    /// <summary>Reason for NiNode subclasses outside the plan's node table.</summary>
    public const string OtherNodeReason =
        "node subclass outside the cut-1a node table: the node is placed, its subclass fields are native state";

    /// <summary>Reason for particle systems placed as helper nodes.</summary>
    public const string ParticleNodeReason = "particles: no typed vocabulary";

    /// <summary>Reason for particle data, modifiers, emitters, colliders and controllers.</summary>
    public const string ParticleReason = "particles";

    /// <summary>Reason for Havok blocks.</summary>
    public const string HavokReason = "Havok (later-cut 2)";

    /// <summary>Reason for cameras and lights.</summary>
    public const string CameraLightReason = "later-cut(2): cameras and lights";

    /// <summary>Reason for multibound culling blocks.</summary>
    public const string MultiBoundReason = "multibound culling data";

    /// <summary>Reason for extra data without a typed carrier.</summary>
    public const string ExtraDataReason = "other extra data";

    /// <summary>Reason for furniture markers.</summary>
    public const string MarkerReason = "markers (later-cut 2)";

    /// <summary>Reason for embedded pixel data.</summary>
    public const string PixelDataReason = "later-cut(2): embedded pixel data";

    /// <summary>Reason for PC additional geometry streams.</summary>
    public const string AdditionalGeometryReason = "additional geometry streams: semantics not established";

    /// <summary>Reason for water shading.</summary>
    public const string WaterReason = "water shading: no typed vocabulary (later-cut 2)";

    /// <summary>Reason for properties without a typed vocabulary.</summary>
    public const string NoVocabularyReason = "no typed vocabulary";

    /// <summary>Reason for any block type the cut-1a tables do not name.</summary>
    public const string FallbackReason = "not typed in cut 1a";

    private const string AvObjectType = "NiAVObject";

    /// <summary>The plan's node table: NiNode subclasses whose semantics are the typed node alone.</summary>
    private static readonly HashSet<string> TypedNodeTypes = new(StringComparer.Ordinal)
    {
        "NiNode", "BSFadeNode", "BSValueNode", "BSOrderedNode", "BSMultiBoundNode", "BSRangeNode", "BSBlastNode",
        "BSDamageStage", "BSDebrisNode", "BSMasterParticleSystem"
    };

    private static readonly HashSet<string> GeometryDataTypes = new(StringComparer.Ordinal)
    {
        "NiTriShapeData", "NiTriStripsData", "NiMorphData"
    };

    /// <summary>The skin block types the skin reader types when drawable placed geometry uses them (slice 7).</summary>
    private static readonly HashSet<string> SkinTypes = new(StringComparer.Ordinal)
    {
        "NiSkinInstance", "BSDismemberSkinInstance", "NiSkinData", "NiSkinPartition"
    };

    /// <summary>The property types the material reader types when a drawable placement inherits them (slice 5).</summary>
    private static readonly HashSet<string> MaterialTypes = new(StringComparer.Ordinal)
    {
        "NiAlphaProperty", "NiStencilProperty", "NiZBufferProperty", "NiVertexColorProperty", "NiMaterialProperty",
        "BSShaderPPLightingProperty", "Lighting30ShaderProperty", "BSShaderNoLightingProperty", "SkyShaderProperty",
        "TileShaderProperty", "TallGrassShaderProperty", "NiTexturingProperty"
    };

    /// <summary>The texture blocks a typed material binds (slice 6).</summary>
    private static readonly HashSet<string> TextureTypes = new(StringComparer.Ordinal)
    {
        "BSShaderTextureSet", "NiSourceTexture"
    };

    private static readonly HashSet<string> ParticleSupportTypes = new(StringComparer.Ordinal)
    {
        "BSParentVelocityModifier", "BSWindModifier", "BSStripPSysData"
    };

    /// <summary>The census element identity for a block.</summary>
    public static string Identity(int blockIndex)
    {
        return string.Create(CultureInfo.InvariantCulture, $"block:{blockIndex}");
    }

    /// <summary>
    ///     The census, straight from the header block table: <c>block:{i}</c> with the type name each block's stored
    ///     type index names.
    /// </summary>
    /// <exception cref="InvalidDataException">A type index is outside the type table or names a blank type.</exception>
    public static IReadOnlyList<ModelSourceElement> Census(NifHeaderLayout header)
    {
        ArgumentNullException.ThrowIfNull(header);
        var elements = new ModelSourceElement[header.BlockCount];
        for (var i = 0; i < elements.Length; i++)
        {
            var typeIndex = header.BlockTypeIndices[i];
            if (typeIndex >= header.BlockTypeNames.Count)
            {
                throw new InvalidDataException(
                    $"NIF block {i} names block type {typeIndex}, but the header declares " +
                    $"{header.BlockTypeNames.Count} types.");
            }

            var type = header.BlockTypeNames[typeIndex];
            if (string.IsNullOrWhiteSpace(type))
            {
                throw new InvalidDataException($"NIF block type {typeIndex} (used by block {i}) has a blank name.");
            }

            elements[i] = new ModelSourceElement(Identity(i), type);
        }

        return elements;
    }

    /// <summary>
    ///     The census evidence, for example
    ///     <c>NIF header block table: 12 blocks, 5 types (20.2.0.7/11/34, BE)</c>.
    /// </summary>
    public static string CensusEvidence(NifHeaderLayout header)
    {
        ArgumentNullException.ThrowIfNull(header);
        return string.Create(CultureInfo.InvariantCulture,
            $"NIF header block table: {header.BlockCount} blocks, {header.BlockTypeNames.Count} types " +
            $"({NifModelProbe.FormatVersion(header.Version)}/{header.UserVersion}/{header.BsVersion}, " +
            $"{(header.IsBigEndian ? "BE" : "LE")})");
    }

    /// <summary>Builds the coverage from the census and one classification per census element.</summary>
    /// <exception cref="ArgumentException">A classification is missing, repeated or names an undeclared element.</exception>
    public static ModelSourceCoverage Build(AssetReference source, NifHeaderLayout header,
        IReadOnlyList<ModelSourceElement> census, IReadOnlyList<ModelSourceClassification> classifications,
        CancellationToken cancellationToken)
    {
        return new ModelSourceCoverage(source, CensusEvidence(header), census, classifications, cancellationToken);
    }

    /// <summary>Classifies one block (see the type remarks for the precedence).</summary>
    /// <param name="schema">The nif.xml definitions.</param>
    /// <param name="blockIndex">The block.</param>
    /// <param name="type">Its type name.</param>
    /// <param name="producedNode">Whether the block was placed as at least one node.</param>
    /// <param name="referenceReachable">Whether the block is reachable from the footer roots through Ref links.</param>
    /// <param name="disposition">A sub-reader's decision for the block, which takes precedence when present.</param>
    /// <param name="reachedOnlyFromParticles">
    ///     For an undecided animation interpolator or key-data block: true when only particle controllers reach it
    ///     (<see cref="NifModelAnimationCoverage.ParticleReach" />), which gives it the particle row.
    /// </param>
    public static ModelSourceClassification Classify(NifSchema schema, int blockIndex, string type, bool producedNode,
        bool referenceReachable, NifModelBlockDisposition? disposition = null, bool reachedOnlyFromParticles = false)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var identity = Identity(blockIndex);
        if (disposition is { } decided)
        {
            return new ModelSourceClassification(identity, decided.Kind, decided.Reason);
        }

        if (!producedNode)
        {
            if (!referenceReachable)
            {
                return new ModelSourceClassification(identity, ModelSourceCoverageKind.NativeOnly, UnreachableReason);
            }

            if (schema.Inherits(type, AvObjectType))
            {
                return new ModelSourceClassification(identity, ModelSourceCoverageKind.NativeOnly,
                    OutsideHierarchyReason);
            }
        }

        if (reachedOnlyFromParticles &&
            NifModelAnimationCoverage.TableRow(schema, type, true) is { } particleRow)
        {
            return new ModelSourceClassification(identity, ModelSourceCoverageKind.NativeOnly, particleRow.Reason);
        }

        var reason = CategoryReason(schema, type);
        return reason is null
            ? new ModelSourceClassification(identity, ModelSourceCoverageKind.Typed)
            : new ModelSourceClassification(identity, ModelSourceCoverageKind.NativeOnly, reason);
    }

    /// <summary>
    ///     The NativeOnly reason for a block type, or null for the plan's typed node types (NiBillboardNode, NiSwitchNode
    ///     and NiLODNode included) and the geometry types the geometry reader decides per block. Order matters: Havok and
    ///     particle controllers are checked before the generic controller rule, particles before geometry. A geometry,
    ///     data, morph data or skin block a sub-reader visited never reaches this table (its disposition wins), so the
    ///     data and skin rows here name blocks nothing placed and drawable references. An animation block (a controller,
    ///     interpolator, sequence, object palette, keyframe data or other animation data) takes its plan 2.1 table row
    ///     (<see cref="NifModelAnimationCoverage.TableRow" />): the animation stage made no decision about it.
    /// </summary>
    public static string? CategoryReason(NifSchema schema, string type)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(type);
        if (TypedNodeTypes.Contains(type))
        {
            return null;
        }

        if (schema.Inherits(type, "NiBillboardNode") || schema.Inherits(type, "NiSwitchNode"))
        {
            return null;
        }

        if (schema.Inherits(type, "NiNode"))
        {
            return OtherNodeReason;
        }

        if (type.StartsWith("bhk", StringComparison.Ordinal) ||
            string.Equals(type, "hkPackedNiTriStripsData", StringComparison.Ordinal))
        {
            return HavokReason;
        }

        if (schema.Inherits(type, "NiParticles"))
        {
            return ParticleNodeReason;
        }

        if (IsParticleSupport(schema, type))
        {
            return ParticleReason;
        }

        if (schema.Inherits(type, "NiCamera") || schema.Inherits(type, "NiLight"))
        {
            return CameraLightReason;
        }

        if (NifModelGeometryReader.IsGeometryType(type))
        {
            return null;
        }

        if (schema.Inherits(type, "NiTriBasedGeom"))
        {
            return OtherGeometryReason;
        }

        if (GeometryDataTypes.Contains(type) ||
            string.Equals(type, NifModelGeometryReader.PackedAdditionalDataType, StringComparison.Ordinal))
        {
            return UnusedGeometryDataReason;
        }

        if (string.Equals(type, "NiAdditionalGeometryData", StringComparison.Ordinal))
        {
            return AdditionalGeometryReason;
        }

        if (SkinTypes.Contains(type))
        {
            return UnusedSkinReason;
        }

        if (schema.Inherits(type, "NiLODData"))
        {
            return LodDataReason;
        }

        if (MaterialTypes.Contains(type))
        {
            return UninheritedPropertyReason;
        }

        if (TextureTypes.Contains(type))
        {
            return UnboundTextureReason;
        }

        if (string.Equals(type, "WaterShaderProperty", StringComparison.Ordinal))
        {
            return WaterReason;
        }

        if (string.Equals(type, "NiFogProperty", StringComparison.Ordinal))
        {
            return NoVocabularyReason;
        }

        if (string.Equals(type, "NiPixelData", StringComparison.Ordinal))
        {
            return PixelDataReason;
        }

        if (schema.Inherits(type, "BSMultiBound") || schema.Inherits(type, "BSMultiBoundData"))
        {
            return MultiBoundReason;
        }

        if (NifModelAnimationCoverage.TableRow(schema, type) is { } animation)
        {
            return animation.Reason;
        }

        if (schema.Inherits(type, "BSFurnitureMarker"))
        {
            return MarkerReason;
        }

        return schema.Inherits(type, "NiExtraData") ? ExtraDataReason : FallbackReason;
    }

    /// <summary>
    ///     Which blocks are reachable from the footer roots through <c>Ref</c> links (back pointers, <c>Ptr</c>, are not
    ///     followed). Links are read from each block's decoded value tree; a tolerant decode that stopped early
    ///     contributes the links it decoded.
    /// </summary>
    public static bool[] ReferenceReachability(IReadOnlyList<NifDecodedBlock> blocks, IReadOnlyList<int> roots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(roots);
        var reachable = new bool[blocks.Count];
        var pending = new Stack<int>();
        foreach (var root in roots)
        {
            if ((uint)root < (uint)reachable.Length && !reachable[root])
            {
                reachable[root] = true;
                pending.Push(root);
            }
        }

        var links = new List<int>();
        while (pending.TryPop(out var index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            links.Clear();
            CollectLinks(blocks[index].Root, links);
            foreach (var target in links)
            {
                if ((uint)target < (uint)reachable.Length && !reachable[target])
                {
                    reachable[target] = true;
                    pending.Push(target);
                }
            }
        }

        return reachable;
    }

    private static bool IsParticleSupport(NifSchema schema, string type)
    {
        return type.StartsWith("NiPSys", StringComparison.Ordinal) ||
               type.StartsWith("BSPSys", StringComparison.Ordinal) ||
               ParticleSupportTypes.Contains(type) ||
               schema.Inherits(type, "NiParticlesData") ||
               schema.Inherits(type, "NiPSysModifier") ||
               schema.Inherits(type, "NiPSysCollider") ||
               schema.Inherits(type, "NiPSysEmitterCtlr");
    }

    private static void CollectLinks(NifValue value, List<int> links)
    {
        switch (value)
        {
            case NifRefValue { IsPointer: false, IsNone: false } reference:
                links.Add(reference.Index);
                break;
            case NifStructValue structure:
                foreach (var field in structure.Fields)
                {
                    CollectLinks(field.Value, links);
                }

                break;
            case NifArrayValue array:
                foreach (var item in array.Items)
                {
                    CollectLinks(item, links);
                }

                break;
        }
    }
}
