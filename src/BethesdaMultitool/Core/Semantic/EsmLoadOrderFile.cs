using BethesdaMultitool.Core.Formats.Esm;

namespace BethesdaMultitool.Core.Semantic;

internal sealed record EsmLoadOrderFile(
    string FilePath,
    string FileName,
    EsmFileHeader Header,
    int LoadIndex);
