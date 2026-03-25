using System.Buffers;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

/// <summary>
///     Base class for ESM record parsing handlers.
///     Provides shared access to the parsing context.
/// </summary>
internal abstract class RecordHandlerBase(RecordParserContext context)
{
    protected readonly RecordParserContext Context = context;

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
        var records = Context.GetRecordsByType(recordType).ToList();
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

        var records = Context.GetRecordsByType(recordType);
        var results = new List<T>();
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
