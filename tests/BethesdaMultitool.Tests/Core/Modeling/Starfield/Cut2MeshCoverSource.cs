namespace BethesdaMultitool.Tests.Core.Modeling.Starfield;

/// <summary>
///     One place a cut-2 cover row's payload bytes can be read from: a Starfield GNRL archive spelled as the manifest
///     spells sources (<c>&lt;SteamLibrary&gt;/Starfield/Data/&lt;archive&gt;</c>), the entry path inside it and the
///     entry's directory index as the generator measured it. Every candidate is verified against the row's size and
///     SHA-256 before it is used.
/// </summary>
/// <param name="Source">The manifest source string, exactly as the manifest spells it.</param>
/// <param name="Archive">The archive's file name inside the Starfield Data folder.</param>
/// <param name="Entry">The entry path inside the archive (slash separated, as the name table stores it).</param>
/// <param name="Index">The entry's directory index inside the archive.</param>
internal sealed record Cut2MeshCoverSource(string Source, string Archive, string Entry, int Index);
