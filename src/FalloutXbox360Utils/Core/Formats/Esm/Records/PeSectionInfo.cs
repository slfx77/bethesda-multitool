namespace FalloutXbox360Utils.Core.Formats.Esm.Records;

/// <summary>
///     Describes a PE section from the module's in-memory PE headers.
/// </summary>
internal readonly record struct PeSectionInfo(
    int Index,
    string Name,
    uint VirtualAddress,
    uint VirtualSize,
    uint Characteristics);
