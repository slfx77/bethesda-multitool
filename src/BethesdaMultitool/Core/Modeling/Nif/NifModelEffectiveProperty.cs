namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>One property in a geometry occurrence's effective property set.</summary>
/// <param name="Slot">The Gamebryo property type it fills.</param>
/// <param name="SlotName">
///     The slot's key in the material key: the slot name for the known slots, <c>other:{Type}</c> for
///     <see cref="NifPropertySlot.Other" />.
/// </param>
/// <param name="BlockIndex">The property block.</param>
/// <param name="OwnerBlockIndex">The NiAVObject block whose Properties list names it (the geometry or an ancestor).</param>
internal readonly record struct NifModelEffectiveProperty(
    NifPropertySlot Slot,
    string SlotName,
    int BlockIndex,
    int OwnerBlockIndex);
