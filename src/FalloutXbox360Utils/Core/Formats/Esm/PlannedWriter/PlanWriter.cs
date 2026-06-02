using FalloutXbox360Utils.Core.Formats.Esm.Merge;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Output;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter;

/// <summary>
///     Walks an <see cref="EmitPlan" /> and produces a top-level GRUP for a requested record
///     type. Pure: never allocates, never resolves, never validates — those happen in the
///     planner. Reuses the legacy byte-emission primitives
///     (<see cref="PluginRecordByteBuilder" />, <see cref="RecordMergeEngine" />,
///     <see cref="TopLevelRecordEmitter" />) so Tier 1 parity is byte-exact.
/// </summary>
public sealed class PlanWriter
{
    /// <summary>Record header flag bit indicating the body is zlib-compressed.</summary>
    private const uint CompressedFlag = 0x00040000u;

    private readonly PlannedEncoderRegistry _encoders;

    public PlanWriter(PlannedEncoderRegistry encoders)
    {
        _encoders = encoders ?? throw new ArgumentNullException(nameof(encoders));
    }

    /// <summary>
    ///     True when this writer can handle the given record type (an encoder is registered).
    ///     The dispatch shim should only consult the writer for types it owns.
    /// </summary>
    public bool Handles(string recordType) => _encoders.Contains(recordType);

    /// <summary>
    ///     Produce the wrapped top-level GRUP bytes for one record type from the plan.
    ///     Returns an empty array when no records of the type produced emitted bytes —
    ///     matching legacy "anyEmitted=false → return []" behavior.
    /// </summary>
    public byte[] BuildGrupForType(string recordType, EmitPlan plan, PluginBuildOptions options)
    {
        if (string.IsNullOrEmpty(recordType))
        {
            throw new ArgumentException("Record type required.", nameof(recordType));
        }

        ArgumentNullException.ThrowIfNull(plan);

        if (!_encoders.Contains(recordType))
        {
            throw new InvalidOperationException(
                $"PlanWriter has no registered encoder for {recordType}; the dispatch shim should not have called it.");
        }

        var encoder = _encoders.Get(recordType);
        var policy = SubrecordMergePolicy.ForRecordType(recordType);

        using var grupBodyStream = new MemoryStream();
        var anyEmitted = false;

        foreach (var record in plan.Records)
        {
            if (record.Type != recordType)
            {
                continue;
            }

            if (record.Disposition is RecordDisposition.KeepMaster or RecordDisposition.Skip)
            {
                continue; // KeepMaster records live in the master ESM; Skip records are dropped.
            }

            if (record.Model is null)
            {
                continue; // Override / New always have a Model from the planner.
            }

            var refs = new PlanReferenceLookup(record, plan);
            var encoded = encoder.Encode(record.Model, record, refs);

            if (encoded.Subrecords.Count == 0)
            {
                continue; // Encoder declined — matches legacy "no changes → skip override" path.
            }

            var recordBytes = record.Disposition switch
            {
                RecordDisposition.New => BuildNewRecord(recordType, record.FormId, encoded.Subrecords, options),
                RecordDisposition.Override => BuildOverrideRecord(record, encoded, policy, options),
                _ => throw new InvalidOperationException($"Unexpected disposition {record.Disposition}."),
            };

            grupBodyStream.Write(recordBytes);
            anyEmitted = true;
        }

        return anyEmitted
            ? TopLevelRecordEmitter.WrapInTopLevelGrup(recordType, grupBodyStream.ToArray())
            : [];
    }

    private static byte[] BuildNewRecord(
        string recordType,
        uint formId,
        IReadOnlyList<EncodedSubrecord> subrecords,
        PluginBuildOptions options)
    {
        var flags = options.CompressRecords ? CompressedFlag : 0u;
        return PluginRecordByteBuilder.BuildNewRecordBytes(recordType, formId, flags, subrecords);
    }

    private static byte[] BuildOverrideRecord(
        RecordPlan record,
        EncodedRecord encoded,
        SubrecordMergePolicy policy,
        PluginBuildOptions options)
    {
        if (record.Master is null)
        {
            throw new InvalidOperationException(
                $"Override disposition for {record.Type} 0x{record.FormId:X8} has no master record — planner contract violated.");
        }

        var merge = RecordMergeEngine.Merge(record.Master, encoded, policy);
        return PluginRecordByteBuilder.BuildOverrideRecordBytes(record.Master, merge.SubrecordBytes, options);
    }
}
