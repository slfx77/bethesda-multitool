using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Builds a shared lookup for worldspace FormIDs to readable names used by reports and exports.
/// </summary>
internal static class WorldspaceNameIndex
{
    public static IReadOnlyDictionary<uint, string> Build(
        RecordCollection records,
        IReadOnlyDictionary<uint, string>? formIdMap)
    {
        var names = new Dictionary<uint, string>();
        foreach (var worldspace in records.Worldspaces)
        {
            if (worldspace.FormId != 0 && !string.IsNullOrWhiteSpace(worldspace.EditorId))
            {
                names[worldspace.FormId] = worldspace.EditorId;
            }
        }

        if (formIdMap == null)
        {
            return names;
        }

        foreach (var (formId, name) in formIdMap)
        {
            if (formId != 0 && !string.IsNullOrWhiteSpace(name))
            {
                names.TryAdd(formId, name);
            }
        }

        return names;
    }
}
