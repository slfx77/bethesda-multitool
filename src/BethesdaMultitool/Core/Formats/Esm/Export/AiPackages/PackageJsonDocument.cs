using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Export.AiPackages;

/// <summary>
///     Everything <see cref="PackageJsonWriter" /> needs for one <c>esm packages -f json</c> document.
/// </summary>
/// <param name="Source">The loaded source path, recorded verbatim.</param>
/// <param name="Game">The game the semantic loader detected for the source.</param>
/// <param name="ToolVersion">The producing tool's version, recorded verbatim as <c>toolVersion</c>.</param>
/// <param name="TotalPackages">Every PACK record the source holds, before filtering.</param>
/// <param name="MatchedPackages">Packages that passed the type/NPC filters, before the limit.</param>
/// <param name="Packages">The packages to write, in output order (the matched packages after the limit).</param>
/// <param name="TypeFilter">The <c>--type</c> filter, or null when none was given.</param>
/// <param name="NpcFilter">The <c>--npc</c> filter, or null when none was given.</param>
/// <param name="Limit">The <c>--limit</c> value that produced <paramref name="Packages" />.</param>
internal sealed record PackageJsonDocument(
    string Source,
    BethesdaGame Game,
    string ToolVersion,
    int TotalPackages,
    int MatchedPackages,
    IReadOnlyList<PackageRecord> Packages,
    string? TypeFilter,
    string? NpcFilter,
    int Limit);
