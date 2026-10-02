namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>A resolved cut-1a cover file: its bytes (verified against the manifest SHA-256) and where they came from.</summary>
/// <param name="Bytes">The file bytes.</param>
/// <param name="ResolvedFrom">The candidate that supplied them (source and entry, or a Data folder and layer).</param>
/// <param name="Step">
///     Which step of the resolver found them: <c>source</c>, <c>alsoIn</c>, <c>steamFinalBuild</c> or <c>steamInstall</c>.
/// </param>
internal sealed record Cut1aFixture(byte[] Bytes, string ResolvedFrom, string Step);
