using BethesdaMultitool.Core.Formats.Esm;

namespace BethesdaMultitool.Core.Semantic;

/// <summary>One ESM/ESP in a load order: its path, parsed header, and zero-based load index.</summary>
internal sealed record EsmLoadOrderFile(
    string FilePath,
    string FileName,
    EsmFileHeader Header,
    int LoadIndex);
