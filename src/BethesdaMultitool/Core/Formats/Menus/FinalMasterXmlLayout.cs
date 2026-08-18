namespace BethesdaMultitool.Core.Formats.Menus;

/// <summary>
///     Maps the flat document names inside <c>final_master_xml.dat</c> onto the subfoldered
///     <c>menus\</c> paths the PC engine opens.
///     <para>
///         The PC engine hard-codes each path (<c>Data\Menus\Main\hud_main_menu.xml</c>,
///         <c>Data\Menus\CharGen\race_sex_menu.xml</c>, …), so a flat dump into <c>menus\</c>
///         leaves most of them unfindable. Every mapping below was taken from the vanilla PC
///         <c>Fallout - Misc.bsa</c> layout and cross-checked against the <c>Data\Menus\…</c>
///         string literals in retail <c>FalloutNV.exe</c>; all 41 console documents resolve to a
///         unique vanilla path by basename.
///     </para>
/// </summary>
public static class FinalMasterXmlLayout
{
    /// <summary>
    ///     Console document basename (no extension, lowercase) → subfolder beneath <c>menus\</c>.
    ///     Anything absent sits directly in <c>menus\</c>.
    /// </summary>
    private static readonly Dictionary<string, string> Subfolders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["char_gen_menu"] = "chargen",
            ["love_tester_menu"] = "chargen",
            ["race_sex_menu"] = "chargen",
            ["specialbookmenu"] = "chargen",
            ["dialog_menu"] = "dialog",
            ["texteditmenu"] = "dialog",
            ["skill_perk"] = "generic",
            ["hud_main_menu"] = "main",
            ["inventory_menu"] = "main",
            ["map_menu"] = "main",
            ["quickkeys_menu"] = "main",
            ["safe_zone"] = "main",
            ["stats_menu"] = "main",
            ["credits_menu"] = "options",
            ["main_menu"] = "options",
            ["pause_menu"] = "options",
            ["start_menu"] = "options"
        };

    /// <summary>
    ///     Menu classes vanilla PC declares that the 360 container does not carry. The console
    ///     build has no counterpart for these — save/load go through the Xbox storage UI, and the
    ///     rest are simply absent — so a conversion built only from <c>final_master_xml.dat</c>
    ///     leaves the PC engine with no document to open when one of them is requested. These are
    ///     what the PC donor backfill exists to supply.
    /// </summary>
    public static readonly IReadOnlyList<string> MenusAbsentFromConsoleBuild =
    [
        "menus\\book_menu.xml",
        "menus\\breath_meter_menu.xml",
        "menus\\generic\\quest_added.xml",
        "menus\\generic\\test_menu.xml",
        "menus\\options\\load_menu.xml",
        "menus\\options\\save_menu.xml",
        "menus\\reputation_menu.xml",
        "menus\\surgerymenu.xml",
        "menus\\trait_select_menu.xml"
    ];

    /// <summary>
    ///     The console's own interface dictionary, which its <c>Fallout - Misc.bsa</c> does ship.
    ///     Excluded from the PC backfill so the converted build keeps the console copy.
    /// </summary>
    public const string ConsoleSuppliedDictionary = "menus\\falloutdict.txt";

    /// <summary>
    ///     True when a donor archive entry belongs to the interface tree and should be considered
    ///     for backfill. Console-supplied documents are filtered separately, by resolved path.
    /// </summary>
    public static bool IsBackfillCandidate(string archivePath)
    {
        ArgumentNullException.ThrowIfNull(archivePath);

        return archivePath.StartsWith("menus\\", StringComparison.OrdinalIgnoreCase)
               && !archivePath.Equals(ConsoleSuppliedDictionary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Resolves a console document name to its archive-relative PC path, e.g.
    ///     <c>hud_main_menu.xml</c> → <c>menus\main\hud_main_menu.xml</c>.
    /// </summary>
    public static string ToPcPath(string consoleName)
    {
        ArgumentException.ThrowIfNullOrEmpty(consoleName);

        var fileName = Path.GetFileName(consoleName);
        var stem = Path.GetFileNameWithoutExtension(fileName);

        return Subfolders.TryGetValue(stem, out var subfolder)
            ? Path.Combine("menus", subfolder, fileName)
            : Path.Combine("menus", fileName);
    }
}
