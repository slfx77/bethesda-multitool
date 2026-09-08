using static SampleGenerator.SampleGeneratorConfig;
using static SampleGenerator.SampleGeneratorFileOperations;

namespace SampleGenerator;

/// <summary>
///     Regroups the non-build fixtures under <c>Sample/</c>.
///     <para>
///         They accumulated flat and mixed: crash dumps under <c>MemoryDump</c>, Xbox 360 symbol
///         trees under <c>PDB</c>, and three loose per-game directories — <c>Fallout 4</c>,
///         <c>Oblivion Remastered</c>, <c>Skyrim</c> — that read like game installs but each hold
///         nothing but an executable and its <c>.pdb</c>. This files them by what they are, so
///         <c>Sample/Builds</c> sits beside <c>MemoryDumps</c>, <c>DebugSymbols</c> and <c>Saves</c>
///         instead of among them.
///     </para>
///     <para>
///         Every move is a rename within one volume, and is skipped when the destination already
///         exists — so the operation is idempotent and re-running it after a partial run is safe.
///     </para>
/// </summary>
internal static class SampleGeneratorLayout
{
    /// <summary>
    ///     Source (relative to <c>Sample/</c>) to destination, in the order they are applied.
    ///     Debug-symbol destinations are named the way the build catalog names builds, so a symbol
    ///     set is recognisably the same title as its build directory.
    /// </summary>
    internal static readonly (string From, string To, string What)[] Moves =
    [
        (@"MemoryDump", @"MemoryDumps",
            "Xbox 360 crash dumps and the two ESMs carved from them"),

        (@"PDB", @"DebugSymbols\Fallout - New Vegas (X360)",
            "cvdump output for the Xbox 360 Debug/MemDebug/ReleaseBeta/Retail builds"),

        (@"Fallout 4", @"DebugSymbols\Fallout 4 (PC)",
            "Fallout4.exe/.pdb and the beta pair — symbols, not an install"),

        (@"Oblivion Remastered", @"DebugSymbols\The Elder Scrolls IV - Oblivion Remastered (PC)",
            "OblivionRemastered-Win64-Shipping.exe/.pdb — symbols, not an install"),

        (@"Skyrim", @"DebugSymbols\The Elder Scrolls V - Skyrim (PC)",
            "TESV.exe and TESV.map — symbols, not an install"),
    ];

    /// <summary>
    ///     Original source packages that belong to no single build, moved into <c>Sample/Media/</c>.
    ///     <c>Elder Scrolls travels game files.zip</c> is one archive spanning several Travels
    ///     titles (Shadowkey's Symbian tree and Stormhold's JAR among them), so filing it under any
    ///     one build directory would misattribute the rest.
    /// </summary>
    internal static readonly (string From, string To)[] MediaFiles =
    [
        (@"Full_Builds\Elder Scrolls travels game files.zip",
            @"Media\Elder Scrolls travels game files.zip"),
    ];

    /// <summary>
    ///     Applies the regrouping. Returns the number of moves performed; already-applied moves are
    ///     counted as skipped rather than repeated.
    /// </summary>
    internal static (int Moved, int Skipped) Apply(bool dryRun)
    {
        var moved = 0;
        var skipped = 0;

        foreach (var (from, to, what) in Moves)
        {
            var source = Path.Combine(SampleRoot, from);
            var destination = SampleGeneratorPathSafety.ResolveDestinationPath(SampleRoot, to);

            if (!Directory.Exists(source))
            {
                Console.WriteLine($"  skip  {from} -> {to} (already moved or absent)");
                skipped++;
                continue;
            }

            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            {
                Console.WriteLine($"  skip  {from} -> {to} (destination already populated)");
                skipped++;
                continue;
            }

            if (dryRun)
            {
                var (files, bytes) = MeasureTree(source);
                Console.WriteLine($"  would move  {from} -> {to}  ({files:N0} files, {bytes / (double)(1L << 30):F2} GB) — {what}");
                moved++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var (movedFiles, movedBytes) = MoveTree(source, destination);
            Console.WriteLine($"  moved {from} -> {to}  ({movedFiles:N0} files, {movedBytes / (double)(1L << 30):F2} GB)");
            moved++;
        }

        foreach (var (from, to) in MediaFiles)
        {
            var source = Path.Combine(SampleRoot, from);
            var destination = SampleGeneratorPathSafety.ResolveDestinationPath(SampleRoot, to);

            if (!File.Exists(source) || File.Exists(destination))
            {
                skipped++;
                continue;
            }

            if (dryRun)
            {
                Console.WriteLine($"  would move  {from} -> {to}");
                moved++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination);
            Console.WriteLine($"  moved {from} -> {to}");
            moved++;
        }

        if (!dryRun)
        {
            RemoveEmptyLegacyRoot(Path.Combine(SampleRoot, "Full_Builds"));
        }

        return (moved, skipped);
    }

    /// <summary>
    ///     Removes <c>Sample/Full_Builds</c> once every catalog entry has been migrated out of it.
    ///     Refuses while anything remains, so leftovers surface for review instead of being deleted.
    /// </summary>
    internal static void RemoveEmptyLegacyRoot(string legacyRoot)
    {
        if (!Directory.Exists(legacyRoot))
        {
            return;
        }

        // Migrated builds can leave an empty shell behind; an empty directory is not a leftover
        // worth reporting, so prune those before deciding whether anything real remains.
        foreach (var directory in Directory.EnumerateDirectories(legacyRoot).ToArray())
        {
            if (!Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).Any())
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        var remaining = Directory.EnumerateFileSystemEntries(legacyRoot).ToArray();
        if (remaining.Length == 0)
        {
            Directory.Delete(legacyRoot);
            Console.WriteLine("  removed empty Sample/Full_Builds");
            return;
        }

        Console.WriteLine($"  Sample/Full_Builds still holds {remaining.Length} entr" +
                          $"{(remaining.Length == 1 ? "y" : "ies")} not claimed by the catalog:");
        foreach (var entry in remaining.OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"    - {Path.GetFileName(entry)}");
        }
    }
}
