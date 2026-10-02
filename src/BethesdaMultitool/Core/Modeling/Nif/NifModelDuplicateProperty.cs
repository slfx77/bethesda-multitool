namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     A property that one object lists after another property of the same slot. The first entry in the stored
///     Properties list is kept (Assumed: which of two same-type properties on one object Gamebryo applies is not
///     established); the reader reports the duplicate and keeps both blocks in native state.
/// </summary>
/// <param name="SlotName">The slot both properties fill.</param>
/// <param name="OwnerBlockIndex">The object listing both.</param>
/// <param name="KeptBlockIndex">The property that is effective.</param>
/// <param name="IgnoredBlockIndex">The later property that is not.</param>
internal readonly record struct NifModelDuplicateProperty(
    string SlotName,
    int OwnerBlockIndex,
    int KeptBlockIndex,
    int IgnoredBlockIndex);
