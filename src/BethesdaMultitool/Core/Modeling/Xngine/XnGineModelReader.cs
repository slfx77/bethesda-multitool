using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Xngine;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Modeling.Units;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Catalog;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     The XnGine <c>.3D</c> model reader, <c>bmt.xngine.3d</c> (cut-1c plan sections 3 and 6, slice 5: geometry only):
///     a v2.5, v2.6 or v2.7 static mesh from Daggerfall's ARCH3D.BSA, Battlespire's 3D.BSA, 3D.BS6 and loose files, or
///     Redguard's loose 3dart files and ROB segments, read straight into Shared's <see cref="ModelDocument" /> with an
///     independent census of every source element.
/// </summary>
/// <remarks>
///     <para>
///         Reading: the record is read once under <see cref="MaximumSourceBytes" /> and hashed (for an LZSS entry the
///         source serves the decompressed payload, which is what is hashed; the stored digest goes to the container
///         row). A 3dfx tag is refused as <see cref="NotSupportedException" /> with the probe's reason, a record
///         satisfying the <c>.3DC</c> shape test likewise (it belongs to <c>bmt.redguard.3dc</c>: its header offsets are
///         frame 1's, so a <c>.3D</c> read would return the wrong points without error), and any other tag as
///         <see cref="InvalidDataException" />. The game comes from <see cref="XnGineGameIdentity" /> (the
///         <c>bmt.game</c> option, the container facts of <c>item.Source</c>, the <c>bmt.classic-game</c> option, the
///         content), which also fixes the plane-header layout and the unit row; its diagnostics pass through. The record
///         is then parsed once with STORED UVs (<see cref="XnGineUvHandling.Stored" />) and every plane is triangulated
///         (<see cref="XnGineTriangulation" />) and given its reference-rule UVs (<see cref="XnGineUvRule" />, with the
///         packed-UV unfold only for a Daggerfall record whose container names an object id below 905, plan D2).
///     </para>
///     <para>
///         The document: one scene, one Transform node at the identity holding mesh 0, one primitive per surviving
///         texture key (<see cref="XnGineModelGeometry" />) with a placeholder material each
///         (<see cref="XnGineModelMaterials" />; textures and palettes are slice 7's), units from the game's row with the
///         identity evidence in front, the Y-up basis the reader normalized to (<see cref="XnGineModelBasis" />), the
///         source provenance (the entry path and the parsed payload's SHA-256), the native rows
///         (<see cref="XnGineModelNativeState" />) and the coverage (<see cref="XnGineModelCoverage" />).
///     </para>
///     <para>
///         The per-item cache scope is not used: slice 5 resolves no companion, so any scope the registration hands over
///         is accepted and left untouched (slice 8 brings the composite cache). Inspection reads exactly what conversion
///         reads and no stage decodes a pixel, so <see cref="SupportsInspectionWithoutPixelDecoding" /> holds; slice 7
///         must keep it true when it adds images (plan decision D9).
///     </para>
/// </remarks>
public sealed class XnGineModelReader : IModelSourceReader, IModelSourceFormatMetadataProvider
{
    /// <summary>The largest record the reader loads (<see cref="XnGineModelFormatMetadata.MaximumSourceBytes" />).</summary>
    public const int MaximumSourceBytes = XnGineModelFormatMetadata.MaximumSourceBytes;

    /// <inheritdoc />
    public string FormatId => XnGineModelFormatMetadata.FormatId;

    /// <inheritdoc />
    /// <remarks>No stage of this reader decodes an image; slice 5 carries no image at all.</remarks>
    public bool SupportsInspectionWithoutPixelDecoding => true;

    /// <inheritdoc />
    public ModelSourceFormatMetadata FormatMetadata => XnGineModelFormatMetadata.Description;

    /// <inheritdoc />
    public ModelProbeResult Probe(ModelSourceCandidate candidate)
    {
        return XnGineModelProbe.Probe(candidate);
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
                $"The XnGine record declares {item.Length} bytes, more than the reader's {MaximumSourceBytes}-byte budget."));
        }

        var bytes = ReadBytes(context.Input, MaximumSourceBytes, cancellationToken);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var content = XnGineContentFacts.Measure(bytes);
        Admit(bytes, content);

        var container = ClassicContainerFacts.TryQuery(item.Source, item.Reference);
        var identity = XnGineGameIdentity.Resolve(context.AppOptions, container, content);
        var diagnostics = identity.Diagnostics
            .Select(static d => XnGineModelDiagnostics.Create(d.Code, d.Message))
            .ToList();
        CheckElementBudget(content.PlaneCount);

        var mesh = XnGineMesh.Parse(bytes, identity.ObjectId ?? 0, identity.Layout, uvHandling: XnGineUvHandling.Stored);
        var unfold = DecideUnfold(identity, diagnostics);
        var planes = new XnGineTriangulatedPlane[mesh.Planes.Count];
        var rules = new XnGineUvRuleResult[mesh.Planes.Count];
        for (var k = 0; k < planes.Length; k++)
        {
            if ((k & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var plane = mesh.Planes[k];
            var corners = plane.Points.Select(corner => mesh.Points[corner.PointIndex]).ToList();
            planes[k] = XnGineTriangulation.Triangulate(k, corners, plane.Normal);
            rules[k] = XnGineUvRule.Compute(XnGineTriangulation.CornerVectors(corners),
                plane.Points.Select(static corner => new XnGineCornerUv(corner.U, corner.V)).ToArray(), unfold);
        }

        var pointDomain = XnGineModelGeometry.PointDomainId(item.Reference.SourceId, item.Reference.Path, container,
            mesh.PointListOffset);
        var geometry = XnGineModelGeometry.Build(mesh, planes, rules, pointDomain,
            static _ => (XnGineModelGeometry.FallbackTextureSize, XnGineModelGeometry.FallbackTextureSize),
            cancellationToken);
        var tiling = mesh.Tiling();
        AddDiagnostics(diagnostics, content, identity, mesh, planes, rules, geometry, tiling, unfold);
        var unreferenced = geometry.UnreferencedPoints(mesh.Points.Count);

        var nativeStates = XnGineModelNativeState.Build(new XnGineModelNativeState.Inputs(item, bytes, sha256, mesh,
            identity, container, planes, rules, geometry, unreferenced, tiling, unfold, context.NativeDetail),
            cancellationToken);
        var name = identity.ObjectId?.ToString(CultureInfo.InvariantCulture) ??
                   Path.GetFileNameWithoutExtension(item.Reference.Path);
        var node = new SceneNode(name, Matrix4x4.Identity, meshIndex: 0) { Role = SceneNodeRole.Transform };
        var units = ClassicModelUnits.For(identity.Game);
        var document = new ModelDocument(FormatId, name, [new SceneDefinition(name, [0])], [node],
            [new SceneMesh(name, geometry.Primitives)], XnGineModelMaterials.Placeholders(geometry.Keys),
            sourceIdentity: item.Reference.ToString(), diagnostics: diagnostics,
            units: new SceneUnits(units.MetersPerUnit, units.Provenance, identity.Evidence + ": " + units.Evidence),
            sourceBasis: XnGineModelBasis.Basis, nativeStates: nativeStates)
        {
            SourceProvenance = new SceneSourceProvenance(item.Reference.Path, sha256)
        };
        var coverage = XnGineModelCoverage.Build(item.Reference, bytes, tiling, planes, unreferenced.Count,
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
                throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                    $"The XnGine record exceeds the reader's {maximumBytes}-byte budget."));
            }

            output.Write(buffer, 0, count);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    /// <summary>
    ///     The read's admission, the probe's rules restated over the whole record: a 3dfx tag and a <c>.3DC</c> are
    ///     declined, another tag is not an XnGine mesh.
    /// </summary>
    /// <exception cref="NotSupportedException">A 3dfx mesh, or a record the <c>.3DC</c> reader owns.</exception>
    /// <exception cref="InvalidDataException">The tag is not a mesh tag.</exception>
    private static void Admit(byte[] bytes, XnGineContentFacts content)
    {
        if (XnGineModelProbe.FxartTags.Contains(content.Tag, StringComparer.Ordinal))
        {
            throw new NotSupportedException(XnGineModelFormatMetadata.FxartUnsupportedReason);
        }

        if (!content.IsMeshTag)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"The record's tag '{content.Tag}' is not an XnGine mesh tag ({string.Join(", ", XnGineContentFacts.MeshTags)})."));
        }

        if (XnGineModelProbe.SatisfiesAnimatedShape(bytes))
        {
            throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The record satisfies the Redguard .3DC shape test (header +16 = {content.HeaderPlus16} frames, frame " +
                $"block at +20 = {content.HeaderPlus20}); it is read by bmt.redguard.3dc, not as a static .3D, whose " +
                $"header offsets would be frame 1's."));
        }
    }

    /// <summary>Refuses a record whose census would exceed the coverage element budget.</summary>
    /// <exception cref="NotSupportedException">The header's plane count leaves no room for the census.</exception>
    private static void CheckElementBudget(int planeCount)
    {
        // Every declared area can leave at most one unclaimed range before it, plus one at the end.
        const int areaBound = 6;
        if ((long)planeCount + XnGineModelCoverage.FixedElements + areaBound > ModelSourceCoverage.MaximumElements)
        {
            throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The record declares {planeCount} planes, more than the {ModelSourceCoverage.MaximumElements} census " +
                $"elements a source may have allow (the largest retail mesh has 1,740)."));
        }
    }

    /// <summary>
    ///     Whether the packed-UV unfold applies (plan decision D2): only on a Daggerfall record whose container names an
    ///     object id below <see cref="XnGineMesh.PackedUvObjectIdLimit" />. A Daggerfall record with no object id gets a
    ///     diagnostic, because the reference's gate cannot be evaluated.
    /// </summary>
    private static bool DecideUnfold(XnGineGameIdentity identity, List<SceneDiagnostic> diagnostics)
    {
        if (identity.Game != BethesdaGame.Daggerfall)
        {
            return false;
        }

        if (identity.ObjectId is { } id)
        {
            return id < XnGineMesh.PackedUvObjectIdLimit;
        }

        diagnostics.Add(XnGineModelDiagnostics.Create(XnGineModelDiagnostics.UnfoldUndetermined,
            string.Create(CultureInfo.InvariantCulture,
                $"Daggerfall record with no object id (no numbered archive supplied one), so the reference's " +
                $"packed-UV gate (ids below {XnGineMesh.PackedUvObjectIdLimit}) cannot be evaluated; the unfold is not " +
                $"applied and the portable UVs use the stored values of corners 0 to 2")));
        return false;
    }

    /// <summary>The document diagnostics beside the identity's (plan section 3.4).</summary>
    private static void AddDiagnostics(List<SceneDiagnostic> diagnostics, XnGineContentFacts content,
        XnGineGameIdentity identity, XnGineMesh mesh, IReadOnlyList<XnGineTriangulatedPlane> planes,
        IReadOnlyList<XnGineUvRuleResult> rules, XnGineModelGeometryResult geometry,
        ByteAreaTiling tiling, bool unfold)
    {
        var culture = CultureInfo.InvariantCulture;
        if (content.HeaderPlus16 != 0)
        {
            diagnostics.Add(XnGineModelDiagnostics.Create(XnGineModelDiagnostics.StaticFrameCount, string.Create(culture,
                $"header +16 = {content.HeaderPlus16} on a mesh read as static (it fails the .3DC shape test); the value " +
                $"is kept in bmt.xngine.header and its meaning is not established (a frame count is inferred)")));
        }

        if (unfold)
        {
            diagnostics.Add(XnGineModelDiagnostics.Create(XnGineModelDiagnostics.UnfoldApplied, string.Create(culture,
                $"packed-UV unfold applied to corners 0 to 2 (Daggerfall object id {identity.ObjectId} below " +
                $"{XnGineMesh.PackedUvObjectIdLimit}, as the reference applies it), changing " +
                $"{rules.Sum(static rule => rule.UnfoldedValues)} stored values in the portable UVs; Assumed: on polygons " +
                $"with values in the fold range the stored later corners agree with the raw reading and contradict the " +
                $"unfold; xngine.uv16 keeps the stored values")));
        }

        XnGineModelDiagnostics.AddGeometry(diagnostics, planes, geometry);

        if (tiling.Overlaps.Count > 0 || tiling.OutOfRange.Count > 0 ||
            (mesh.PlaneDataOffset > 0 && !tiling.Areas.Any(static area => area.Name == XnGineModelCoverage.PlaneDataElement)))
        {
            diagnostics.Add(XnGineModelDiagnostics.Create(XnGineModelDiagnostics.AreaTiling, string.Create(culture,
                $"the declared byte areas do not tile cleanly: {tiling.Overlaps.Count} overlap(s), " +
                $"{tiling.OutOfRange.Count} area(s) out of range, plane data at {mesh.PlaneDataOffset} " +
                $"{(tiling.Areas.Any(static area => area.Name == XnGineModelCoverage.PlaneDataElement) ? "fits" : "absent or out of range")}")));
        }
    }
}
