using System.Buffers;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Schema;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing;

/// <summary>
///     Decodes a chosen set of record types into schema <see cref="DecodedNode" /> trees for a
///     <em>typed-primary</em> game (FNV/FO3): one that keeps its hand-written typed handlers as the Records
///     base but also has a registered schema. The result is a FormID → tree map that rides alongside the
///     typed models (in <see cref="Models.RecordCollection.DecodedTreesByFormId" />), giving the unified,
///     profile-driven presentation the same decoded substrate every other game already produces — without
///     disturbing the typed lists the rest of the app (conversion, reports, indexing) depends on.
///     <para>
///         Scoped to <see cref="ProfiledTypes" />: only record types that a presentation profile actually
///         renders are decoded, so the enrichment stays cheap and grows one type at a time as profiles land.
///     </para>
/// </summary>
internal static class SchemaTreeEnricher
{
    /// <summary>
    ///     Record types the unified presenter has a profile for (so they are worth decoding a tree for).
    ///     Grows as the curated-display rollout proceeds. Keep in sync with
    ///     <see cref="Presentation.Profiles.RecordProfiles" />.
    /// </summary>
    public static readonly IReadOnlySet<string> ProfiledTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "NPC_",
        "ARMO",
        "WEAP",
        "CREA",
        "QUST",
        "PACK",
        "DIAL"
    };

    public static Dictionary<uint, IReadOnlyList<DecodedNode>> Enrich(
        RecordParserContext context,
        IReadOnlyDictionary<string, RecordDef> schema,
        IReadOnlySet<string> recordTypes)
    {
        var result = new Dictionary<uint, IReadOnlyList<DecodedNode>>();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            foreach (var record in context.ScanResult.MainRecords)
            {
                if (!recordTypes.Contains(record.RecordType) ||
                    !schema.TryGetValue(record.RecordType, out var def))
                {
                    continue;
                }

                var read = context.ReadRecordData(record, buffer);
                if (read == null)
                {
                    continue;
                }

                var (data, size) = read.Value;
                var subs = new List<RawSubrecord>();
                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, size, record.IsBigEndian))
                {
                    subs.Add(new RawSubrecord(sub.Signature, data.AsSpan(sub.DataOffset, sub.DataLength).ToArray()));
                }

                // FormID names resolve at display time (same as the schema-primary path), so no resolver here.
                result[record.FormId] = SchemaRecordDecoder.Decode(
                    def, subs, record.IsBigEndian, game: context.Game, formVersion: record.FormVersion);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return result;
    }
}
