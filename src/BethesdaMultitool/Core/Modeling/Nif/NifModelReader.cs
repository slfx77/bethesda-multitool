using System.Globalization;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using Slfx77.Multitool.Core.Documents;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Catalog;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The NIF model reader (design section 2.3, plan sections 2 and 3; cut-1b plan section 4, slice 10; cut 2): 20.2.0.7,
///     user 11, a scene graph at BS 14, 21, 26, 32 and 34 or a <c>.kf</c> animation stream at those and BS 24, 25, 27, 28,
///     30, 31 and 33, in either byte order, and since cut 2 a <c>.kf</c> animation stream at the one pre-20.2.0.5 key,
///     little-endian 20.0.0.4 at user 10 or 11, BS 11 (<see cref="NifModelProbe.IsLegacyKfKey" />: the Oblivion-era
///     <c>.kf</c> identity, of which FNV ships five files, bounded to the eight block types measured on them,
///     <see cref="NifModelProbe.LegacyKfBlockTypes" />), read straight into Shared's <see cref="ModelDocument" /> with an
///     independent census of every header block.
/// </summary>
/// <remarks>
///     <para>
///         Slices 2 to 8: the node hierarchy with names (palette names where a node has none,
///         <see cref="NifModelPaletteNames" />), roles and the TRS rule, units and basis, the stored geometry and morph
///         targets of every placed NiTriShape, BSSegmentedTriShape and NiTriStrips (<see cref="NifModelGeometryReader" />),
///         the effective material of every placement (<see cref="NifModelMaterialReader" />) with its textures
///         (<see cref="NifModelTextureSource" />: companions resolved through the read context, DDS descriptors, the DDX
///         gate), the skins of PC-layout geometry with their joints, inverse binds, influences and dismember face
///         streams (<see cref="NifModelSkinReader" />), switch, LOD and hidden layer sets and billboards
///         (<see cref="NifModelLayerReader" />; the billboard facing and the document's reflected-face rule follow the
///         read's <see cref="NifModelBillboardSource" />, each billboard occurrence declares its rest world scalar as
///         <see cref="SceneBillboard.SourceScale" />, and after the animation stage
///         <see cref="NifModelBillboardScaleSign" /> reports the billboards whose world scale is not positive, at rest
///         or at a scale value a clip states), native
///         state for the header, every block, every primitive, material, image and skin, and complete coverage. Slice 10
///         adds the X360 and PS3 packed geometry and skins (<see cref="NifPackedGeometryReader" />,
///         <see cref="NifPackedGeometryDecoder" />): the six measured layouts
///         are typed under the platform named by <see cref="BethesdaModelRegistration.PlatformOption" /> (X360 assumed
///         with a diagnostic when it is not set), and a packed block outside them is NativeOnly with a precise reason;
///         nothing is fabricated for it. Geometry blocks placed in the hierarchy always become nodes (with their
///         transforms), with a mesh when their geometry was typed.
///     </para>
///     <para>
///         Cut-1b slice 10, the switch-over (owner ruling D1: no option, flag or environment variable switches it): after
///         the placements, the animation stage (<see cref="NifModelAnimationReader" />) reads the placed graph with the
///         cut-1a material and geometry targets (<see cref="NifModelPropertyTargets.FromResults" />) under the read's
///         platform; the document gains its clips, their <c>bmt.nif.animation.clip</c> native rows
///         (<see cref="NifModelAnimationNativeState" />) and the stage's diagnostic
///         (<see cref="NifModelAnimationDiagnostics" />); the stage's per-block decisions merge with the other sub-readers'
///         (Typed wins) and an animation block it made no decision about takes its plan 2.1 table row
///         (<see cref="NifModelAnimationCoverage" />) instead of the retired 'later-cut(1b): animation' reason; the row
///         budget counts one row per clip (plan section 2.3). A file whose every footer root is an NiControllerSequence is
///         a <c>.kf</c> animation stream and is read by <see cref="NifModelAnimationStreamReader" /> (the skeleton from
///         <see cref="BethesdaModelRegistration.SkeletonOption" />, else the nearest ancestor <c>skeleton.nif</c>). Cut 2:
///         the 20.0.0.4 <c>.kf</c> key goes through the same path, its header sized by NifParser's legacy measure walk
///         (<see cref="NifHeaderLayout" />) and its sequences read through the Oblivion view
///         (<see cref="NifModelAnimationSource" />: the inline names and the String Palette entries become the file's
///         string table); a 20.0.0.4 file whose roots are not all sequences is refused with the <c>later-cut(2)</c> reason,
///         and so is a stream whose blocks use a type outside the eight measured ones
///         (<see cref="NifModelProbe.LegacyKfBlockTypeRejection" />, the rule the probe and the Python gate probe apply),
///         before any block is decoded.
///     </para>
///     <para>
///         Reading: the bytes are read once under <see cref="MaximumSourceBytes" /> and hashed, parsed by NifParser,
///         checked against the reader's keys, decoded once per block by <see cref="NifBlockDecoder" /> (strict for NiNode
///         subclasses and the geometry, geometry-data and morph-data types; tolerant for the rest, with any failure
///         reported as a diagnostic and kept in native state; a property, texture or skin block a sub-reader types must
///         nevertheless have decoded exactly), and checked for layout and footer before anything is built. Corrupt input
///         throws <see cref="InvalidDataException" />; an out-of-scope key, a scene graph at an animation-only BS version,
///         roots that mix a sequence with other blocks, and a <c>.kf</c> with no skeleton or no expressible clip throw
///         <see cref="NotSupportedException" />.
///     </para>
///     <para>
///         Inspection (<see cref="ModelReadPurpose.Inspection" />) reads exactly what conversion reads: no stage of this
///         reader decodes a pixel. Image work is header inspection, the BC1 selector scan and the DDX block relayout, all
///         through <see cref="INifTextureCodec" />.
///     </para>
/// </remarks>
public sealed class NifModelReader : IModelSourceReader, IModelSourceFormatMetadataProvider
{
    /// <summary>The largest NIF the reader loads; larger inputs are refused before any allocation beyond it.</summary>
    public const int MaximumSourceBytes = 256 * 1024 * 1024;

    /// <summary>
    ///     The most blocks one file may declare: one native-state row per block plus the header row must fit
    ///     <see cref="ModelDocument.MaximumNativeStates" />, which also keeps the census inside
    ///     <see cref="ModelSourceCoverage.MaximumElements" />. The per-primitive, per-material, per-image, per-skin and
    ///     per-clip rows are checked once they are known (<see cref="CheckRowBudget" />), and a file whose rows together
    ///     exceed the budget is not supported.
    /// </summary>
    public const int MaximumBlocks = ModelDocument.MaximumNativeStates - 1;

    /// <summary>Diagnostic code for a block kept as native state from a partial (tolerant) decode.</summary>
    public const string DecodeIncompleteDiagnostic = "bmt.nif.decode-incomplete";

    private const int MaximumDecodeDiagnostics = 256;

    private readonly INifTextureCodec _codec;

    /// <summary>Creates the reader with the production texture codec.</summary>
    public NifModelReader()
        : this(NifTextureCodec.Instance)
    {
    }

    /// <summary>Creates the reader with an explicit texture codec (a counting seam for tests).</summary>
    internal NifModelReader(INifTextureCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _codec = codec;
    }

    /// <inheritdoc />
    public string FormatId => "bmt.nif";

    /// <inheritdoc />
    /// <remarks>
    ///     No stage decodes pixels: DDS descriptors come from header inspection and the BC1 selector scan, and a DDX's
    ///     block relayout is allowed during inspection (plan decisions, 2026-09-24).
    /// </remarks>
    public bool SupportsInspectionWithoutPixelDecoding => true;

    /// <inheritdoc />
    public ModelSourceFormatMetadata FormatMetadata => NifModelFormatMetadata.Description;

    /// <inheritdoc />
    public ModelProbeResult Probe(ModelSourceCandidate candidate)
    {
        return NifModelProbe.Probe(candidate);
    }

    /// <inheritdoc />
    public ModelReadResult Read(ModelSourceItem item, ModelReadContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(context);
        if (item.Reference != context.Item.Reference || !ReferenceEquals(item.Source, context.Item.Source))
        {
            throw new ArgumentException("The reader and context must borrow the same source occurrence.",
                nameof(item));
        }

        if (context.CacheScope is not NifModelReadCache cache)
        {
            throw new ArgumentException("NIF reading requires a fresh NifModelReadCache.", nameof(context));
        }

        cache.Bind(item.Reference);
        cancellationToken.ThrowIfCancellationRequested();
        var units = NifModelUnits.Resolve(context.AppOptions);
        var platform = NifPackedPlatformOption.Resolve(context.AppOptions);
        if (item.Length > MaximumSourceBytes)
        {
            throw new NotSupportedException(
                $"The NIF declares {item.Length} bytes, more than the reader's {MaximumSourceBytes}-byte budget.");
        }

        var bytes = ReadBytes(context.Input, MaximumSourceBytes, cancellationToken);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var info = NifParser.Parse(bytes)
                   ?? throw new InvalidDataException("The NIF header could not be parsed (truncated or malformed).");
        if (NifModelProbe.ScopeRejection(info.BinaryVersion, info.UserVersion, info.BsVersion, info.IsBigEndian)
            is { } rejection)
        {
            throw new NotSupportedException(rejection);
        }

        if (info.BlockCount > MaximumBlocks)
        {
            throw new NotSupportedException(
                $"The NIF declares {info.BlockCount} blocks, more than the {MaximumBlocks} the model budgets allow.");
        }

        var schema = NifSchema.LoadEmbedded();
        var decoder = new NifBlockDecoder(schema, info, bytes);
        var footer = decoder.ValidateLayout();
        var census = NifModelCoverage.Census(decoder.Header);
        var isAnimationStream = IsAnimationStream(schema, info, footer);
        if (!isAnimationStream &&
            NifModelProbe.GraphScopeRejection(info.BinaryVersion, info.UserVersion, info.BsVersion, info.IsBigEndian)
                is { } graphRejection)
        {
            throw new NotSupportedException(graphRejection);
        }

        if (isAnimationStream &&
            NifModelProbe.LegacyKfBlockTypeRejection(info.BinaryVersion, info.UserVersion, info.BsVersion,
                info.IsBigEndian, info.Blocks.Select(static block => block.TypeName)) is { } typeRejection)
        {
            throw new NotSupportedException(typeRejection);
        }

        var blocks = DecodeBlocks(decoder, schema, info, cancellationToken);
        var state = new NifModelReadState(item, context.NativeDetail, bytes, sha256, info, schema, decoder, footer,
            blocks);
        if (isAnimationStream)
        {
            return NifModelAnimationStreamReader.Read(FormatId, state, context, cache, census, units, platform,
                cancellationToken);
        }

        var reachable = NifModelCoverage.ReferenceReachability(blocks, footer.Roots, cancellationToken);
        var palette = NifModelPaletteNames.Read(state, reachable, cancellationToken);
        var graph = NifModelNodeReader.Read(state, palette, cancellationToken);
        state.Diagnostics.AddRange(graph.Diagnostics);
        state.Diagnostics.AddRange(palette.Diagnostics);
        AddDecodeDiagnostics(state);

        var billboardSource = NifModelBillboardSource.Resolve(info, context.AppOptions, platform);
        var layers = NifModelLayerReader.Read(state, graph, billboardSource, cancellationToken);
        var skins = new NifModelSkinReader(state, graph, cancellationToken);
        var textures = new NifModelTextureSource(state, context, cache, _codec, cancellationToken);
        var materials = new NifModelMaterialReader(state, graph, textures, cancellationToken);
        var geometry = NifModelGeometryReader.Read(state, graph, materials.Resolve, skins, platform,
            cancellationToken);
        var materialResult = materials.Complete();
        var textureResult = textures.Complete();
        var skinResult = skins.Complete();
        state.Diagnostics.AddRange(geometry.Diagnostics);
        state.Diagnostics.AddRange(materialResult.Diagnostics);
        state.Diagnostics.AddRange(textureResult.Diagnostics);
        state.Diagnostics.AddRange(skinResult.Diagnostics);
        state.Diagnostics.AddRange(layers.Diagnostics);
        var paletteDispositions = NifModelPaletteNames.Dispositions(graph);
        graph = NifModelNodeReader.WithPlacements(graph, new NifModelNodePlacements(geometry.MeshByNode,
            skinResult.SkinByNode, skinResult.JointNodes, layers.BillboardByNode));

        // Cut-1b slice 10: the animation stage runs on the placed graph with the cut-1a targets, unconditionally.
        var targets = NifModelPropertyTargets.FromResults(materialResult, geometry);
        var animation = NifModelAnimationReader.ReadNif(state, graph, targets, platform, cancellationToken);
        CheckRowBudget(blocks.Length, geometry.PrimitiveRows.Count, materialResult.NativeRows.Count,
            textureResult.NativeRows.Count, skinResult.NativeRows.Count, animation.Clips.Count, 0);
        state.Diagnostics.AddRange(NifModelAnimationDiagnostics.ForClassifications(animation.Clips.Count,
            NifModelAnimationCoverage.ClassifyFile(state, animation, cancellationToken)));
        state.Diagnostics.AddRange(
            NifModelBillboardScaleSign.Report(graph, layers, animation.Clips, cancellationToken));

        var dispositions = MergeDispositions(geometry.Dispositions, materialResult.Dispositions,
            textureResult.Dispositions, skinResult.Dispositions, layers.Dispositions, paletteDispositions,
            animation.Dispositions);
        var particleOnly = NifModelAnimationCoverage.ParticleReach(state, cancellationToken);
        var classifications = new ModelSourceClassification[blocks.Length];
        for (var i = 0; i < classifications.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            classifications[i] = NifModelCoverage.Classify(schema, i, blocks[i].Type,
                graph.OccurrencesByBlock[i].Count > 0, reachable[i],
                dispositions.TryGetValue(i, out var disposition) ? disposition : null, particleOnly.Contains(i));
        }

        var clipRows = NifModelAnimationNativeState.Build(state, animation, 0, cancellationToken);
        var prebuiltRows = materialResult.NativeRows.Concat(textureResult.NativeRows).Concat(skinResult.NativeRows)
            .Concat(clipRows).ToArray();
        var nativeStates = NifModelNativeState.Build(state, graph, geometry, prebuiltRows,
            FedTargets(materialResult, textureResult, skinResult, layers), layers.NodeFactsByBlock, cancellationToken);
        var name = Path.GetFileNameWithoutExtension(item.Reference.Path);
        var document = new ModelDocument(FormatId, name, [new SceneDefinition(name, graph.RootNodeIndices)],
            graph.Nodes, geometry.Meshes, materialResult.Materials, textureResult.Images, materialResult.Samplers,
            animation.Clips, sourceIdentity: item.Reference.ToString(), skins: skinResult.Skins,
            diagnostics: state.Diagnostics, units: units, sourceBasis: NifModelUnits.Basis, nativeStates: nativeStates,
            layerSets: layers.LayerSets)
        {
            SourceProvenance = new SceneSourceProvenance(item.Reference.Path, sha256),
            ReflectedFaces = billboardSource.ReflectedFaces
        };
        var coverage = NifModelCoverage.Build(item.Reference, decoder.Header, census, classifications,
            cancellationToken);
        return new ModelReadResult(document, coverage);
    }

    /// <summary>Copies at most the budget from the borrowed stream and leaves it open on every outcome.</summary>
    /// <exception cref="NotSupportedException">The stream holds more than <paramref name="maximumBytes" />.</exception>
    internal static byte[] ReadBytes(Stream input, int maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, (long)maximumBytes - output.Length + 1));
            if (count == 0)
            {
                break;
            }

            if (output.Length + count > maximumBytes)
            {
                throw new NotSupportedException(
                    $"The NIF exceeds the reader's {maximumBytes}-byte budget.");
            }

            output.Write(buffer, 0, count);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    /// <summary>
    ///     The native-state row budget (plan section 2.3; cut-1b slice 10): one header row, one row per block, per
    ///     emitted primitive, material, image and skin, one <c>bmt.nif.animation.clip</c> row per clip and, for a
    ///     <c>.kf</c>, the <c>bmt.nif.animation.skeleton</c> row must together fit
    ///     <see cref="ModelDocument.MaximumNativeStates" />.
    /// </summary>
    /// <param name="blocks">The file's blocks.</param>
    /// <param name="primitives">The primitive rows.</param>
    /// <param name="materials">The material rows.</param>
    /// <param name="images">The image rows.</param>
    /// <param name="skins">The skin rows.</param>
    /// <param name="clips">The clips (one row each).</param>
    /// <param name="skeletonRows">1 for a <c>.kf</c> (its skeleton provenance row), 0 for a scene graph.</param>
    /// <exception cref="NotSupportedException">The rows exceed the budget.</exception>
    internal static void CheckRowBudget(int blocks, int primitives, int materials, int images, int skins, int clips,
        int skeletonRows)
    {
        var rowCount = 1L + blocks + primitives + materials + images + skins + clips + skeletonRows;
        if (rowCount > ModelDocument.MaximumNativeStates)
        {
            throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The NIF's {blocks} blocks, {primitives} primitives, {materials} materials, {images} images, " +
                $"{skins} skins, {clips} clips and {skeletonRows} skeleton row(s) need more native-state rows than " +
                $"the {ModelDocument.MaximumNativeStates} a document allows."));
        }
    }

    /// <summary>
    ///     Classifies the footer roots (plan section 1, self-check 7; cut-1b slice 10): true for a <c>.kf</c> animation
    ///     stream, whose every root is an NiControllerSequence; false for a scene graph, whose every root must be an
    ///     NiAVObject.
    /// </summary>
    /// <param name="schema">The nif.xml definitions.</param>
    /// <param name="info">NifParser's header and block table.</param>
    /// <param name="footer">The validated footer.</param>
    /// <returns>True for a <c>.kf</c> animation stream.</returns>
    /// <exception cref="NotSupportedException">
    ///     An animation-sequence root sits beside a root that is not an NiControllerSequence (or is itself an NiSequence
    ///     that is not one).
    /// </exception>
    /// <exception cref="InvalidDataException">A scene-graph root is not an NiAVObject.</exception>
    internal static bool IsAnimationStream(NifSchema schema, NifInfo info, NifFooter footer)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(footer);
        string? sequenceRoot = null;
        foreach (var root in footer.Roots)
        {
            var type = info.Blocks[root].TypeName;
            if (NifModelProbe.IsAnimationRoot(type))
            {
                sequenceRoot ??= type;
            }
        }

        if (sequenceRoot is not null)
        {
            foreach (var root in footer.Roots)
            {
                if (!NifModelProbe.IsAnimationStreamRoot(info.Blocks[root].TypeName))
                {
                    throw new NotSupportedException(NifModelProbe.MixedRootsReason(sequenceRoot));
                }
            }

            return true;
        }

        for (var i = 0; i < footer.Roots.Count; i++)
        {
            var root = footer.Roots[i];
            var type = info.Blocks[root].TypeName;
            if (!schema.Inherits(type, "NiAVObject"))
            {
                throw new InvalidDataException(
                    $"NIF footer root {i} is block {root} ({type}), which is not an NiAVObject.");
            }
        }

        return false;
    }

    /// <summary>
    ///     Decodes every block once: NiNode subclasses and the geometry types strictly, every other block tolerantly (a
    ///     strict failure is corrupt input).
    /// </summary>
    /// <exception cref="InvalidDataException">A strictly decoded block does not decode exactly.</exception>
    internal static NifDecodedBlock[] DecodeBlocks(NifBlockDecoder decoder, NifSchema schema, NifInfo info,
        CancellationToken cancellationToken)
    {
        var blocks = new NifDecodedBlock[info.Blocks.Count];
        for (var i = 0; i < blocks.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = info.Blocks[i].TypeName;
            var mode = schema.Inherits(type, "NiNode") || NifModelGeometryReader.IsStrictType(type)
                ? NifDecodeMode.Strict
                : NifDecodeMode.Tolerant;
            try
            {
                blocks[i] = decoder.Decode(i, mode);
            }
            catch (NifDecodeException failure)
            {
                throw new InvalidDataException(failure.Message, failure);
            }
        }

        return blocks;
    }

    /// <summary>Adds one diagnostic per block kept from a partial decode (bounded, with one overflow row).</summary>
    internal static void AddDecodeDiagnostics(NifModelReadState state)
    {
        var incomplete = state.Blocks.Where(block => !block.IsComplete).ToList();
        foreach (var block in incomplete.Take(MaximumDecodeDiagnostics))
        {
            var detail = block.Failure?.Message ??
                         (block.Problems.Count > 0
                             ? $"{block.Problems.Count} problem(s), first: {block.Problems[0].Message}"
                             : $"consumed {block.ConsumedBytes} of {block.Size} bytes");
            if (detail.Length > 2048)
            {
                detail = detail[..2048] + " [truncated]";
            }

            state.Diagnostics.Add(new SceneDiagnostic(DecodeIncompleteDiagnostic, DocumentText.Raw(
                $"Block {block.Index} ({block.Type}) is kept as native state from a partial decode: {detail}")));
        }

        if (incomplete.Count > MaximumDecodeDiagnostics)
        {
            state.Diagnostics.Add(new SceneDiagnostic(DecodeIncompleteDiagnostic, DocumentText.Raw(
                $"{incomplete.Count - MaximumDecodeDiagnostics} more block(s) are kept from partial decodes; " +
                "see their bmt.nif.block native state.")));
        }
    }

    /// <summary>
    ///     Merges the sub-readers' coverage decisions. They decide disjoint block kinds (geometry, properties and texture
    ///     sets, source textures, skins, LOD data, object palettes, and since cut-1b slice 10 the animation blocks); should
    ///     two ever decide the same block, Typed wins, and between two NativeOnly decisions the earlier argument's.
    /// </summary>
    private static Dictionary<int, NifModelBlockDisposition> MergeDispositions(
        params IReadOnlyDictionary<int, NifModelBlockDisposition>[] decisions)
    {
        var merged = new Dictionary<int, NifModelBlockDisposition>();
        foreach (var decision in decisions)
        {
            foreach (var (block, disposition) in decision)
            {
                if (!merged.TryGetValue(block, out var existing) || (!existing.IsTyped && disposition.IsTyped))
                {
                    merged[block] = disposition;
                }
            }
        }

        return merged;
    }

    /// <summary>
    ///     The element each block that fed typed state without producing a node or feeding a mesh fed, for its native row:
    ///     a property, texture set or source texture its first material, else image; a skin block its first skin; a LOD
    ///     data block its first LOD node occurrence.
    /// </summary>
    private static Dictionary<int, SceneElementRef> FedTargets(NifModelMaterialResult materials,
        NifModelTextureResult textures, NifModelSkinResult skins, NifModelLayerResult layers)
    {
        var targets = new Dictionary<int, SceneElementRef>();
        foreach (var (block, material) in materials.MaterialByFedBlock)
        {
            targets.TryAdd(block, new SceneElementRef(SceneElementKind.Material, material));
        }

        foreach (var (block, image) in textures.ImageByFedBlock)
        {
            targets.TryAdd(block, new SceneElementRef(SceneElementKind.Image, image));
        }

        foreach (var (block, skin) in skins.SkinByFedBlock)
        {
            targets.TryAdd(block, new SceneElementRef(SceneElementKind.Skin, skin));
        }

        foreach (var (block, node) in layers.NodeByFedBlock)
        {
            targets.TryAdd(block, new SceneElementRef(SceneElementKind.Node, node));
        }

        return targets;
    }
}
