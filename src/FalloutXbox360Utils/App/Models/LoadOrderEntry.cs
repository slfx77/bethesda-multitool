using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils;

/// <summary>
///     A single entry in the load order. Each represents one ESM, ESP, or DMP file
///     with its parsed records and resolver. Entries later in the list (higher index)
///     override records from earlier entries, matching the game's DLC loading semantics.
/// </summary>
internal sealed class LoadOrderEntry : IDisposable
{
    /// <summary>Full path to the ESM/ESP/DMP file.</summary>
    public required string FilePath { get; init; }

    /// <summary>Display name (filename only) for the UI list.</summary>
    public string DisplayName => Path.GetFileName(FilePath);

    /// <summary>The detected file type (EsmFile or Minidump).</summary>
    public AnalysisFileType FileType { get; init; }

    /// <summary>FormID resolver built from this file's records.</summary>
    public FormIdResolver? Resolver { get; set; }

    /// <summary>Full RecordCollection parsed from this file (needed for world map terrain).</summary>
    public RecordCollection? Records { get; set; }

    /// <summary>True if this entry has been successfully loaded and parsed.</summary>
    public bool IsLoaded => Resolver != null;

    public void Dispose()
    {
        Records = null;
        Resolver = null;
    }
}
