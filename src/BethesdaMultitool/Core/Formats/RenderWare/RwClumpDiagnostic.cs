namespace BethesdaMultitool.Core.Formats.RenderWare;

/// <summary>An unevaluated source feature, identified by owning graph object and chunk type.</summary>
internal sealed record RwClumpDiagnostic(string Scope, uint ChunkType, string Reason);
