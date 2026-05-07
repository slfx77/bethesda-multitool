using FalloutXbox360Utils.Core.Formats.Esm;

namespace FalloutXbox360Utils.Core.Semantic;

internal sealed record EsmLoadOrderFile(
    string FilePath,
    string FileName,
    EsmFileHeader Header,
    int LoadIndex);
