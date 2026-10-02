using System.Globalization;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The effective property set of one placed geometry occurrence (plan section 3, "Materials and properties"): at most
///     one property per <see cref="NifPropertySlot" />, the nearest on the path from the geometry to its root. Two
///     occurrences with the same set (and the same texture-coordinate limit, see <see cref="NifModelMaterialReader" />)
///     share one material; a property on a node therefore gives each placement under it its own key.
/// </summary>
internal sealed class NifModelPropertySet
{
    /// <summary>Creates a set.</summary>
    /// <param name="properties">One property per slot name.</param>
    /// <param name="duplicates">Same-slot properties one object listed after the one that was kept.</param>
    public NifModelPropertySet(IEnumerable<NifModelEffectiveProperty> properties,
        IReadOnlyList<NifModelDuplicateProperty> duplicates)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(duplicates);
        Properties = properties.OrderBy(p => p.SlotName, StringComparer.Ordinal).ToArray();
        Duplicates = duplicates;
        Key = string.Join(";", Properties.Select(p =>
            string.Create(CultureInfo.InvariantCulture, $"{p.SlotName}={p.BlockIndex}")));
    }

    /// <summary>The effective properties, ordered by slot name.</summary>
    public IReadOnlyList<NifModelEffectiveProperty> Properties { get; }

    /// <summary>Same-slot properties that lost to an earlier entry in the same object's Properties list.</summary>
    public IReadOnlyList<NifModelDuplicateProperty> Duplicates { get; }

    /// <summary>The deterministic key: <c>slot=block</c> pairs in slot-name order.</summary>
    public string Key { get; }

    /// <summary>True when no property is effective on the occurrence.</summary>
    public bool IsEmpty => Properties.Count == 0;

    /// <summary>The effective property of a known slot, or null.</summary>
    public NifModelEffectiveProperty? Get(NifPropertySlot slot)
    {
        foreach (var property in Properties)
        {
            if (property.Slot == slot && slot != NifPropertySlot.Other)
            {
                return property;
            }
        }

        return null;
    }
}
