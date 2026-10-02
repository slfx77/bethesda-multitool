namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     One place a cut-1a cover file's bytes can be read from: a manifest source (a loose directory such as the retired
///     unpacked PC tree, a <c>.bsa</c>, or a <c>&lt;SteamLibrary&gt;/&lt;game&gt;/&lt;path&gt;</c> spelling that
///     <see cref="Helpers.RealAssetPaths" /> resolves) and the entry inside it. Every candidate is verified against the
///     manifest's SHA-256 before it is used, so the order of candidates only decides which copy is read first.
/// </summary>
/// <param name="Source">The manifest source string, exactly as the manifest spells it.</param>
/// <param name="Entry">The path inside the source (forward slashes).</param>
internal sealed record Cut1aCoverSource(string Source, string Entry);
