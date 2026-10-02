namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>A resolved cut-1b cover file: its bytes (verified against the manifest SHA-256) and where they came from.</summary>
/// <param name="Bytes">The file bytes.</param>
/// <param name="ResolvedFrom">The candidate that supplied them (source and entry).</param>
/// <param name="Step">Which candidate found them: <c>source</c> (the manifest's own container) or <c>alsoIn</c>.</param>
internal sealed record Cut1bFixture(byte[] Bytes, string ResolvedFrom, string Step);
