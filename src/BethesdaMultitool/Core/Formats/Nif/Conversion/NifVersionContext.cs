namespace BethesdaMultitool.Core.Formats.Nif.Conversion;

/// <summary>
///     Context for evaluating NIF version expressions. The Version / UserVersion / BsVersion are read
///     from the NIF file's own header during parsing (Oblivion in particular ships mixed versions on
///     disk), not assumed per game.
/// </summary>
public sealed record NifVersionContext
{
    /// <summary>NIF file version (e.g., 0x14020007 for 20.2.0.7)</summary>
    public uint Version { get; init; }

    /// <summary>User version (game-specific)</summary>
    public uint UserVersion { get; init; }

    /// <summary>Bethesda stream version (e.g., 34 for FO3/NV)</summary>
    public int BsVersion { get; init; }
}
