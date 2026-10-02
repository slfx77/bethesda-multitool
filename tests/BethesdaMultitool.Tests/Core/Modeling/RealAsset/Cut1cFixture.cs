namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     A resolved cut-1c cover row: its payload bytes (for an LZSS entry the decompressed bytes, verified against the
///     manifest's payload SHA-256, with the stored bytes verified against the stored SHA-256 on the way) and where
///     they came from.
/// </summary>
/// <param name="Bytes">The payload bytes (empty for the pinned empty ROB segment).</param>
/// <param name="ResolvedFrom">The candidate that supplied them (source, entry and index).</param>
/// <param name="Step">Which candidate found them: <c>source</c> (the manifest's primary) or <c>alsoIn</c>.</param>
internal sealed record Cut1cFixture(byte[] Bytes, string ResolvedFrom, string Step);
