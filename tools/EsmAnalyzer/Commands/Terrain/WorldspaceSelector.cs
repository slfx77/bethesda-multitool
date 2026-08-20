using Spectre.Console;

namespace EsmAnalyzer.Commands.Terrain;

/// <summary>
///     Resolves a <c>-w/--worldspace</c> argument against the worldspaces the loaded FILE actually
///     contains, instead of a hardcoded per-game FormID table.
///     <para>
///         The previous table (<c>FalloutWorldspaces</c>, deleted 2026-08-11) was documented "Known
///         worldspace FormIDs for Fallout: New Vegas" and aliased <c>"Wasteland"</c> to FNV's
///         <c>WastelandNV</c> 0x000DA726 — but Fallout 3's exterior worldspace is literally named
///         <c>Wasteland</c> with FormID 0x0000003C, so <c>-w Wasteland</c> against Fallout3.esm
///         resolved to an ID that file does not contain. The consumers used the value only as a
///         FormID equality filter, so the miss surfaced as "Found 0 CELL records" (or, worse,
///         silently disabled bounds filtering) and blamed the terrain data rather than the lookup.
///         Resolving against the file's own WRLD EditorIDs cannot drift from the data, works for
///         every game, and survives DLC/mod load-order changes that baked mod-index FormIDs did not.
///     </para>
/// </summary>
internal static class WorldspaceSelector
{
    /// <summary>
    ///     Names tried, in order, when the caller supplies no worldspace: Fallout: New Vegas's Mojave
    ///     then Fallout 3's Capital Wasteland. Names, never FormIDs — each is matched against the
    ///     file's own records, so the first one present wins and a file with neither fails loudly.
    /// </summary>
    private static readonly string[] DefaultNamePreference = ["WastelandNV", "Wasteland"];

    /// <summary>
    ///     Resolves <paramref name="nameOrFormId" /> (a WRLD EditorID, a FormID like <c>0x0000003C</c>,
    ///     or null/empty for the default) against the worldspaces present in <paramref name="data" />.
    ///     Prints a diagnostic listing every available worldspace on failure and returns false.
    /// </summary>
    internal static bool TryResolve(
        byte[] data,
        bool bigEndian,
        string? nameOrFormId,
        out string worldspaceName,
        out uint worldspaceFormId)
    {
        worldspaceName = string.Empty;
        worldspaceFormId = 0;

        var available = WrldGrupScanner.FindAllWorldspaces(data, bigEndian);
        if (available.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] no WRLD (worldspace) records found in this file.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(nameOrFormId))
        {
            foreach (var preferred in DefaultNamePreference)
            {
                if (TryMatchByName(available, preferred, out worldspaceName, out worldspaceFormId))
                {
                    return true;
                }
            }

            AnsiConsole.MarkupLine(
                "[red]ERROR:[/] no worldspace specified and none of the default names " +
                $"({string.Join(", ", DefaultNamePreference)}) exist in this file.");
            PrintAvailable(available);
            return false;
        }

        if (TryMatchByName(available, nameOrFormId, out worldspaceName, out worldspaceFormId))
        {
            return true;
        }

        // A FormID is only accepted when the file actually contains it — otherwise the caller would
        // filter for a record that cannot match and report the emptiness as a terrain problem.
        if (EsmFileLoader.ParseFormId(nameOrFormId) is { } parsed)
        {
            foreach (var (name, formId) in available)
            {
                if (formId != parsed) continue;
                worldspaceName = name;
                worldspaceFormId = formId;
                return true;
            }

            AnsiConsole.MarkupLine(
                $"[red]ERROR:[/] worldspace 0x{parsed:X8} is not present in this file.");
            PrintAvailable(available);
            return false;
        }

        AnsiConsole.MarkupLine(
            $"[red]ERROR:[/] unknown worldspace '{Markup.Escape(nameOrFormId)}' " +
            "(expected a WRLD EditorID or a FormID).");
        PrintAvailable(available);
        return false;
    }

    private static bool TryMatchByName(
        List<(string name, uint formId)> available,
        string wanted,
        out string worldspaceName,
        out uint worldspaceFormId)
    {
        foreach (var (name, formId) in available)
        {
            if (!string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) continue;
            worldspaceName = name;
            worldspaceFormId = formId;
            return true;
        }

        worldspaceName = string.Empty;
        worldspaceFormId = 0;
        return false;
    }

    private static void PrintAvailable(List<(string name, uint formId)> available)
    {
        AnsiConsole.MarkupLine($"[yellow]Worldspaces in this file ({available.Count}):[/]");
        foreach (var (name, formId) in available)
        {
            AnsiConsole.MarkupLine($"  {Markup.Escape(name)}: 0x{formId:X8}");
        }
    }
}