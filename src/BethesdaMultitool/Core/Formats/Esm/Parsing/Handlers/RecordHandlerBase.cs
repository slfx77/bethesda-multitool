using System.Buffers;
using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Base class for ESM record parsing handlers.
///     Provides shared access to the parsing context.
/// </summary>
internal abstract class RecordHandlerBase(RecordParserContext context)
{
    protected readonly RecordParserContext Context = context;

    /// <summary>
    ///     Records a subrecord this handler iterated over but did not model (its <c>switch</c> fell through
    ///     to <c>default:</c>) to <see cref="UnmodeledSubrecordLog" />, so the drop is visible to the
    ///     completeness tooling instead of silent. No-op unless <see cref="UnmodeledSubrecordLog.Enabled" />.
    /// </summary>
    protected static void NoteUnmodeledSubrecord(string recordType, string signature, int dataLength)
    {
        UnmodeledSubrecordLog.Note(recordType, signature, dataLength);
    }

    /// <summary>
    ///     Common parse loop: get records by type, rent buffer, iterate, parse each record.
    ///     When <see cref="RecordParserContext.Accessor" /> is null, uses the scan-only path.
    /// </summary>
    protected List<T> ParseRecordList<T>(
        string recordType,
        int bufferSize,
        Func<DetectedMainRecord, byte[], T?> parseFromAccessor,
        Func<DetectedMainRecord, T?> parseFromScanOnly) where T : class
    {
        var records = Context.GetRecordListByType(recordType);
        var results = new List<T>(records.Count);

        if (Context.Accessor == null)
        {
            foreach (var record in records)
            {
                var item = parseFromScanOnly(record);
                if (item != null)
                {
                    results.Add(item);
                }
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            try
            {
                foreach (var record in records)
                {
                    var item = parseFromAccessor(record, buffer);
                    if (item != null)
                    {
                        results.Add(item);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return results;
    }

    /// <summary>
    ///     Parse loop for accessor-only records (returns empty list when accessor is null).
    /// </summary>
    protected List<T> ParseAccessorOnly<T>(
        string recordType,
        int bufferSize,
        Func<DetectedMainRecord, byte[], T?> parseFromAccessor) where T : class
    {
        if (Context.Accessor == null)
        {
            return [];
        }

        var records = Context.GetRecordListByType(recordType);
        var results = new List<T>(records.Count);
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            foreach (var record in records)
            {
                var item = parseFromAccessor(record, buffer);
                if (item != null)
                {
                    results.Add(item);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return results;
    }
}
