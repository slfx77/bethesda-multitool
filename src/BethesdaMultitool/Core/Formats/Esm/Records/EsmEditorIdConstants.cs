namespace BethesdaMultitool.Core.Formats.Esm.Records;

/// <summary>
///     Constants for runtime Editor ID extraction: FormType-to-offset mappings for
///     TESFullName fields and INFO record prompt offsets.
///     FormType byte values from PDB ENUM_FORM_ID. Offsets from MemDebug PDB class layouts.
/// </summary>
internal static class EsmEditorIdConstants
{
    /// <summary>
    ///     Offset of the prompt/dialogue text BSStringT within an INFO record's TESForm.
    /// </summary>
    internal const int InfoPromptOffset = 44;

    /// <summary>
    ///     Maps runtime FormType byte values (ENUM_FORM_ID) to the offset of the TESFullName
    ///     embedded BSStringT field from the complete-object base for each record type.
    /// </summary>
    internal static readonly Dictionary<byte, int> FullNameOffsetByFormType = new()
    {
        [0x08] = 44, // FACT - TESFaction
        [0x0A] = 44, // HAIR - TESHair
        [0x0B] = 44, // EYES - TESEyes
        [0x0C] = 44, // RACE - TESRace
        [0x15] = 68, // ACTI - TESObjectACTI
        [0x18] = 68, // ARMO - TESObjectARMO
        [0x19] = 68, // BOOK - TESObjectBOOK
        [0x1B] = 80, // CONT - TESObjectCONT
        [0x1C] = 68, // DOOR - TESObjectDOOR
        [0x1F] = 68, // MISC - TESObjectMISC
        // These are complete-object-relative PDB offsets. MSTT/FLOR map values point at
        // interior TESForm subobjects (+20/+12), so callers must rebase before adding them.
        [0x22] = 4, // MSTT - BGSMovableStatic (TESFullName is first base)
        [0x26] = 80, // FLOR - TESFlora (TESFullName via TESBoundAnimObject after TESProduceForm)
        [0x28] = 68, // WEAP - TESObjectWEAP
        [0x29] = 68, // AMMO - TESAmmo
        [0x2A] = 228, // NPC_ - TESNPC
        [0x2E] = 68, // KEYM - TESKey
        [0x2F] = 68, // ALCH - AlchemyItem
        [0x33] = 68, // PROJ - BGSProjectile
        [0x47] = 68, // QUST - TESQuest (ENUM_FORM_ID value 71)
        [0x59] = 44 // AVIF - ActorValueInfo
    };
}
