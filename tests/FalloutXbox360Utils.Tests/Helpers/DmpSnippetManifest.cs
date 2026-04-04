using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Tests.Helpers;

internal sealed class DmpSnippetManifest
{
    public required string SourceFileName { get; init; }
    public long SourceFileSize { get; init; }
    public required DmpSnippetMinidumpInfo MinidumpInfo { get; init; }
    public required List<RuntimeEditorIdEntry> RuntimeEditorIds { get; init; }
    public required List<RuntimeEditorIdEntry> RuntimeRefrFormEntries { get; init; }
    public required Dictionary<uint, string> FormIdMap { get; init; }
    public required List<DmpSnippetRange> Ranges { get; init; }
}