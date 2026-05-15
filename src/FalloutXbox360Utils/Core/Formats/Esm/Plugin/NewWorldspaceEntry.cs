namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

/// <summary>
///     Bookkeeping for a new (non-master) WRLD that's been pre-encoded by
///     <see cref="PluginBuilder" /> so the cell-children pipeline can emit it alongside its
///     captured child cells. The keys of the dictionary that holds these entries are the
///     ORIGINAL DMP-source FormID; <see cref="EmittedFormId" /> is the plugin-index FormID
///     allocated for the emitted output (and matches the FormID encoded inside
///     <see cref="RecordBytes" />).
/// </summary>
public sealed record NewWorldspaceEntry(uint EmittedFormId, byte[] RecordBytes);
