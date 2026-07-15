using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Planner-owned NVDP policy. Identifies every live DOOR-base REFR and builds the
///     deliberately narrow source-door → cloned-door rewrite map used by NAVM emission.
///     This map is separate from the global source map: a cloned one-way Strip door must
///     not retarget unrelated XTEL references that correctly point at the retail door.
/// </summary>
public sealed record NavmDoorLinkPlan
{
    public static NavmDoorLinkPlan Empty { get; } = new();

    public ImmutableDictionary<uint, uint> SourceToEmittedDoorRef { get; init; } =
        ImmutableDictionary<uint, uint>.Empty;

    public ImmutableHashSet<uint> ValidDoorRefFormIds { get; init; } =
        ImmutableHashSet<uint>.Empty;
}

public static class NavmDoorLinkPlanner
{
    public static NavmDoorLinkPlan Build(
        EmitPlan plan,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(masterByFormId);

        var valid = ImmutableHashSet.CreateBuilder<uint>();
        var rewrites = ImmutableDictionary.CreateBuilder<uint, uint>();

        // Master door placements remain live even when the plugin emits no override.
        foreach (var (formId, record) in masterByFormId)
        {
            if (record.Header.Signature == "REFR"
                && TryReadBaseFormId(record, out var baseFormId)
                && IsDoorBase(baseFormId, plan, masterByFormId))
            {
                valid.Add(formId);
            }
        }

        foreach (var cell in plan.CellsByFormId.Values)
        {
            if (cell.Emits == false)
            {
                continue;
            }

            AddBucket(cell.PersistentChildren, cell, plan, masterByFormId, valid, rewrites);
            AddBucket(cell.VwdChildren, cell, plan, masterByFormId, valid, rewrites);
            AddBucket(cell.TemporaryChildren, cell, plan, masterByFormId, valid, rewrites);
        }

        return new NavmDoorLinkPlan
        {
            SourceToEmittedDoorRef = rewrites.ToImmutable(),
            ValidDoorRefFormIds = valid.ToImmutable(),
        };
    }

    private static void AddBucket(
        IReadOnlyList<RecordPlan> children,
        CellPlan cell,
        EmitPlan plan,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId,
        ImmutableHashSet<uint>.Builder valid,
        ImmutableDictionary<uint, uint>.Builder rewrites)
    {
        foreach (var child in children)
        {
            if (child.Type != "REFR"
                || child.Disposition is RecordDisposition.Skip or RecordDisposition.KeepMaster)
            {
                continue;
            }

            if (cell.RefDecisions.TryGetValue(child.FormId, out var decision)
                && decision.Verdict == PlacedRefEmitVerdict.Drop)
            {
                continue;
            }

            var baseFormId = decision?.FinalBaseFormId ?? 0;
            if (baseFormId == 0 && child.Model is PlacedReference placed)
            {
                baseFormId = plan.SourceToEmittedFormId.TryGetValue(placed.BaseFormId, out var remapped)
                    ? remapped
                    : placed.BaseFormId;
            }

            if (!IsDoorBase(baseFormId, plan, masterByFormId))
            {
                continue;
            }

            valid.Add(child.FormId);
            if (child.SourceFormId is not { } source || source == 0 || source == child.FormId)
            {
                continue;
            }

            if (rewrites.TryGetValue(source, out var existing) && existing != child.FormId)
            {
                throw new InvalidOperationException(
                    $"Door REFR 0x{source:X8} planned more than once "
                    + $"(0x{existing:X8}, 0x{child.FormId:X8}); NVDP remap would be ambiguous.");
            }

            rewrites[source] = child.FormId;
        }
    }

    private static bool IsDoorBase(
        uint formId,
        EmitPlan plan,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId)
    {
        if (formId == 0)
        {
            return false;
        }

        if (masterByFormId.TryGetValue(formId, out var master))
        {
            return master.Header.Signature == "DOOR";
        }

        return plan.RecordIndexByEmittedFormId.TryGetValue(formId, out var index)
               && index >= 0
               && index < plan.Records.Length
               && plan.Records[index].Type == "DOOR";
    }

    private static bool TryReadBaseFormId(ParsedMainRecord record, out uint baseFormId)
    {
        foreach (var subrecord in record.Subrecords)
        {
            if (subrecord.Signature == "NAME" && subrecord.Data.Length >= 4)
            {
                baseFormId = BinaryPrimitives.ReadUInt32LittleEndian(subrecord.Data.AsSpan(0, 4));
                return true;
            }
        }

        baseFormId = 0;
        return false;
    }
}
