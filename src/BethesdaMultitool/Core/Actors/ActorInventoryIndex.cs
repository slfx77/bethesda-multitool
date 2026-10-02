using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Actors;

/// <summary>Indexes concrete item identities and snapshot global values once per actor inspector.</summary>
internal sealed class ActorInventoryIndex
{
    private readonly Dictionary<uint, float> _globals = [];
    private readonly HashSet<uint> _items = [];

    /// <summary>Indexes known inventory record families and preserves final global overrides.</summary>
    /// <param name="records">The inspector's parsed source snapshot, which must remain unchanged.</param>
    /// <param name="cancellationToken">Cancellation checked for each indexed record.</param>
    internal ActorInventoryIndex(RecordCollection records, CancellationToken cancellationToken)
    {
        foreach (var global in records.Globals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _globals[global.FormId] = global.Value;
        }
        var ids = records.Weapons.Select(item => item.FormId)
            .Concat(records.Armor.Select(item => item.FormId)).Concat(records.Ammo.Select(item => item.FormId))
            .Concat(records.Books.Select(item => item.FormId)).Concat(records.Consumables.Select(item => item.FormId))
            .Concat(records.MiscItems.Select(item => item.FormId)).Concat(records.Keys.Select(item => item.FormId))
            .Concat(records.Notes.Select(item => item.FormId)).Concat(records.WeaponMods.Select(item => item.FormId))
            .Concat(records.Ingredients.Select(item => item.FormId)).Concat(records.Lights.Select(item => item.FormId))
            .Concat(records.CaravanCards.Select(item => item.FormId)).Concat(records.CaravanMoney.Select(item => item.FormId))
            .Concat(records.GenericRecords.Where(item => item.RecordType is "SLGM" or "CLOT" or "APPA" or "CHIP"
                or "WEAP" or "ARMO" or "AMMO" or "BOOK" or "ALCH" or "MISC" or "KEYM" or "NOTE"
                or "IMOD" or "INGR" or "LIGH" or "CCRD" or "CMNY").Select(item => item.FormId));
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _items.Add(id);
        }
    }

    /// <summary>Checks whether the parsed snapshot contains a supported concrete inventory record.</summary>
    /// <param name="formId">Resolved item identity.</param>
    /// <returns>True for a known concrete item; list and missing identities return false.</returns>
    internal bool Contains(uint formId) => _items.Contains(formId);

    /// <summary>Reads an authored global value without substituting a script or live runtime value.</summary>
    /// <param name="formId">Global record identity.</param>
    /// <param name="value">The raw parsed value, which the generator must validate.</param>
    /// <returns>Whether the final source snapshot contains the global.</returns>
    internal bool TryGetGlobal(uint formId, out float value) => _globals.TryGetValue(formId, out value);
}
