namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     One place a cut-1b cover file's bytes can be read from: a manifest source (an archive under
///     <c>Sample/Builds</c>, spelled <c>Sample/...</c>, or a <c>&lt;SteamLibrary&gt;/&lt;game&gt;/&lt;path&gt;</c> spelling)
///     and the entry inside it. Every candidate is verified against the manifest's SHA-256 before it is used.
/// </summary>
/// <param name="Source">The manifest source string, exactly as the manifest spells it.</param>
/// <param name="Entry">The path inside the source (forward slashes).</param>
internal sealed record Cut1bCoverSource(string Source, string Entry);
