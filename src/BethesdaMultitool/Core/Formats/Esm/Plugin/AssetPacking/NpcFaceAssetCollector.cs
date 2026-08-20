using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Collects the per-NPC FaceGen texture sidecars whose runtime path is derived from
///     the plugin filename rather than stored in an NPC_ subrecord. FNV does not fall back
///     from an overriding plugin's namespace to <c>falloutnv.esm</c> for these files, so a
///     normal "already in baseline" decision is insufficient: the source bytes must be
///     copied into the emitted plugin's namespace.
/// </summary>
internal static class NpcFaceAssetCollector
{
    public static Result Collect(
        RecordCollection records,
        IReadOnlyDictionary<uint, uint> sourceToAllocatedFormIds,
        string outputPluginFileName)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPluginFileName);

        var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var allocatedToSource = BuildReverseAllocationMap(sourceToAllocatedFormIds);
        var outputToken = Path.GetFileName(outputPluginFileName).ToLowerInvariant();

        foreach (var npc in records.Npcs)
        {
            if (npc.FormId == 0)
            {
                continue;
            }

            var sourceFormId = allocatedToSource.TryGetValue(npc.FormId, out var source)
                ? source
                : npc.FormId;
            var sourceHex = sourceFormId.ToString("x8");
            var targetHex = (npc.FormId & 0x00FFFFFFu).ToString("x8");
            var gender = npc.Stats is { Flags: var flags } && (flags & 1u) != 0
                ? "female"
                : "male";

            Add(
                $"textures\\characters\\facemods\\falloutnv.esm\\{sourceHex}_0.dds",
                $"textures\\characters\\facemods\\{outputToken}\\{targetHex}_0.dds");
            Add(
                $"textures\\characters\\bodymods\\falloutnv.esm\\{sourceHex}modbody{gender}.dds",
                $"textures\\characters\\bodymods\\{outputToken}\\{targetHex}modbody{gender}.dds");
        }

        return new Result(sourcePaths, renames);

        void Add(string sourcePath, string packPath)
        {
            sourcePaths.Add(sourcePath);
            // FormID allocation is one-to-one. First-wins remains defensive if malformed
            // input presents the same source NPC more than once.
            renames.TryAdd(sourcePath, packPath);
        }
    }

    private static Dictionary<uint, uint> BuildReverseAllocationMap(
        IReadOnlyDictionary<uint, uint> sourceToAllocatedFormIds)
    {
        var result = new Dictionary<uint, uint>();
        foreach (var (source, allocated) in sourceToAllocatedFormIds)
        {
            if (source != 0 && allocated != 0)
            {
                result.TryAdd(allocated, source);
            }
        }

        return result;
    }

    internal sealed record Result(
        IReadOnlySet<string> SourcePaths,
        IReadOnlyDictionary<string, string> PackPathRenames);
}
