using System.Security.Cryptography;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Fixtures for the cut-1b slice 3 tests: exact name bytes, a one-occurrence-per-block table, an in-memory skeleton
///     lookup that records every path it is asked about, and a synthetic NIF read into a
///     <see cref="NifModelReadState" /> and node graph the way <see cref="NifModelReader" /> builds them (parse, decode
///     each block, reachability, palette names, node walk), so the target maps are built from real decoded blocks.
/// </summary>
internal static class NifModelAnimationTargetTestSupport
{
    /// <summary>The Latin-1 bytes of a text (one byte per character up to U+00FF).</summary>
    public static byte[] Latin1(string text)
    {
        return Encoding.Latin1.GetBytes(text);
    }

    /// <summary>Names for blocks 0, 1, 2, ... in order, as exact Latin-1 bytes.</summary>
    public static NifModelTargetName[] Names(params string[] names)
    {
        return names.Select(static (name, block) => new NifModelTargetName(Latin1(name), block)).ToArray();
    }

    /// <summary>An occurrence table in which block b has the single node occurrence b.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> OneOccurrenceEach(int blockCount)
    {
        return Enumerable.Range(0, blockCount).Select(static block => (IReadOnlyList<int>)new[] { block }).ToArray();
    }

    /// <summary>
    ///     A lookup over an in-memory source holding one file per given path, and the list of every path it was asked
    ///     about, in order.
    /// </summary>
    public static (NifModelSkeletonLookup Lookup, List<string> Asked) MemoryLookup(params string[] present)
    {
        var source = new InMemoryAssetSource("data");
        var entries = new Dictionary<string, AssetEntry>(StringComparer.Ordinal);
        foreach (var path in present)
        {
            entries[path] = source.Add(path, Latin1(path));
        }

        var asked = new List<string>();
        NifModelSkeletonLookup lookup = path =>
        {
            asked.Add(path);
            if (entries.TryGetValue(path, out var entry))
            {
                return new[] { new ModelSourceItem(source, entry) };
            }

            return Array.Empty<ModelSourceItem>();
        };
        return (lookup, asked);
    }

    /// <summary>
    ///     Reads a built fixture into a read state and its node graph, as <see cref="NifModelReader" /> does before its
    ///     sub-readers run: NiNode and geometry blocks decode strictly, every other block tolerantly.
    /// </summary>
    public static (NifModelReadState State, NifModelNodeGraph Graph) ReadGraph(byte[] bytes)
    {
        var info = NifParser.Parse(bytes);
        Assert.NotNull(info);
        var schema = NifSchema.LoadEmbedded();
        var decoder = new NifBlockDecoder(schema, info, bytes);
        var footer = decoder.ValidateLayout();
        var blocks = new NifDecodedBlock[info.Blocks.Count];
        for (var i = 0; i < blocks.Length; i++)
        {
            var type = info.Blocks[i].TypeName;
            var mode = schema.Inherits(type, "NiNode") || NifModelGeometryReader.IsStrictType(type)
                ? NifDecodeMode.Strict
                : NifDecodeMode.Tolerant;
            blocks[i] = decoder.Decode(i, mode);
        }

        var (item, input) = NifModelTestSupport.Open(bytes);
        input.Dispose();
        var state = new NifModelReadState(item, ModelNativeDetail.Metadata, bytes,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), info, schema, decoder, footer, blocks);
        var cancellationToken = TestContext.Current.CancellationToken;
        var reachable = NifModelCoverage.ReferenceReachability(blocks, footer.Roots, cancellationToken);
        var palette = NifModelPaletteNames.Read(state, reachable, cancellationToken);
        return (state, NifModelNodeReader.Read(state, palette, cancellationToken));
    }
}
