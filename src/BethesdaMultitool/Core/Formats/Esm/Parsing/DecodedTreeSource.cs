using System.Buffers;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Schema;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing;

/// <summary>
///     Lazy per-record schema decoder over the still-open source file — the <see cref="DecodedNode" />
///     counterpart to <c>BtdHeightSource</c>, and deliberately the same shape: own nothing the caller
///     does not already own, serialize decodes behind one gate, cache a bounded working set, and
///     degrade to null rather than throw at a consumer.
///     <para>
///         Measured 2026-08-25 on Fallout 76's <c>SeventySix.esm</c>: eagerly decoding every browsable
///         record retained <b>1,873 MB</b> — 27% of the whole post-load managed heap — for trees that
///         only the record browser, the CLI <c>show</c> renderers and the presentation profiles ever
///         read, one record at a time. Nothing in the render path touches them at all.
///     </para>
///     <para>
///         Rebuilding is exact rather than approximate: the record is re-read from its
///         <see cref="DetectedMainRecord" /> descriptor (offset, header size, data size, compression
///         flag, endianness and <b>form version</b>) and re-decoded with the same
///         <see cref="RecordDef" />, so a lazily-produced tree is the tree the eager pass would have
///         built. Form version in particular is load-bearing — <c>SchemaRecordDecoder</c> selects
///         between union arms on it, so decoding without it silently picks the wrong layout.
///     </para>
/// </summary>
internal sealed class DecodedTreeSource
{
    /// <summary>
    ///     Trees kept decoded. Consumers are all one-record-at-a-time (a browser selection, a
    ///     <c>show</c> render, a profile), so this only has to cover re-reads of the record being
    ///     looked at plus a little history — it is a latency cache, not a working set.
    /// </summary>
    private const int MaxCachedTrees = 256;

    private readonly Dictionary<uint, LinkedListNode<CachedTree>> _byFormId = new();
    private readonly Dictionary<string, RecordDef> _byType;
    private readonly RecordParserContext _context;
    private readonly object _gate = new();
    private readonly LinkedList<CachedTree> _recency = new();

    internal DecodedTreeSource(RecordParserContext context, Dictionary<string, RecordDef> byType)
    {
        _context = context;
        _byType = byType;
    }

    /// <summary>
    ///     Whether lazy decoding is possible at all. Without a backing accessor there is nothing to
    ///     re-read, so the parser must keep decoding eagerly — a synthesized record set (some DMP
    ///     paths, tests building records by hand) has no file behind it.
    /// </summary>
    internal static bool CanServe(RecordParserContext context)
    {
        return context.Accessor is not null;
    }

    /// <summary>
    ///     The record's schema-decoded field tree, decoded on first request. Null when the record has
    ///     no descriptor, no registered <see cref="RecordDef" />, or could not be re-read — the same
    ///     null the eager path produces for an unschema'd type, so consumers need no new branch.
    /// </summary>
    internal IReadOnlyList<DecodedNode>? GetTree(DetectedMainRecord? descriptor)
    {
        if (descriptor is null || !_byType.TryGetValue(descriptor.RecordType, out var def))
        {
            return null;
        }

        lock (_gate)
        {
            if (_byFormId.TryGetValue(descriptor.FormId, out var node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                return node.Value.Tree;
            }

            var tree = Decode(descriptor, def);

            // Cached even when null: an unreadable record must not be re-attempted on every repaint
            // of the browser row that failed.
            var added = _recency.AddFirst(new CachedTree(descriptor.FormId, tree));
            _byFormId[descriptor.FormId] = added;
            if (_byFormId.Count > MaxCachedTrees)
            {
                var oldest = _recency.Last!;
                _recency.RemoveLast();
                _byFormId.Remove(oldest.Value.Key);
            }

            return tree;
        }
    }

    private IReadOnlyList<DecodedNode>? Decode(DetectedMainRecord descriptor, RecordDef def)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var recordData = _context.ReadRecordData(descriptor, buffer);
            if (recordData is null)
            {
                return null;
            }

            var (data, dataSize) = recordData.Value;
            var subrecords = new List<RawSubrecord>();
            foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, descriptor.IsBigEndian))
            {
                subrecords.Add(new RawSubrecord(sub.Signature, data.AsSpan(sub.DataOffset, sub.DataLength).ToArray()));
            }

            return SchemaRecordDecoder.Decode(
                def, subrecords, descriptor.IsBigEndian,
                game: _context.Game, formVersion: descriptor.FormVersion);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException
                                       or ObjectDisposedException)
        {
            // The file can be closed under a still-live record (session teardown racing a GUI
            // binding). Degrade to "no tree" rather than throwing across a data binding.
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed record CachedTree(uint Key, IReadOnlyList<DecodedNode>? Tree);
}
