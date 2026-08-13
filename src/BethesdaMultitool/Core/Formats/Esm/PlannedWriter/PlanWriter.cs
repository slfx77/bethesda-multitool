using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter;

/// <summary>
///     Walks an <see cref="EmitPlan" /> and produces a top-level GRUP for a requested record
///     type. It never allocates or chooses a reference target — those decisions happen in
///     the planner — but it applies the plan's settled source-to-emitted aliases to encoded
///     FormID fields before serialization. Reuses the legacy byte-emission primitives
///     (<see cref="PluginRecordByteBuilder" />, <see cref="RecordMergeEngine" />,
///     <see cref="TopLevelRecordEmitter" />) so Tier 1 parity is byte-exact.
/// </summary>
public sealed class PlanWriter
{
    /// <summary>Record header flag bit indicating the body is zlib-compressed.</summary>
    private const uint CompressedFlag = 0x00040000u;

    private readonly PlannedEncoderRegistry _encoders;
    private readonly IConversionProgressSink _sink;
    private readonly ConversionPipelineStats? _stats;
    private readonly HashSet<QuestVariableProducerOwner> _emittedProducerOwners = [];

    public PlanWriter(
        PlannedEncoderRegistry encoders,
        IConversionProgressSink? sink = null,
        ConversionPipelineStats? stats = null)
    {
        _encoders = encoders ?? throw new ArgumentNullException(nameof(encoders));
        _sink = sink ?? NullConversionProgressSink.Instance;
        _stats = stats;
    }

    /// <summary>
    ///     True when this writer can handle the given record type (an encoder is registered).
    ///     The dispatch shim should only consult the writer for types it owns.
    /// </summary>
    public bool Handles(string recordType) => _encoders.Contains(recordType);

    /// <summary>
    ///     Exact model-owned script paths whose enclosing records have reached the output
    ///     byte stream. A planned record is intentionally absent when its encoder declines
    ///     late (for example, after PACK inline-script sanitation).
    /// </summary>
    internal PlanProducerEmissionLedger ProducerLedger => new(
        _emittedProducerOwners
            .OrderBy(static owner => owner.RecordType, StringComparer.Ordinal)
            .ThenBy(static owner => owner.SourceFormId)
            .ThenBy(static owner => owner.ScriptPath, StringComparer.Ordinal)
            .ToImmutableArray());

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
                // KeepMaster records live in the master ESM; Skip records are dropped.
                _stats?.IncrementSkipped(recordType);
                _stats?.IncrementDropReason(record.Disposition == RecordDisposition.KeepMaster
                    ? "plan.keep-master"
                    : "plan.skip");
                continue;
            }

            // Append-only master SCPT locals are a settled planner directive, and may
            // target a master-only catalog entry whose semantic DMP Model is null. Execute
            // this before the generic model-null guard. The dedicated encoder preserves
            // master order and inserts new locals before SCRO/SCRV; generic positional
            // merging cannot safely express that insertion.
            if (record.Type == "SCPT" && !record.ScriptVariableAugmentations.IsEmpty)
            {
                if (record.Disposition != RecordDisposition.Override || record.Master is null)
                {
                    throw new InvalidOperationException(
                        $"SCPT variable augmentation for 0x{record.FormId:X8} requires a master override.");
                }

                var subrecordBytes = Encoders.ComplexRef.MasterScriptVariableAugmentationEncoder
                    .EncodeSubrecordStream(record);
                var augmentedRecord = PluginRecordByteBuilder.BuildOverrideRecordBytes(
                    record.Master, subrecordBytes, options);
                grupBodyStream.Write(augmentedRecord);
                ScriptEmissionProvenanceReporter.ReportAugmentedMasterScript(
                    _sink,
                    record.Master,
                    EsmParser.ParseSubrecords(subrecordBytes),
                    record.ScriptVariableAugmentations);
                anyEmitted = true;
                continue;
            }

            if (record.Model is null)
            {
                continue; // Override / New always have a Model from the planner.
            }

            var refs = new PlanReferenceLookup(record, plan);
            var encoded = encoder.Encode(record.Model, record, refs);
            foreach (var warning in encoded.Warnings)
            {
                _sink.Warn(
                    "Writing planned records",
                    warning,
                    recordType,
                    record.SourceFormId ?? record.FormId,
                    "planned-encoder.warning");
                if (_stats is { } warnStats)
                {
                    warnStats.Warnings++;
                }
            }

            encoded = encoded with
            {
                Subrecords = EncodedSubrecordFormIdRemapper.Remap(
                    recordType, encoded.Subrecords, plan.SourceToEmittedFormId)
            };
            if (plan.Meta.PlannerCoverage.Contains("SCPT"))
            {
                encoded = encoded with
                {
                    Subrecords = EncodedScriptBindingSanitizer.DropInvalidScri(
                        encoded.Subrecords, plan.ValidScriptFormIds)
                };
            }

            if (encoded.Subrecords.Count == 0)
            {
                if (recordType == "SCPT" && record.Disposition == RecordDisposition.New)
                {
                    throw new InvalidOperationException(
                        $"Planned new SCPT 0x{record.FormId:X8} produced no bytes; "
                        + "the planner/writer script-emission policies have diverged.");
                }

                // Encoder declined — matches legacy "no changes → skip override" path. This
                // is the DelegatingPlannedEncoder empty-Override convention for ~49 Simple<T>
                // types, so it is the single largest skip population under planner emission.
                _stats?.IncrementSkipped(recordType);
                _stats?.IncrementDropReason("planned-encoder.declined");
                continue;
            }

            var recordBytes = record.Disposition switch
            {
                RecordDisposition.New => BuildNewRecord(recordType, record.FormId, encoded.Subrecords, options),
                RecordDisposition.Override => BuildOverrideRecord(record, encoded, policy, options),
                _ => throw new InvalidOperationException($"Unexpected disposition {record.Disposition}."),
            };

            grupBodyStream.Write(recordBytes);
            // NOTE: emitted-record accounting is NOT done here. Records written into a GRUP
            // can still be discarded downstream (cell gates clear whole buckets after their
            // children encode), so the only trustworthy census is over the assembled file —
            // see PluginEmissionCensus, applied once in EsmAssembler.
            // Producer discovery deliberately excludes master-anchored records. For an
            // override, merge policy can retain the master's script bytes instead of the
            // encoded block, so encoder metadata alone cannot prove what survived. Fail
            // closed and promote only New records, whose serialized body is exactly the
            // remapped encoded stream above.
            if (record.Disposition == RecordDisposition.New
                && record.SourceFormId is { } sourceFormId)
            {
                foreach (var scriptPath in encoded.EmittedScriptPaths)
                {
                    _emittedProducerOwners.Add(new QuestVariableProducerOwner(
                        record.Type,
                        sourceFormId,
                        scriptPath));
                }
            }

            if (recordType == "SCPT"
                && record.Disposition == RecordDisposition.New
                && record.Model is ScriptRecord script)
            {
                ScriptEmissionProvenanceReporter.ReportNewScript(
                    _sink,
                    script,
                    record.FormId,
                    encoded.Subrecords);
            }

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

        if (record.RetainMasterSubrecordSignatures.Count > 0)
        {
            foreach (var signature in record.RetainMasterSubrecordSignatures)
            {
                if (!encoded.Subrecords.Any(subrecord =>
                        string.Equals(subrecord.Signature, signature, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"Diagnostic retention requested {signature} on {record.Type} 0x{record.FormId:X8}, " +
                        "but the DMP encoder produced no such subrecord.");
                }
            }

            policy = policy.WithAdditionalMasterRetention(record.RetainMasterSubrecordSignatures);
        }

        var merge = RecordMergeEngine.Merge(record.Master, encoded, policy);
        var subrecordBytes = merge.SubrecordBytes;

        // Master actor overrides keep MASTER's ACBS Flags + TemplateFlags (identity
        // policy): runtime captures leak state bits that re-point scripts/leveling
        // — the Omerta entrance guard's forcegreet died to a leaked UseScript template flag.
        if (record.Type is "NPC_" or "CREA")
        {
            subrecordBytes = Plugin.Writers.Encoders.Character.ActorBaseAcbsBuilder
                .RestoreMasterIdentityFlags(subrecordBytes, record.Master);
        }

        return PluginRecordByteBuilder.BuildOverrideRecordBytes(record.Master, subrecordBytes, options);
    }
}
