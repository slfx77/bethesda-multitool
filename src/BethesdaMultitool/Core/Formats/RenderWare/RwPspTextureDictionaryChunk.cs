namespace BethesdaMultitool.Core.Formats.RenderWare;

/// <summary>A complete original chunk, with its raw header and payload retained in the dictionary's owned source copy.</summary>
/// <param name="Type">Raw chunk type.</param>
/// <param name="LibraryId">Raw library word.</param>
/// <param name="HeaderOffset">Header byte offset relative to this dictionary root.</param>
/// <param name="Bytes">Original header and complete bounded payload, without rewriting size words.</param>
internal sealed record RwPspTextureDictionaryChunk(uint Type, uint LibraryId, int HeaderOffset, ReadOnlyMemory<byte> Bytes)
{
    /// <summary>The bounded chunk body; the unmodified declared size remains in Bytes.</summary>
    public ReadOnlyMemory<byte> Payload => Bytes[RwChunk.HeaderLength..];
}
