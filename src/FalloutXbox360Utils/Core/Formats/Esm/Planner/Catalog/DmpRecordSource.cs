using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;

/// <summary>
///     DMP-side input to <see cref="RecordCatalog" />. Walks the typed <see cref="RecordCollection" />
///     lists and yields <see cref="CatalogEntry" /> values keyed by signature.
/// </summary>
/// <remarks>
///     Tier 0 ships with an empty type→list mapping. As each tier ports a record type, the
///     enumeration adds the corresponding <see cref="RecordCollection" /> list. The empty
///     default means a Tier 0 build with any non-trivial <c>enabledTypes</c> set still
///     produces zero <see cref="SourceKind.DmpOverride" /> / <see cref="SourceKind.DmpNew" />
///     entries — which is correct (no encoders yet), and the parity harness catches any
///     accidental cross-pipeline interference.
/// </remarks>
public sealed class DmpRecordSource
{
    /// <summary>
    ///     Per-record-type extractors. Each yields <c>(FormId, Model)</c> pairs in the same
    ///     order legacy <c>EnumerateModelsByType</c> yields them — Tier 1 byte-exact parity
    ///     depends on this. Add the type's row when its planned encoder ships.
    /// </summary>
    private static readonly Dictionary<string, Func<RecordCollection, IEnumerable<(uint FormId, object Model)>>>
        Extractors = new(StringComparer.Ordinal)
        {
            // Tier 1 — trivial static-data encoders.
            ["GMST"] = c => c.GameSettings.Select(r => (r.FormId, (object)r)),
            ["GLOB"] = c => c.Globals.Select(r => (r.FormId, (object)r)),
            ["WEAP"] = c => c.Weapons.Select(r => (r.FormId, (object)r)),
            ["ARMO"] = c => c.Armor.Select(r => (r.FormId, (object)r)),
            ["AMMO"] = c => c.Ammo.Select(r => (r.FormId, (object)r)),
            ["ALCH"] = c => c.Consumables.Select(r => (r.FormId, (object)r)),
            ["BOOK"] = c => c.Books.Select(r => (r.FormId, (object)r)),
            ["STAT"] = c => c.Statics.Select(r => (r.FormId, (object)r)),
            // Tier 2 — simple FormID-ref encoders (FormIDs emitted verbatim or via WEAP's
            // transitional validFormIds/remapTable pass-through).
            ["DOOR"] = c => c.Doors.Select(r => (r.FormId, (object)r)),
            ["MISC"] = c => c.MiscItems.Select(r => (r.FormId, (object)r)),
            ["KEYM"] = c => c.Keys.Select(r => (r.FormId, (object)r)),
            ["NOTE"] = c => c.Notes.Select(r => (r.FormId, (object)r)),
            ["RCPE"] = c => c.Recipes.Select(r => (r.FormId, (object)r)),
            ["COBJ"] = c => c.ConstructibleObjects.Select(r => (r.FormId, (object)r)),
            ["ARMA"] = c => c.ArmorAddons.Select(r => (r.FormId, (object)r)),
            ["IMOD"] = c => c.WeaponMods.Select(r => (r.FormId, (object)r)),
            ["ENCH"] = c => c.Enchantments.Select(r => (r.FormId, (object)r)),
            ["SPEL"] = c => c.Spells.Select(r => (r.FormId, (object)r)),
            ["EXPL"] = c => c.Explosions.Select(r => (r.FormId, (object)r)),
            ["MGEF"] = c => c.BaseEffects.Select(r => (r.FormId, (object)r)),
            ["PROJ"] = c => c.Projectiles.Select(r => (r.FormId, (object)r)),
        };

    private readonly RecordCollection _collection;

    public DmpRecordSource(RecordCollection collection)
    {
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
    }

    /// <summary>
    ///     Yield one entry per DMP record whose type appears in <paramref name="enabledTypes" />.
    ///     The combine step in <see cref="RecordCatalog" /> matches these against the master
    ///     side by FormID — entries paired with master become
    ///     <see cref="SourceKind.DmpOverride" />, the rest <see cref="SourceKind.DmpNew" />.
    /// </summary>
    public IEnumerable<(string Type, uint FormId, object Model)> Enumerate(IReadOnlySet<string> enabledTypes)
    {
        if (enabledTypes.Count == 0)
        {
            yield break;
        }

        foreach (var type in enabledTypes)
        {
            if (!Extractors.TryGetValue(type, out var extractor))
            {
                continue; // No mapping yet for this type — caller must ensure the planner has an encoder before enabling.
            }

            foreach (var (formId, model) in extractor(_collection))
            {
                yield return (type, formId, model);
            }
        }
    }

    /// <summary>
    ///     True when <see cref="DmpRecordSource" /> knows how to enumerate the given record
    ///     type. <c>EsmPlanner.Build</c> uses this so unmapped types route to the legacy
    ///     pipeline without a wasted catalog pass.
    /// </summary>
    public static bool SupportsType(string recordType) => Extractors.ContainsKey(recordType);
}
