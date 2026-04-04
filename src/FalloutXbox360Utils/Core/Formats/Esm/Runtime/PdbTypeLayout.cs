namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime;

/// <summary>
///     PDB-derived struct layout for a single FormType (e.g., TESFaction, TESObjectWEAP).
/// </summary>
internal sealed record PdbTypeLayout(
    byte FormType,
    string RecordCode,
    string ClassName,
    int StructSize,
    List<PdbFieldLayout> Fields);
