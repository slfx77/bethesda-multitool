namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter;

/// <summary>
///     Lookup table mapping record signature → <see cref="IPlannedRecordEncoder" />.
///     The dispatch shim consults this when handing records to the writer; an unmapped
///     type means the writer cannot service the request and the caller should fall back
///     to the legacy pipeline (until that path is deleted in the final bulk-removal PR).
/// </summary>
public sealed class PlannedEncoderRegistry
{
    private readonly Dictionary<string, IPlannedRecordEncoder> _byType =
        new(StringComparer.Ordinal);

    public PlannedEncoderRegistry(IEnumerable<IPlannedRecordEncoder> encoders)
    {
        ArgumentNullException.ThrowIfNull(encoders);

        foreach (var encoder in encoders)
        {
            if (_byType.ContainsKey(encoder.RecordType))
            {
                throw new InvalidOperationException(
                    $"Duplicate planned encoder registered for {encoder.RecordType}.");
            }

            _byType[encoder.RecordType] = encoder;
        }
    }

    /// <summary>Returns true when an encoder is registered for the given type.</summary>
    public bool Contains(string recordType) => _byType.ContainsKey(recordType);

    /// <summary>Strongly-typed lookup. Throws on miss; pair with <see cref="Contains" />.</summary>
    public IPlannedRecordEncoder Get(string recordType) =>
        _byType.TryGetValue(recordType, out var encoder)
            ? encoder
            : throw new KeyNotFoundException(
                $"No planned encoder registered for record type {recordType}.");

    /// <summary>Total encoder count — for reporting how much of the migration is done.</summary>
    public int Count => _byType.Count;
}
