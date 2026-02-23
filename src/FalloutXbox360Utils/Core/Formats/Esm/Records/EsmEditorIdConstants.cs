namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Constants for runtime Editor ID extraction: FormType-to-offset mappings for
///     TESFullName fields and INFO record prompt offsets.
/// </summary>
internal static class EsmEditorIdConstants
{
    /// <summary>
    ///     Maps runtime FormType byte values to the offset of the TESFullName pointer
    ///     within the C++ class hierarchy for each record type.
    /// </summary>
    internal static readonly Dictionary<byte, int> FullNameOffsetByFormType = new()
    {
        [0x08] = 44, // FACT - TESFaction
        [0x0A] = 44, // HAIR - TESHair (TESForm->TESFullName->TESModel->TESHair)
        [0x0B] = 44, // EYES - TESEyes (TESForm->TESFullName->TESModel->TESEyes)
        [0x0C] = 44, // RACE - TESRace
        [0x15] = 68, // ACTI - TESObjectACTI
        [0x18] = 68, // ARMO - TESObjectARMO
        [0x19] = 68, // BOOK - TESObjectBOOK
        [0x1B] = 80, // CONT - TESObjectCONT
        [0x1C] = 68, // DOOR - TESObjectDOOR
        [0x1F] = 68, // MISC - TESObjectMISC
        [0x28] = 68, // WEAP - TESObjectWEAP
        [0x29] = 68, // AMMO - TESAmmo
        [0x2A] = 228, // NPC_ - TESNPC
        [0x2E] = 68, // KEYM - TESKey
        [0x2F] = 68, // ALCH - AlchemyItem
        [0x33] = 68 // PROJ - BGSProjectile (TESBoundObject->TESFullName->TESModel->...)
    };

    /// <summary>
    ///     Offset of the prompt/dialogue text BSStringT within an INFO record's TESForm.
    /// </summary>
    internal const int InfoPromptOffset = 44;
}
