namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     One named waypoint path from a zone's <c>.pth</c> file. Retail holds seven paths over the
///     21 zones: three "UmbraKeth" chases in the crypts (23, 31 and 32 points) and four "New Path"
///     rows with ZERO points — an editor placeholder a reader must accept rather than treat as a
///     malformed record.
/// </summary>
/// <param name="Name">
///     Cut at the first NUL of the 64-byte field; the tail is 0xCC stack fill in 7/7 retail rows.
/// </param>
/// <param name="Points">The waypoints, in file order.</param>
internal sealed record ShadowkeyPath(string Name, IReadOnlyList<ShadowkeyPathPoint> Points);
