using System.Globalization;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Catalog;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Starfield;

/// <summary>
///     The Starfield <c>.mesh</c> model reader, <c>bmt.starfield.mesh</c> (cut-2 plan
///     <c>docs/design/cut2-starfield-mesh-reader-plan-20260928.md</c>): one external geometry blob, the level of one
///     <c>BSGeometry</c> shape that a Starfield NIF names by path, read straight into Shared's <see cref="ModelDocument" />
///     with an independent census of every section.
/// </summary>
/// <remarks>
///     <para>
///         Reading: the units are resolved first (<see cref="StarfieldMeshModelUnits" />; a <c>--game</c> other than
///         Starfield throws <see cref="ArgumentException" />), then the stream is read once under
///         <see cref="MaximumSourceBytes" />, hashed, and parsed by the one decoder the renderer uses
///         (<see cref="StarfieldMeshFile.Decode" />; the owner's cut-1b ruling "ONE decode path"). A parse failure, and
///         every rule <see cref="StarfieldMeshModelFacts.Measure" /> checks (trailing bytes, an index count or LOD list
///         that is not a multiple of 3, an index at or beyond the vertex count, a stream count other than 0 or the vertex
///         count, a weight count other than vertices x weightsPerVertex, a tangent W code of 1 or 2, a first normal W of 1
///         without the meshlet tail), throws <see cref="InvalidDataException" /> naming the field and offset; more LOD
///         lists or weights per vertex than the probe admits throws <see cref="NotSupportedException" />.
///     </para>
///     <para>
///         The document: one scene whose roots are node 0 (mesh 0, the main index list) and one node per LOD list
///         (<see cref="StarfieldMeshModelLayers" />, the exclusive group with LOD 0 on); the geometry of
///         <see cref="StarfieldMeshModelGeometry" />; the placeholder material (<see cref="StarfieldMeshModelMaterials" />);
///         the Starfield units and the NIF basis; the source provenance (the entry path and the stream's SHA-256); the
///         native rows (<see cref="StarfieldMeshModelNativeState" />); the diagnostics
///         (<see cref="StarfieldMeshModelDiagnostics" />); and the coverage (<see cref="StarfieldMeshModelCoverage" />).
///         No image, sampler, skin or animation: a lone <c>.mesh</c> carries none.
///     </para>
///     <para>
///         The per-item cache scope is not used: the reader resolves no companion, so any scope is accepted and left
///         untouched. Inspection, preview and conversion read the same document, and no stage decodes a pixel, so
///         <see cref="SupportsInspectionWithoutPixelDecoding" /> holds.
///     </para>
/// </remarks>
public sealed class StarfieldMeshModelReader : IModelSourceReader, IModelSourceFormatMetadataProvider
{
    /// <summary>The largest stream the reader loads (<see cref="StarfieldMeshModelFormatMetadata.MaximumSourceBytes" />).</summary>
    public const int MaximumSourceBytes = StarfieldMeshModelFormatMetadata.MaximumSourceBytes;

    /// <inheritdoc />
    public string FormatId => StarfieldMeshModelFormatMetadata.FormatId;

    /// <inheritdoc />
    /// <remarks>No stage of this reader decodes an image; a <c>.mesh</c> carries none.</remarks>
    public bool SupportsInspectionWithoutPixelDecoding => true;

    /// <inheritdoc />
    public ModelSourceFormatMetadata FormatMetadata => StarfieldMeshModelFormatMetadata.Description;

    /// <inheritdoc />
    public ModelProbeResult Probe(ModelSourceCandidate candidate)
    {
        return StarfieldMeshModelProbe.Probe(candidate);
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
        var units = StarfieldMeshModelUnits.Resolve(context.AppOptions);
        if (item.Length > MaximumSourceBytes)
        {
            throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The .mesh declares {item.Length} bytes, more than the reader's {MaximumSourceBytes}-byte budget."));
        }

        var bytes = ReadBytes(context.Input, MaximumSourceBytes, cancellationToken);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var mesh = StarfieldMeshFile.Decode(bytes, out var failure) ??
                   throw new InvalidDataException("Starfield .mesh " + (failure?.ToString() ?? "could not be decoded") +
                                                  ".");
        var facts = StarfieldMeshModelFacts.Measure(mesh, bytes.Length);
        cancellationToken.ThrowIfCancellationRequested();

        var name = Path.GetFileNameWithoutExtension(item.Reference.Path);
        var geometry = StarfieldMeshModelGeometry.Build(mesh, facts, name, cancellationToken);
        var layers = StarfieldMeshModelLayers.Build(geometry, name, cancellationToken);
        var nativeStates = StarfieldMeshModelNativeState.Build(item, bytes, sha256, mesh, facts, context.NativeDetail);
        var document = new ModelDocument(FormatId, name, [new SceneDefinition(name, layers.Roots)], layers.Nodes,
            layers.Meshes, [StarfieldMeshModelMaterials.Placeholder()], sourceIdentity: item.Reference.ToString(),
            diagnostics: Diagnostics(mesh, facts), units: units, sourceBasis: StarfieldMeshModelUnits.Basis,
            nativeStates: nativeStates, layerSets: layers.LayerSets)
        {
            SourceProvenance = new SceneSourceProvenance(item.Reference.Path, sha256)
        };
        var coverage = StarfieldMeshModelCoverage.Build(item.Reference, mesh, cancellationToken);
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
                    $"The .mesh exceeds the reader's {maximumBytes}-byte budget."));
            }

            output.Write(buffer, 0, count);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    /// <summary>The document diagnostics (plan section 3.5), each code at most once.</summary>
    private static List<SceneDiagnostic> Diagnostics(StarfieldMeshFile mesh, StarfieldMeshModelFacts facts)
    {
        var culture = CultureInfo.InvariantCulture;
        var diagnostics = new List<SceneDiagnostic>
        {
            StarfieldMeshModelDiagnostics.Create(StarfieldMeshModelDiagnostics.MaterialInNif,
                "materials are named by the referencing NIF and resolved through Starfield's material database; a lone " +
                ".mesh carries none, so the primitive binds the placeholder " + StarfieldMeshModelMaterials.PlaceholderName)
        };

        if (mesh.WeightPairs is not null)
        {
            diagnostics.Add(StarfieldMeshModelDiagnostics.Create(StarfieldMeshModelDiagnostics.SkinPaletteInNif,
                string.Create(culture,
                    $"{mesh.WeightsPerVertex} bone and weight pair(s) per vertex are typed as the streams " +
                    $"{StarfieldMeshModelGeometry.BoneAttributePrefix}k and {StarfieldMeshModelGeometry.WeightAttributePrefix}k, " +
                    $"values as stored: the joint palette the bone indices address (BSSkin::Instance bones, " +
                    $"BSSkin::BoneData binds) lives in the referencing NIF, so no skin is bound")));
        }

        if (mesh.ColorBytes is not null)
        {
            diagnostics.Add(StarfieldMeshModelDiagnostics.Create(StarfieldMeshModelDiagnostics.ColorInterpretation,
                $"vertex colors are the non-primary stream {StarfieldMeshModelGeometry.ColorAttribute} (RGBA, color space " +
                $"Unknown) and the portable vertex color stays white: {StarfieldMeshModelGeometry.ColorEvidence}"));
        }

        if (facts.Sentinels > 0)
        {
            diagnostics.Add(StarfieldMeshModelDiagnostics.Create(StarfieldMeshModelDiagnostics.Dec4Sentinel,
                string.Create(culture,
                    $"{facts.NormalSentinelsByW.Sum()} normal(s) and {facts.TangentSentinelsByW.Sum()} tangent(s) carry " +
                    $"the Dec4 zero code (511, 511, 511); each is kept as decoded (-1/1023 per channel, length 0.00169), " +
                    $"counted in {StarfieldMeshModelNativeState.SentinelsKind}")));
        }

        if (facts.Uv0NonFinite + facts.Uv1NonFinite > 0)
        {
            diagnostics.Add(StarfieldMeshModelDiagnostics.Create(StarfieldMeshModelDiagnostics.UvNonFinite,
                string.Create(culture,
                    $"{facts.Uv0NonFinite} UV0 and {facts.Uv1NonFinite} UV1 component(s) are NaN or infinite; they read " +
                    $"0 in the portable coordinates and each affected set's exact half bits are kept in " +
                    $"{StarfieldMeshModelGeometry.RawUvAttribute(0)} or {StarfieldMeshModelGeometry.RawUvAttribute(1)}")));
        }

        var tailMismatch = TailMismatch(mesh, facts);
        if (tailMismatch is not null)
        {
            diagnostics.Add(StarfieldMeshModelDiagnostics.Create(StarfieldMeshModelDiagnostics.TailWithoutNormalW,
                tailMismatch));
        }

        var emptyLods = mesh.LodIndexLists.Select(static (list, index) => (list, index))
            .Where(static pair => pair.list.Length == 0).Select(static pair => pair.index + 1).ToList();
        if (emptyLods.Count > 0)
        {
            diagnostics.Add(StarfieldMeshModelDiagnostics.Create(StarfieldMeshModelDiagnostics.EmptyLod,
                "LOD list(s) " + string.Join(", ", emptyLods.Select(static level => level.ToString(CultureInfo.InvariantCulture))) +
                " store no indices; their nodes carry no mesh"));
        }

        var absent = new List<string>();
        if (mesh.Uv0Bits is null)
        {
            absent.Add("UV0 (the portable coordinates read 0)");
        }

        if (mesh.NormalCodes is null)
        {
            absent.Add("normals (the primitive is flat-shaded)");
        }

        if (mesh.TangentCodes is null)
        {
            absent.Add("tangents (none are typed)");
        }

        if (absent.Count > 0)
        {
            diagnostics.Add(StarfieldMeshModelDiagnostics.Create(StarfieldMeshModelDiagnostics.StreamAbsent,
                "the stream stores no " + string.Join(", no ", absent) + "; every retail file stores all three"));
        }

        if (facts.UnusedVertices > 0)
        {
            diagnostics.Add(StarfieldMeshModelDiagnostics.Create(StarfieldMeshModelDiagnostics.UnusedVertices,
                string.Create(culture,
                    $"{facts.UnusedVertices} of {facts.VertexCount} vertices are referenced by no triangle of the main " +
                    $"index list; they are kept in stored order")));
        }

        return diagnostics;
    }

    /// <summary>
    ///     The normal-W pairing the tail rule rests on, when this stream departs from it without being refused: W 0 with
    ///     the tail, W codes that differ between normals, or a code outside 0 and 1. Null when the stream follows it.
    /// </summary>
    private static string? TailMismatch(StarfieldMeshFile mesh, StarfieldMeshModelFacts facts)
    {
        if (facts.FirstNormalW is not { } first)
        {
            return null;
        }

        var used = Enumerable.Range(0, 4).Where(code => facts.NormalW[code] > 0).ToList();
        var mixed = used.Count > 1;
        var outside = used.Any(static code => code > 1);
        var tailWithZero = first == 0 && mesh.HasMeshletTail;
        if (!mixed && !outside && !tailWithZero)
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"normal W codes used: {string.Join(", ", used)}; first normal W {first}; meshlet tail " +
            $"{(mesh.HasMeshletTail ? "present" : "absent")}. On every retail file normal W is uniform, 1 exactly with " +
            $"the tail and 0 exactly without it; this stream is read as its bytes say");
    }
}
