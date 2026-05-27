using FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;

/// <summary>
///     Outcome of a DMP→ESP conversion run.
/// </summary>
public sealed record PluginBuildResult
{
    public required bool Success { get; init; }
    public required ConversionPipelineStats Stats { get; init; }

    /// <summary>Path of the written ESP, or null if the run failed before write.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Error message, set when <see cref="Success" /> is false.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Validation report, set when <see cref="PluginBuildOptions.ValidateOutput" /> was true.</summary>
    public string? ValidationReport { get; init; }

    /// <summary>
    ///     Source-DMP FormID → emitted-ESP FormID alias map. Captures every new record the
    ///     converter allocated a fresh FormID for (the source key may be a runtime FormID,
    ///     a master-EditorID-aliased FormID, etc.). Consumers like the asset packer use it
    ///     to remap CSV-shaped voice paths (which reference source FormIDs) onto the
    ///     engine's lookup path (which uses the emitted ESP FormID).
    /// </summary>
    public IReadOnlyDictionary<uint, uint> NewRecordSourceToAllocated { get; init; } =
        new Dictionary<uint, uint>();

    /// <summary>
    ///     Per-emitted-INFO×response triple-key bindings used by the asset packer to bridge
    ///     build-era FormID drift between CSV-supplied dialogue voice paths and the converter's
    ///     emitted INFO FormIDs. Each binding carries the parent DIAL EditorId, speaker
    ///     voice-type EditorId, and 1-based response number — enough to reconstruct the
    ///     engine's runtime voice-file path even when the CSV references a different FormID
    ///     era than the source DMP.
    /// </summary>
    public IReadOnlyList<EmittedDialogueAudioBinding> EmittedDialogueAudioBindings { get; init; } = [];
}
