namespace BethesdaMultitool.Core.Formats.Travels;

/// <summary>
///     One dungeon's two-line display name from Stormhold's <c>dungnamesin.dat</c> — the game
///     prints the pair stacked ("Dungeon"/"Camp", "North"/"Slope", "Varus'"/"Victory").
/// </summary>
/// <param name="Id">1-based dungeon id, matching <see cref="TravelsDungeonLink.Id" />.</param>
/// <param name="FirstLine">The upper line.</param>
/// <param name="SecondLine">The lower line.</param>
internal sealed record TravelsDungeonName(int Id, string FirstLine, string SecondLine);
