using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Starfield;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Starfield;

/// <summary>
///     Shared plumbing for the Starfield <c>.mesh</c> reader tests: read a built stream through the real
///     <see cref="IModelSourceReader" /> contract (item, context, cache scope) and probe a bounded candidate. Streams come
///     from <see cref="StarfieldMeshTestBuilder" />, which shares no code with the decoder.
/// </summary>
internal static class StarfieldMeshModelTestSupport
{
    /// <summary>The virtual path streams are read from; its stem is the document name.</summary>
    public const string DefaultPath = "geometries/0123456789abcdef0123/fedcba9876543210fedc.mesh";

    /// <summary>The document name <see cref="DefaultPath" /> gives.</summary>
    public const string DefaultName = "fedcba9876543210fedc";

    /// <summary>The source id of the in-memory source.</summary>
    public const string SourceId = "memory";

    /// <summary>Reads a built stream with a fresh cache scope, as the Shared read lifetime would.</summary>
    public static ModelReadResult Read(byte[] bytes, IReadOnlyDictionary<string, string>? options = null,
        ModelNativeDetail detail = ModelNativeDetail.Metadata, string path = DefaultPath,
        ModelReadPurpose purpose = ModelReadPurpose.Conversion)
    {
        var source = new InMemoryAssetSource(SourceId);
        var item = new ModelSourceItem(source, source.Add(path, bytes));
        using var input = new MemoryStream(bytes, false);
        var context = new ModelReadContext(item, input, new NoCacheScope(), detail, options, purpose: purpose);
        return new StarfieldMeshModelReader().Read(item, context, CancellationToken.None);
    }

    /// <summary>The app options that set the game option to <paramref name="game" />.</summary>
    public static IReadOnlyDictionary<string, string> Game(string game)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal) { [BethesdaModelRegistration.GameOption] = game };
    }

    /// <summary>
    ///     Probes the first <paramref name="prefixLength" /> bytes (default: up to the 64 KiB probe budget). The candidate
    ///     is complete when the prefix is the whole stream and shorter than the budget (as Shared's helper decides) unless
    ///     <paramref name="isComplete" /> says otherwise. The path has no <c>.mesh</c> extension: recognition must come
    ///     from content alone. The declared length is the stream's length unless <paramref name="declaredLength" />
    ///     gives another.
    /// </summary>
    public static ModelProbeResult Probe(byte[] bytes, int? prefixLength = null, bool? isComplete = null,
        long? declaredLength = null)
    {
        var length = prefixLength ?? Math.Min(bytes.Length, ModelSourceCandidate.MaximumProbeBytes);
        var entry = new AssetEntry(new AssetReference(SourceId, "probe/candidate.bin"), declaredLength ?? bytes.Length);
        var candidate = new ModelSourceCandidate(entry, bytes.AsSpan(0, length),
            isComplete ?? (length == bytes.Length && length < ModelSourceCandidate.MaximumProbeBytes));
        return new StarfieldMeshModelReader().Probe(candidate);
    }

    /// <summary>The single primitive of mesh 0 (the main index list).</summary>
    public static ScenePrimitive Primary(ModelDocument document)
    {
        return Assert.Single(document.Meshes[0].Primitives);
    }

    /// <summary>The primitive's attribute stream with the given name.</summary>
    public static SceneAttributeStream Stream(ScenePrimitive primitive, string name)
    {
        return Assert.Single(primitive.Attributes, stream => stream.Name == name);
    }

    /// <summary>A stream's tuples read as little-endian u16 values.</summary>
    public static ushort[] U16(SceneAttributeStream stream)
    {
        var bytes = stream.CopyContent();
        var values = new ushort[bytes.Length / 2];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BitConverter.ToUInt16(bytes, i * 2);
        }

        return values;
    }

    /// <summary>A cache scope that holds nothing (the reader resolves no companion).</summary>
    internal sealed class NoCacheScope : IModelReadCacheScope
    {
        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
