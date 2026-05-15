using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.CLI.Commands.Dmp;

/// <summary>
///     Learns per-worldspace authored CELL FormID ranges and resolves ambiguous exterior cells
///     when exactly one observed range contains the cell FormID.
/// </summary>
internal sealed class WorldspaceFormIdRangeIndex
{
    private const uint VirtualCellFormIdFloor = 0xFE000000u;

    private readonly Dictionary<uint, (uint Min, uint Max)> _ranges = [];

    public WorldspaceFormIdRangeIndex()
    {
    }

    public WorldspaceFormIdRangeIndex(IReadOnlyDictionary<uint, (uint Min, uint Max)> ranges)
    {
        foreach (var (worldspaceFormId, range) in ranges)
        {
            _ranges[worldspaceFormId] = range;
        }
    }

    public IReadOnlyDictionary<uint, (uint Min, uint Max)> Ranges => _ranges;

    public void ObserveCell(CellRecord cell, IReadOnlyDictionary<uint, uint> runtimeCellOwner)
    {
        if (cell.IsInterior || !IsAuthoredCellFormId(cell.FormId))
        {
            return;
        }

        uint? worldspaceFormId = cell.WorldspaceFormId is { } esmWs && esmWs != 0u
            ? esmWs
            : null;
        if (worldspaceFormId is null
            && runtimeCellOwner.TryGetValue(cell.FormId, out var runtimeWs))
        {
            worldspaceFormId = runtimeWs;
        }

        if (worldspaceFormId is { } ws && ws != 0u)
        {
            AddObservedCell(ws, cell.FormId);
        }
    }

    public void AddObservedCell(uint worldspaceFormId, uint cellFormId)
    {
        if (worldspaceFormId == 0 || !IsAuthoredCellFormId(cellFormId))
        {
            return;
        }

        if (!_ranges.TryGetValue(worldspaceFormId, out var range))
        {
            _ranges[worldspaceFormId] = (cellFormId, cellFormId);
            return;
        }

        _ranges[worldspaceFormId] = (Math.Min(range.Min, cellFormId), Math.Max(range.Max, cellFormId));
    }

    public uint? ResolveUniqueOwner(CellRecord cell)
    {
        var rangeOwner = 0u;
        var rangeMatches = 0;
        var candidates = cell.CandidateWorldspaceFormIds;
        if (candidates.Count > 0)
        {
            foreach (var wsFid in candidates)
            {
                if (IsInRange(wsFid, cell.FormId))
                {
                    rangeOwner = wsFid;
                    if (++rangeMatches > 1)
                    {
                        break;
                    }
                }
            }
        }
        else
        {
            foreach (var (wsFid, range) in _ranges)
            {
                if (cell.FormId >= range.Min && cell.FormId <= range.Max)
                {
                    rangeOwner = wsFid;
                    if (++rangeMatches > 1)
                    {
                        break;
                    }
                }
            }
        }

        return rangeMatches == 1 ? rangeOwner : null;
    }

    private bool IsInRange(uint worldspaceFormId, uint cellFormId)
    {
        return _ranges.TryGetValue(worldspaceFormId, out var range)
               && cellFormId >= range.Min
               && cellFormId <= range.Max;
    }

    private static bool IsAuthoredCellFormId(uint formId)
    {
        return formId < VirtualCellFormIdFloor;
    }
}
