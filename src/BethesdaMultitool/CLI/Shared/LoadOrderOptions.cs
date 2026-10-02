using System.CommandLine;

namespace BethesdaMultitool.CLI.Shared;

internal static class LoadOrderOptions
{
    internal static Option<string[]> CreateOption() => new("--load-order")
    {
        Description = "Explicit ordered Fallout 3/New Vegas plugins, separated by ';' or repeated. Bare filenames resolve beside the input; the input is appended if absent. Bare FormIDs then use load-order slots; Plugin.esm:0xID uses file-local slots.",
        AllowMultipleArgumentsPerToken = true,
        Arity = ArgumentArity.OneOrMore
    };

    internal static Option<bool> CreateAllowMissingMastersOption() => new("--allow-missing-masters")
    {
        Description = "Reserve distinct slots for missing masters and label them unresolved. Does not infer absent content."
    };

    internal static IReadOnlyList<string> ResolvePaths(string focusPath, string[] specs)
    {
        var focus = Path.GetFullPath(focusPath);
        var directory = Path.GetDirectoryName(focus)!;
        var paths = specs.SelectMany(s => s.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Select(s => Path.GetFullPath(Path.IsPathRooted(s) ? s : Path.Combine(directory, s))).ToList();
        if (!paths.Contains(focus, StringComparer.OrdinalIgnoreCase)) { paths.Add(focus); }
        return paths;
    }
}
