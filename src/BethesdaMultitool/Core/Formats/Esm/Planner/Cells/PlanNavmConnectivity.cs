using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Nav;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Each written navmesh's cross-navmesh connectivity — NVEX edge-link targets and NVDP
///     door portals — computed from the plan, for the NAVI record's NVCI arrays.
///     <para>
///     This is the same answer <c>NavmConnectivityExtractor</c> reads back out of the finished
///     NAVM bytes, derived instead from the captured subrecords plus the plan's remaps and
///     valid sets. Doing it here is what lets NAVI stop trailing cell emission: NVCI and the
///     navmesh's own links must describe one graph, and the engine walks NVCI during
///     cross-cell A* setup, so a portal kept in one and dropped from the other is a crash.
///     </para>
///     <para>
///     The rules mirror the writer byte-for-byte: NVEX targets are remapped where the plan has
///     an entry and then kept only when null or pointing at a live navmesh; NVDP door refs are
///     remapped through the door-clone map and kept only when they land on a live door. NVCI
///     itself lists each surviving non-null target once, in first-seen order.
///     </para>
/// </summary>
internal static class PlanNavmConnectivity
{
    private const int NvexEntrySize = 10;
    private const int NvexTargetOffset = 4; // Type(u32) Navmesh(FormId @4) Triangle(u16)
    private const int NvdpEntrySize = 8;

    public static ImmutableDictionary<uint, NavmConnectivity> Compute(
        ImmutableDictionary<uint, CellPlan> cells,
        IReadOnlySet<uint> emittedNavmFormIds,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId,
        IReadOnlyDictionary<uint, uint> sourceToEmitted,
        NavmDoorLinkPlan doorLinks,
        IReadOnlySet<uint> emittedFormIds)
    {
        if (emittedNavmFormIds.Count == 0)
        {
            return ImmutableDictionary<uint, NavmConnectivity>.Empty;
        }

        // Valid NVEX targets = navmeshes actually written ∪ master's own.
        var validTargets = new HashSet<uint>(emittedNavmFormIds);
        // Valid NVCI door refs = anything that resolves at all (emitted ∪ master).
        var validRefFormIds = new HashSet<uint>(emittedFormIds);
        foreach (var (formId, record) in masterByFormId)
        {
            validRefFormIds.Add(formId);
            if (string.Equals(record.Header.Signature, "NAVM", StringComparison.Ordinal))
            {
                validTargets.Add(formId);
            }
        }

        var result = ImmutableDictionary.CreateBuilder<uint, NavmConnectivity>();
        foreach (var cell in cells.Values)
        {
            foreach (var child in cell.TemporaryChildren)
            {
                if (child.Type != "NAVM"
                    || child.Model is not NavMeshRecord navm
                    || !emittedNavmFormIds.Contains(child.FormId))
                {
                    continue;
                }

                result[child.FormId] = Connectivity(
                    navm, sourceToEmitted, doorLinks, validTargets, validRefFormIds);
            }
        }

        return result.ToImmutable();
    }

    private static NavmConnectivity Connectivity(
        NavMeshRecord navm,
        IReadOnlyDictionary<uint, uint> sourceToEmitted,
        NavmDoorLinkPlan doorLinks,
        HashSet<uint> validTargets,
        HashSet<uint> validRefFormIds)
    {
        var standard = new List<uint>();
        var standardSeen = new HashSet<uint>();
        var doors = new List<uint>();
        var doorsSeen = new HashSet<uint>();

        foreach (var sub in navm.RawSubrecords)
        {
            if (sub.Signature == "NVEX")
            {
                for (var k = 0; k + NvexEntrySize <= sub.Bytes.Length; k += NvexEntrySize)
                {
                    var target = Remap(
                        BinaryPrimitives.ReadUInt32LittleEndian(
                            sub.Bytes.AsSpan(k + NvexTargetOffset, 4)),
                        sourceToEmitted);

                    // A null target survives sanitation but is not a neighbor.
                    if (target == 0 || !validTargets.Contains(target))
                    {
                        continue;
                    }

                    if (standardSeen.Add(target))
                    {
                        standard.Add(target);
                    }
                }
            }
            else if (sub.Signature == "NVDP" && sub.Bytes.Length >= NvdpEntrySize)
            {
                for (var k = 0; k + NvdpEntrySize <= sub.Bytes.Length; k += NvdpEntrySize)
                {
                    var door = Remap(
                        BinaryPrimitives.ReadUInt32LittleEndian(sub.Bytes.AsSpan(k, 4)),
                        doorLinks.SourceToEmittedDoorRef);

                    if (door == 0
                        || !doorLinks.ValidDoorRefFormIds.Contains(door)
                        || !validRefFormIds.Contains(door))
                    {
                        continue;
                    }

                    if (doorsSeen.Add(door))
                    {
                        doors.Add(door);
                    }
                }
            }
        }

        return new NavmConnectivity(standard, doors);
    }

    private static uint Remap(uint formId, IReadOnlyDictionary<uint, uint> rewrites) =>
        rewrites.TryGetValue(formId, out var replacement) ? replacement : formId;
}
