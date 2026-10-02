namespace BethesdaMultitool.Core.Semantic.LoadOrder;

/// <summary>Places the opened plugin among an explicitly ordered supplementary list without reordering that list.</summary>
internal static class PrimaryPluginOrder
{
    internal static PluginLoadOrder Create(string primaryPath, IReadOnlyList<string> supplementary,
        Func<string, IReadOnlyList<string>> readMasters)
    {
        var paths = supplementary.Where(path => !Path.GetFullPath(path).Equals(Path.GetFullPath(primaryPath),
            StringComparison.OrdinalIgnoreCase)).ToList();
        var masters = paths.Append(primaryPath).ToDictionary(Path.GetFullPath, readMasters, StringComparer.OrdinalIgnoreCase);
        ArgumentException? failure = null;
        for (var position = 0; position <= paths.Count; position++)
        {
            var candidate = paths.ToList();
            candidate.Insert(position, primaryPath);
            try { return PluginLoadOrder.Create(candidate, true, path => masters[Path.GetFullPath(path)]); }
            catch (ArgumentException ex) { failure = ex; }
        }
        throw new ArgumentException("The selected plugins are not in dependency order. " + failure?.Message, failure);
    }
}
