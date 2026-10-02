namespace BethesdaMultitool.Core.Formats.RenderWare;

/// <summary>A camera or light and its frame association. The payload's semantics remain opaque.</summary>
internal sealed record RwClumpAttachment(int FrameIndex, RwClumpChunk Chunk);
