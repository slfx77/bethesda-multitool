namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     A faction rank with male/female titles and insignia path.
/// </summary>
public record FactionRank(int RankNumber, string? MaleTitle, string? FemaleTitle, string? Insignia);
