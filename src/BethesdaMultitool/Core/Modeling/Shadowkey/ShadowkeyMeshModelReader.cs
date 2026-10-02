using System.Globalization;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Catalog;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Shadowkey;

/// <summary>
///     The Shadowkey mesh reader, <c>bmt.shadowkey.mesh</c> (cut-2 plan
///     <c>docs/design/cut2-shadowkey-reader-plan-20260928.md</c>, sections 3 and 5.1): one record of the N-Gage
///     <c>models.huge</c> pack (an entry of the <c>ShadowkeyPackBackend</c> archive, <c>NNN_name.bin</c>) or a loose
///     extracted record, read into Shared's <see cref="ModelDocument" /> with an independent census of its sections.
/// </summary>
/// <remarks>
///     <para>
///         Reading: the <see cref="BethesdaModelRegistration.GameOption" /> app option must be absent, <c>auto</c> or
///         Shadowkey (<see cref="ShadowkeyModelUnits.RequireShadowkey" />); the stream is read once under
///         <see cref="MaximumSourceBytes" />, hashed, and parsed by <see cref="ShadowkeyMesh.Parse" />, the decoder the
///         viewer uses (the cut-1b ruling "ONE decode path"). A record that does not walk, or whose UV vertex domain is
///         not well defined, throws <see cref="InvalidDataException" /> naming the field and offset.
///     </para>
///     <para>
///         The document is the Standalone shape of <see cref="ShadowkeyMeshDocumentBuilder" />: named after the entry's
///         stem, with the entry path and the record's SHA-256 as its provenance. The per-item cache scope is not used
///         (no companion is resolved), so any scope is accepted.
///     </para>
///     <para>
///         Inspection, preview and conversion read the same document, standard PNG payloads included; that is a recorded
///         deviation from the plan (section 2 puts PNG encoding on conversion reads only; cut-2 review finding 7). No
///         codec is decoded: a skin is direct-color 0x0RGB words, which <see cref="ShadowkeyMesh.Parse" /> reads as the
///         integers they are stored as, and its PNG lists the distinct words in first-appearance order (a pass over every
///         texel value), expands each nibble x17 and deflates the indices unfiltered. That pass is why
///         <see cref="SupportsInspectionWithoutPixelDecoding" /> is claimed on a reading, not by construction. The
///         payloads stay because Shared's GLB planner (<c>ModelGltfImages.PlanCore</c> at 2e7af70) plans an image only
///         from a PNG, DDS or TGA representation, so an inspection read without them would make <c>mesh info</c>'s
///         writer plans report every skin unsupported, a census that conversion contradicts.
///     </para>
/// </remarks>
public sealed class ShadowkeyMeshModelReader : IModelSourceReader, IModelSourceFormatMetadataProvider
{
    /// <summary>The largest record the reader loads (Assumed bound; the largest retail record is 257,784 bytes).</summary>
    public const int MaximumSourceBytes = ShadowkeyModelFormatMetadata.MaximumMeshBytes;

    /// <inheritdoc />
    public string FormatId => ShadowkeyModelFormatMetadata.MeshFormatId;

    /// <inheritdoc />
    public bool SupportsInspectionWithoutPixelDecoding => true;

    /// <inheritdoc />
    public ModelSourceFormatMetadata FormatMetadata => ShadowkeyModelFormatMetadata.Mesh;

    /// <inheritdoc />
    public ModelProbeResult Probe(ModelSourceCandidate candidate)
    {
        return ShadowkeyMeshModelProbe.Probe(candidate);
    }

    /// <inheritdoc />
    public ModelReadResult Read(ModelSourceItem item, ModelReadContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(context);
        if (item.Reference != context.Item.Reference || !ReferenceEquals(item.Source, context.Item.Source))
        {
            throw new ArgumentException("The reader and context must borrow the same source occurrence.", nameof(item));
        }

        cancellationToken.ThrowIfCancellationRequested();
        ShadowkeyModelUnits.RequireShadowkey(context.AppOptions);
        if (item.Length > MaximumSourceBytes)
        {
            throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The record declares {item.Length} bytes, more than the reader's {MaximumSourceBytes}-byte budget."));
        }

        var bytes = ReadBytes(context.Input, MaximumSourceBytes, cancellationToken);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var name = Path.GetFileNameWithoutExtension(item.Reference.Path);
        var mesh = ShadowkeyMesh.Parse(bytes, name);
        cancellationToken.ThrowIfCancellationRequested();

        var source = new ShadowkeyMeshDocumentSource(name, item.Reference.ToString(),
            new SceneSourceProvenance(item.Reference.Path, sha256), item.Reference.SourceId, item.Reference, 0,
            string.Empty);
        var document = ShadowkeyMeshDocumentBuilder.Build(mesh, bytes, ShadowkeyMeshDocumentShape.Standalone, source,
            context.NativeDetail, cancellationToken);
        var coverage = ShadowkeyModelCoverage.Mesh(item.Reference, mesh, cancellationToken);
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
                    $"The input exceeds the reader's {maximumBytes}-byte budget."));
            }

            output.Write(buffer, 0, count);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return output.ToArray();
    }
}
