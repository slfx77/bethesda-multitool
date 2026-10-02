namespace BethesdaMultitool.Core.Formats.RenderWare;

/// <summary>A complete opaque chunk body, with its header offset relative to the CLUMP.</summary>
internal sealed record RwClumpChunk(uint Type, uint LibraryId, int HeaderOffset, ReadOnlyMemory<byte> Payload);
