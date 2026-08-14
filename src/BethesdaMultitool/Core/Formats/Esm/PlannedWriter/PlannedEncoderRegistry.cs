namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter;

/// <summary>
///     Lookup table mapping record signature → <see cref="IPlannedRecordEncoder" />.
///     The dispatch shim consults this before handing a type to the writer; an unmapped
///     requested type is an invariant error because there is no alternate emission route.
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

    /// <summary>Total registered encoder count.</summary>
    public int Count => _byType.Count;
}
