namespace BethesdaMultitool.Core.Formats.RenderWare;

/// <summary>Caller-proven occurrence metadata for one explicitly selected dictionary; names are not lookup precedence.</summary>
/// <param name="SourceIdentity">The containing source/opening identity, not an authoring-path dependency key.</param>
/// <param name="EntryOrdinal">Original archive table ordinal, retaining duplicate entry names.</param>
/// <param name="EntryName">Original archive entry name for diagnostics.</param>
/// <param name="EntryOffset">Absolute entry byte offset in its archive.</param>
/// <param name="EntryLength">Complete entry byte length bounding the selected dictionary.</param>
/// <param name="ResourceOrdinal">Original named-resource ordinal within the entry.</param>
/// <param name="PayloadOffset">Dictionary root offset relative to the entry.</param>
/// <param name="AuthoringPath">Unmodified diagnostic authoring path; never a resolver key.</param>
internal sealed record RwPspTextureDictionaryOrigin(
    string SourceIdentity, int EntryOrdinal, string EntryName, long EntryOffset, long EntryLength,
    int ResourceOrdinal, int PayloadOffset, string AuthoringPath);
