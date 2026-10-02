namespace BethesdaMultitool.Core.Actors;

/// <summary>An edition-specific calculation identity, separate from a trace's observed executable.</summary>
internal sealed record ActorEngineProfile(string Id, string ExecutableSha256, string Evidence)
{
    internal const string PcRetailId = "fnv-pc-steam-1.4.0.525";

    internal static readonly ActorEngineProfile PcRetail = new(PcRetailId,
        "3a87f92f011e5dc9179ddf733cf08be2b39ea6e5b7a8a9e3a9a72dafcc1b104d",
        "PC runtime-image e46b43cdaa32d9b79b7816fa45cb076c7ed59e335b1533118dcb6bf1d9da692d; " +
        "live session007 matching actor-value entry bytes");

    internal static ActorEngineProfile? Resolve(string? id) => id switch
    {
        null => null,
        PcRetailId or "pc-retail" => PcRetail,
        _ => throw new ArgumentException($"Unknown actor engine profile '{id}'. Available: {PcRetailId}.", nameof(id))
    };
}
