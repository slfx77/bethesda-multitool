using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers;

/// <summary>
///     Reference walker for placed references (REFR / ACHR / ACRE — all share the
///     <see cref="PlacedReference" /> model). Yields the outgoing FormID fields the
///     legacy <c>RefrEncoder</c> sanitizes (XEZN / XLKR / XOWN / XESP / XTEL / XLOC).
/// </summary>
public sealed class RefrReferenceWalker : IRecordReferenceWalker
{
    public string RecordType => "REFR";
    public Type ModelType => typeof(PlacedReference);

    public IEnumerable<RawReference> Walk(object model)
    {
        if (model is not PlacedReference placed)
        {
            yield break;
        }

        // NAME — base object FormID (required; engine null-derefs on 0).
        yield return new RawReference
        {
            FieldPath = FieldPath.Subrecord("NAME"),
            FormId = placed.BaseFormId,
        };

        if (placed.OwnerFormId.HasValue)
        {
            yield return new RawReference
            {
                FieldPath = FieldPath.Subrecord("XOWN"),
                FormId = placed.OwnerFormId.Value,
            };
        }

        if (placed.EncounterZoneFormId.HasValue)
        {
            yield return new RawReference
            {
                FieldPath = FieldPath.Subrecord("XEZN"),
                FormId = placed.EncounterZoneFormId.Value,
            };
        }

        if (placed.EnableParentFormId.HasValue)
        {
            yield return new RawReference
            {
                FieldPath = FieldPath.Subrecord("XESP"),
                FormId = placed.EnableParentFormId.Value,
            };
        }

        if (placed.DestinationDoorFormId.HasValue)
        {
            yield return new RawReference
            {
                FieldPath = FieldPath.Member("XTEL", "DestinationDoor"),
                FormId = placed.DestinationDoorFormId.Value,
            };
        }

        if (placed.LockKeyFormId.HasValue)
        {
            yield return new RawReference
            {
                FieldPath = FieldPath.Member("XLOC", "LockKey"),
                FormId = placed.LockKeyFormId.Value,
            };
        }

        if (placed.LinkedRefFormId.HasValue)
        {
            yield return new RawReference
            {
                FieldPath = FieldPath.Subrecord("XLKR"),
                FormId = placed.LinkedRefFormId.Value,
            };
        }

        if (placed.LinkedRefKeywordFormId.HasValue)
        {
            yield return new RawReference
            {
                FieldPath = FieldPath.Member("XLKR", "Keyword"),
                FormId = placed.LinkedRefKeywordFormId.Value,
            };
        }
    }
}
