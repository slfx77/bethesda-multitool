using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Redguard;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Modeling.Units;
using BethesdaMultitool.Core.Modeling.Xngine;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Catalog;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Redguard;

/// <summary>
///     The Redguard <c>.3DC</c> animated-mesh reader, <c>bmt.redguard.3dc</c> (cut-1c plan section 4, slice 6): a frame
///     stack read straight into Shared's <see cref="ModelDocument" />, its keyframe mapped exactly as the <c>.3D</c>
///     reader maps a static mesh and its later frames carried as morph targets driven by one Step clip, with an
///     independent census of every source element.
/// </summary>
/// <remarks>
///     <para>
///         Reading: the file is read once under <see cref="MaximumSourceBytes" /> and hashed. A 3dfx tag is refused as
///         <see cref="NotSupportedException" /> with the <c>.3D</c> reader's reason, a record failing the <c>.3DC</c>
///         shape test likewise (it is a static mesh, read by <c>bmt.xngine.3d</c>), a <c>v2.5</c> tag likewise
///         (<see cref="Redguard3DcModelFormatMetadata.V25UnsupportedReason" />), another tag as
///         <see cref="InvalidDataException" />, a header whose declared sizes do not fit the file as
///         <see cref="InvalidDataException" /> (<see cref="DeclaredSizeFailure" />), a census, pose clip or morph payload
///         over its budget as <see cref="NotSupportedException" /> (<see cref="CheckElementBudget" />,
///         <see cref="CheckMorphBudget" />), and a stack that does not tile exactly as <see cref="InvalidDataException" />
///         naming the reason (<see cref="Redguard3DcFile.TryParse" />, the acceptance rule). The frame table places
///         every block; the header's +24/+48/+52 offsets (frame 1's on wide files only) are recorded and never used.
///     </para>
///     <para>
///         The document: one scene, one Transform node at the identity holding mesh 0, one primitive per texture key
///         (<see cref="XnGineModelGeometry" /> over the keyframe parsed with STORED UVs, the portable UVs from the
///         <c>.3D</c> reference rule without the unfold, plan decision D3), the planes triangulated by the pose-union rule
///         (<see cref="XnGineTriangulation.TriangulatePoses" />: a corner is kept when any pose keeps it), every primitive
///         Flat (plan decision D4) and carrying the later frames as morph targets (<see cref="Redguard3DcModelPoses" />:
///         narrow int16 deltas exactly as position deltas, wide int32 poses exactly as absolute positions), the pose clip
///         (<see cref="Redguard3DcModelAnimation" />, with no authored duration and a derived final key), placeholder
///         materials named <c>TEXTURE.aaa#r</c> as the legacy export names them (<see cref="XnGineModelMaterials" />;
///         textures are slice 7's), the Redguard actor unit row with its diagnostic, the <c>.3D</c> reader's Y-up basis,
///         the source provenance, the native rows (<see cref="Redguard3DcModelNativeState" />) and the coverage
///         (<see cref="Redguard3DcModelCoverage" />). Every primitive shares one point domain whose identity is the
///         keyframe point block's (the container or source, the record and frame 0's point offset), never derived from
///         sizes or positions (Shared SA6). The points no vertex names (7 in 4 retail files) keep their stored value in
///         every frame in the <c>bmt.redguard.3dc.unreferenced-points</c> row, which the point blocks' coverage names.
///     </para>
///     <para>
///         The game is Redguard by format: a <c>.3DC</c> frame stack is Redguard's own (147 of 147 retail files are 3dart
///         actors). The <c>bmt.game</c> option may name <c>redguard</c> or <c>auto</c>; another value throws, because the
///         user asserted a game the file cannot belong to. A container or install that names another game is recorded
///         with <see cref="XnGineGameIdentity.StepRefutedDiagnostic" /> and skipped. The per-item cache scope is not used,
///         and no stage decodes a pixel, so <see cref="SupportsInspectionWithoutPixelDecoding" /> holds.
///     </para>
/// </remarks>
public sealed class Redguard3DcModelReader : IModelSourceReader, IModelSourceFormatMetadataProvider
{
    /// <summary>The largest file the reader loads (<see cref="Redguard3DcModelFormatMetadata.MaximumSourceBytes" />).</summary>
    public const int MaximumSourceBytes = Redguard3DcModelFormatMetadata.MaximumSourceBytes;

    /// <summary>The evidence the game rests on when no option names it.</summary>
    public const string FormatEvidence =
        "Redguard per format: a .3DC frame stack (the shape test and exact tiling; 147 of 147 retail .3DC files are " +
        "Redguard 3dart actors)";

    /// <inheritdoc />
    public string FormatId => Redguard3DcModelFormatMetadata.FormatId;

    /// <inheritdoc />
    /// <remarks>No stage of this reader decodes an image; slice 6 carries no image at all.</remarks>
    public bool SupportsInspectionWithoutPixelDecoding => true;

    /// <inheritdoc />
    public ModelSourceFormatMetadata FormatMetadata => Redguard3DcModelFormatMetadata.Description;

    /// <inheritdoc />
    public ModelProbeResult Probe(ModelSourceCandidate candidate)
    {
        return Redguard3DcModelProbe.Probe(candidate);
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

        cancellationToken.ThrowIfCancellationRequested();
        if (item.Length > MaximumSourceBytes)
        {
            throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The .3DC file declares {item.Length} bytes, more than the reader's {MaximumSourceBytes}-byte budget."));
        }

        var bytes = XnGineModelReader.ReadBytes(context.Input, MaximumSourceBytes, cancellationToken);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var fileName = Path.GetFileName(item.Reference.Path);
        Admit(bytes);
        CheckElementBudget(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16)));
        if (!TryParseStack(bytes, fileName, out var file, out var error))
        {
            throw new InvalidDataException(error);
        }

        var container = ClassicContainerFacts.TryQuery(item.Source, item.Reference);
        var diagnostics = new List<SceneDiagnostic>();
        var evidence = ResolveGame(context.AppOptions, container, diagnostics);

        var mesh = file.ParseKeyframeMesh(XnGineUvHandling.Stored);
        CheckMorphBudget(mesh.Planes.Sum(static plane => (long)plane.Points.Count), file.FrameCount);
        var poses = file.Frames.Select(static frame => frame.Points).ToArray();
        var planes = new XnGineTriangulatedPlane[mesh.Planes.Count];
        var rules = new XnGineUvRuleResult[mesh.Planes.Count];
        var poseDependent = new List<int>();
        for (var k = 0; k < planes.Length; k++)
        {
            if ((k & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var plane = mesh.Planes[k];
            var cornerPoints = plane.Points.Select(static corner => corner.PointIndex).ToArray();
            var corners = cornerPoints.Select(point => mesh.Points[point]).ToList();
            planes[k] = XnGineTriangulation.TriangulatePoses(k, cornerPoints, poses);
            rules[k] = XnGineUvRule.Compute(XnGineTriangulation.CornerVectors(corners),
                plane.Points.Select(static corner => new XnGineCornerUv(corner.U, corner.V)).ToArray(), unfold: false);
            if (corners.Count > 3 &&
                !XnGineTriangulation.Triangulate(k, corners, plane.Normal).KeptCorners.SequenceEqual(planes[k].KeptCorners))
            {
                poseDependent.Add(k);
            }
        }

        var pointDomain = XnGineModelGeometry.PointDomainId(item.Reference.SourceId, item.Reference.Path, container,
            file.FrameTable[0].PointOffset);
        var stack = new Redguard3DcModelPoses(file);
        var geometry = XnGineModelGeometry.Build(mesh, planes, rules, pointDomain,
            static _ => (XnGineModelGeometry.FallbackTextureSize, XnGineModelGeometry.FallbackTextureSize),
            cancellationToken, stack);
        var clip = Redguard3DcModelAnimation.Create(file.FrameCount);
        AddDiagnostics(diagnostics, file, planes, geometry, stack, poseDependent);
        var unreferenced = geometry.UnreferencedPoints(file.PointCount);

        var tiling = file.Tiling();
        var nativeStates = Redguard3DcModelNativeState.Build(new Redguard3DcModelNativeState.Inputs(item, bytes, sha256,
            file, mesh, evidence, container, planes, rules, geometry, poseDependent, unreferenced, tiling,
            context.NativeDetail), cancellationToken);
        var name = Path.GetFileNameWithoutExtension(item.Reference.Path);
        var node = new SceneNode(name, Matrix4x4.Identity, meshIndex: 0) { Role = SceneNodeRole.Transform };
        var units = ClassicModelUnits.ForRedguardActor();
        var animations = clip is null ? Array.Empty<SceneAnimation>() : new[] { clip };
        var document = new ModelDocument(FormatId, name, [new SceneDefinition(name, [0])], [node],
            [new SceneMesh(name, geometry.Primitives)], XnGineModelMaterials.Placeholders(geometry.Keys),
            animations: animations, sourceIdentity: item.Reference.ToString(), diagnostics: diagnostics,
            units: new SceneUnits(units.MetersPerUnit, units.Provenance, evidence + ": " + units.Evidence),
            sourceBasis: XnGineModelBasis.Basis, nativeStates: nativeStates)
        {
            SourceProvenance = new SceneSourceProvenance(item.Reference.Path, sha256)
        };
        var coverage = Redguard3DcModelCoverage.Build(item.Reference, bytes, tiling, planes, file.WideFrames,
            unreferenced.Count, cancellationToken);
        return new ModelReadResult(document, coverage);
    }

    /// <summary>
    ///     Parses a frame stack, reporting why rather than throwing: <see cref="Redguard3DcFile.TryParse" />, whose plane
    ///     walk can also throw <see cref="InvalidDataException" /> (a corner that addresses no point), folded into the
    ///     same answer. The probe asks this of a complete candidate, the read of the whole file.
    /// </summary>
    internal static bool TryParseStack(ReadOnlyMemory<byte> bytes, string name, out Redguard3DcFile file,
        out string error)
    {
        try
        {
            return Redguard3DcFile.TryParse(bytes, name, out file, out error);
        }
        catch (InvalidDataException exception)
        {
            file = null!;
            error = string.Create(CultureInfo.InvariantCulture, $"{name}: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    ///     The declared-size check the probe and the read make before <see cref="TryParseStack" /> (slice-6 review
    ///     finding 4), over a record that satisfies the shape test (the frame table is read through it): the keyframe's
    ///     points must fit the file (point count x 12 bytes), the plane headers must fit it (plane count x 8 bytes), and
    ///     every frame-table offset must lie inside it. <see cref="Redguard3DcFile" /> computes its block ends in int32, so
    ///     without this a point count whose x 12 wraps can tile (a 156-byte file declaring 357,913,942 points asks for a
    ///     4 GiB keyframe array and then throws <see cref="ArgumentOutOfRangeException" />), and an offset near
    ///     <see cref="int.MaxValue" /> wraps its block end below zero, past the parser's outside-the-file test, so the
    ///     stack tiles and its block slices throw later. With the check every block end stays below four times the file
    ///     length, so none wraps. A file whose blocks tile satisfies it already: no retail answer changes.
    /// </summary>
    /// <param name="bytes">The whole file, or a complete probe candidate.</param>
    /// <returns>Null when the declared sizes fit the file, else the reason.</returns>
    internal static string? DeclaredSizeFailure(ReadOnlySpan<byte> bytes)
    {
        var culture = CultureInfo.InvariantCulture;
        var length = bytes.Length;
        var pointCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        var planeCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        var frameCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        var keyframeBytes = (long)pointCount * Redguard3DcFile.WidePointLength;
        if (keyframeBytes > length)
        {
            return string.Create(culture,
                $"The header declares {pointCount} points, a {keyframeBytes}-byte keyframe, more than the {length}-byte " +
                $"file holds.");
        }

        var planeHeaderBytes = (long)planeCount * XnGineContentFacts.DaggerfallPlaneHeaderLength;
        if (planeHeaderBytes > length)
        {
            return string.Create(culture,
                $"The header declares {planeCount} planes, whose 8-byte plane headers alone need {planeHeaderBytes} " +
                $"bytes, more than the {length}-byte file holds.");
        }

        var table = BinaryPrimitives.ReadInt32LittleEndian(bytes[BinaryPrimitives.ReadInt32LittleEndian(bytes[20..])..]);
        var recordBytes = (int)(((long)BinaryPrimitives.ReadInt32LittleEndian(bytes[60..]) - table) / frameCount);
        string[] blocks = ["point", "normal", "plane-data"];
        for (var frame = 0; frame < frameCount; frame++)
        {
            for (var field = 0; field < blocks.Length; field++)
            {
                var offset = BinaryPrimitives.ReadInt32LittleEndian(bytes[(table + frame * recordBytes + field * 4)..]);
                if (offset < 0 || offset > length)
                {
                    return string.Create(culture,
                        $"Frame {frame}'s {blocks[field]} block offset {offset} lies outside the {length}-byte file.");
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     The read's admission, the probe's rules restated over the whole file: a 3dfx tag, a record failing the shape
    ///     test and a <c>v2.5</c> tag are declined (in that order, as the probe decides them), another tag is not an
    ///     XnGine mesh, and a header whose declared sizes do not fit the file is invalid (<see cref="DeclaredSizeFailure" />).
    /// </summary>
    /// <exception cref="NotSupportedException">A 3dfx or v2.5 tag, or a static mesh the <c>.3D</c> reader owns.</exception>
    /// <exception cref="InvalidDataException">The tag is not a mesh tag, or the declared sizes do not fit the file.</exception>
    private static void Admit(byte[] bytes)
    {
        if (bytes.Length < XnGineContentFacts.HeaderLength)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"{bytes.Length} bytes is shorter than the {XnGineContentFacts.HeaderLength}-byte header."));
        }

        var tag = XnGineModelProbe.Tag(bytes);
        if (XnGineModelProbe.FxartTags.Contains(tag, StringComparer.Ordinal))
        {
            throw new NotSupportedException(XnGineModelFormatMetadata.FxartUnsupportedReason);
        }

        if (!XnGineContentFacts.MeshTags.Contains(tag, StringComparer.Ordinal))
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"The record's tag '{tag}' is not an XnGine mesh tag ({string.Join(", ", XnGineContentFacts.MeshTags)})."));
        }

        if (!XnGineModelProbe.SatisfiesAnimatedShape(bytes))
        {
            throw new NotSupportedException(
                "The record fails the Redguard .3DC shape test (header +16 frames, the +20 frame block and a frame table " +
                "dividing into 3- or 4-dword records); a static XnGine mesh is read by bmt.xngine.3d.");
        }

        if (!Redguard3DcModelFormatMetadata.Tags.Contains(tag, StringComparer.Ordinal))
        {
            throw new NotSupportedException(Redguard3DcModelFormatMetadata.V25UnsupportedReason);
        }

        if (DeclaredSizeFailure(bytes) is { } failure)
        {
            throw new InvalidDataException(failure);
        }
    }

    /// <summary>
    ///     Refuses, from the header's counts and before the parse, a file whose census would exceed the coverage element
    ///     budget, or whose one-hot pose clip would exceed <see cref="Redguard3DcModelAnimation.MaximumWeights" /> (slice-6
    ///     review finding 5: the clip holds (N + 1) x (N - 1) weights, quadratic in the frame count, so the census bound
    ///     alone admitted 21,843 frames and a 1.78 GiB weight array).
    /// </summary>
    /// <param name="planeCount">Header +8.</param>
    /// <param name="frameCount">Header +16, positive once the shape test held.</param>
    /// <exception cref="NotSupportedException">The census or the pose clip is over its budget.</exception>
    internal static void CheckElementBudget(int planeCount, int frameCount)
    {
        var elements = (long)planeCount + (long)Redguard3DcModelCoverage.ElementsPerFrame * frameCount +
                       Redguard3DcModelCoverage.FixedElements;
        if (elements > ModelSourceCoverage.MaximumElements)
        {
            throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The file declares {planeCount} planes and {frameCount} frames, {elements} census elements, more than " +
                $"the {ModelSourceCoverage.MaximumElements} a source may have (the largest retail file has 881 planes " +
                $"and the longest 228 frames)."));
        }

        var weights = Redguard3DcModelAnimation.WeightCount(frameCount);
        if (weights > Redguard3DcModelAnimation.MaximumWeights)
        {
            throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The file declares {frameCount} frames, whose one-hot pose clip needs {weights} weights " +
                $"((N + 1) x (N - 1)), more than the reader's {Redguard3DcModelAnimation.MaximumWeights} (at most " +
                $"{Redguard3DcModelAnimation.MaximumFrames} frames; the longest retail stack has 228 frames, a " +
                $"51,983-weight clip)."));
        }
    }

    /// <summary>
    ///     Refuses, after the parse and before the geometry, a stack whose morph targets would exceed
    ///     <see cref="Redguard3DcModelPoses.MaximumPositions" /> (slice-6 review finding 5): N - 1 targets of one position
    ///     per split vertex, and every vertex is one plane corner, so (N - 1) x the plane corners bounds the payload.
    /// </summary>
    /// <param name="corners">The corners of every plane in the plane list.</param>
    /// <param name="frameCount">The parsed frame count.</param>
    /// <exception cref="NotSupportedException">The morph payload is over its budget.</exception>
    internal static void CheckMorphBudget(long corners, int frameCount)
    {
        var targets = Math.Max(frameCount - 1, 0);
        var positions = corners * targets;
        if (positions > Redguard3DcModelPoses.MaximumPositions)
        {
            throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The {targets} pose targets over {corners} plane corners need {positions} morph positions, more than " +
                $"the reader's {Redguard3DcModelPoses.MaximumPositions} (the largest retail stack needs 518,336: " +
                $"GOLMA001, 182 targets over 2,848 corners)."));
        }
    }

    /// <summary>
    ///     The game: Redguard by format (see the type remarks). Returns the evidence; a disagreeing container or install
    ///     adds a diagnostic.
    /// </summary>
    /// <exception cref="ArgumentException">The <c>bmt.game</c> option names another game.</exception>
    private static string ResolveGame(IReadOnlyDictionary<string, string> options, ClassicContainerFacts? container,
        List<SceneDiagnostic> diagnostics)
    {
        var evidence = FormatEvidence;
        if (options.TryGetValue(BethesdaModelRegistration.GameOption, out var value) &&
            !string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var game = XnGineGameIdentity.ParseGame(value);
            if (game != BethesdaGame.Redguard)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                    $"The {BethesdaModelRegistration.GameOption} option '{value}' asserts {game}, but a .3DC frame stack " +
                    $"is a Redguard format (147 of 147 retail .3DC files are Redguard 3dart actors)."), nameof(options));
            }

            evidence = string.Create(CultureInfo.InvariantCulture,
                $"Redguard per {BethesdaModelRegistration.GameOption}={value}, agreeing with the format (a .3DC frame stack)");
        }

        if (container is { Kind: not ClassicContainerKind.RedguardRob })
        {
            diagnostics.Add(XnGineModelDiagnostics.Create(XnGineGameIdentity.StepRefutedDiagnostic,
                string.Create(CultureInfo.InvariantCulture,
                    $"the container {container.ContainerName} ({container.Kind}) implies another game, but a .3DC frame " +
                    $"stack is a Redguard format; the step is skipped")));
        }

        if (options.TryGetValue(BethesdaModelRegistration.ClassicGameOption, out var install) &&
            !string.IsNullOrWhiteSpace(install) &&
            !(XnGineGameIdentity.TryParseXnGineGame(install, out var installGame) && installGame == BethesdaGame.Redguard))
        {
            diagnostics.Add(XnGineModelDiagnostics.Create(XnGineGameIdentity.StepRefutedDiagnostic,
                string.Create(CultureInfo.InvariantCulture,
                    $"the install says {install} ({BethesdaModelRegistration.ClassicGameOption}), but a .3DC frame stack " +
                    $"is a Redguard format; the step is skipped")));
        }

        return evidence;
    }

    /// <summary>The document diagnostics (plan section 4): the reader's own, then the shared geometry codes.</summary>
    private static void AddDiagnostics(List<SceneDiagnostic> diagnostics, Redguard3DcFile file,
        IReadOnlyList<XnGineTriangulatedPlane> planes, XnGineModelGeometryResult geometry, Redguard3DcModelPoses stack,
        IReadOnlyList<int> poseDependent)
    {
        var culture = CultureInfo.InvariantCulture;
        diagnostics.Add(XnGineModelDiagnostics.Create(Redguard3DcModelDiagnostics.ActorScaleUnknown,
            ClassicModelUnits.ActorScaleDiagnosticMessage));
        diagnostics.Add(XnGineModelDiagnostics.Create(Redguard3DcModelDiagnostics.UvUndecoded,
            "the .3DC UV encoding is not decoded (0 of 3,810 non-zero later corners fit any of 16 readings, and " +
            "34.8% of keyframe UV values lie in the packed-UV fold range): xngine.uv16 keeps the stored values, the " +
            "portable UVs apply the .3D reference rule to the keyframe without the unfold, and the materials are " +
            "placeholders"));
        diagnostics.Add(file.FrameCount >= 2
            ? XnGineModelDiagnostics.Create(Redguard3DcModelDiagnostics.PoseRateAssumed, string.Create(culture,
                $"the {file.FrameCount - 1} later frames play at an Assumed {Redguard3DcModelAnimation.FramesPerSecond} " +
                $"frames per second with Step interpolation (RE-3); the clip ends at {file.FrameCount}/" +
                $"{Redguard3DcModelAnimation.FramesPerSecond} s through a derived final key that holds the last pose one " +
                $"frame period"))
            : XnGineModelDiagnostics.Create(Redguard3DcModelDiagnostics.SingleFrame,
                "the stack holds only its keyframe, so it has no morph target and no clip"));
        if (poseDependent.Count > 0)
        {
            var shown = string.Join(", ",
                poseDependent.Take(16).Select(static k => k.ToString(CultureInfo.InvariantCulture)));
            var more = poseDependent.Count > 16 ? " and more" : string.Empty;
            diagnostics.Add(XnGineModelDiagnostics.Create(Redguard3DcModelDiagnostics.PoseDependentCorners,
                string.Create(culture,
                    $"{poseDependent.Count} n-gon(s) keep a corner in some pose that the keyframe's own corner test drops " +
                    $"(planes {shown}{more}), so they are fanned over the pose-union corners and their triangles differ " +
                    $"from the legacy keyframe-only export")));
        }

        XnGineModelDiagnostics.AddGeometry(diagnostics, planes, geometry);
        if (stack.ReachesFloatLimit)
        {
            diagnostics.Add(XnGineModelDiagnostics.Create(Redguard3DcModelDiagnostics.PosePositionRounded,
                "a later pose's coordinate magnitude reaches 2^24, so its float32 position is rounded"));
        }
    }
}
