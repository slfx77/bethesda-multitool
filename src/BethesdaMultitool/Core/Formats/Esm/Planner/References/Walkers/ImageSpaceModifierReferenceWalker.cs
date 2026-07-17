using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.References.Walkers;

/// <summary>Walks the optional intro/outro SOUN references on an IMAD.</summary>
public sealed class ImageSpaceModifierReferenceWalker : IRecordReferenceWalker
{
    public string RecordType => "IMAD";

    public Type ModelType => typeof(ImageSpaceModifierRecord);

    public IEnumerable<RawReference> Walk(object model)
    {
        if (model is not ImageSpaceModifierRecord modifier)
        {
            yield break;
        }

        if (modifier.IntroSoundFormId is { } intro)
        {
            yield return new RawReference
            {
                FieldPath = "RDSD",
                FormId = intro,
            };
        }

        if (modifier.OutroSoundFormId is { } outro)
        {
            yield return new RawReference
            {
                FieldPath = "RDSI",
                FormId = outro,
            };
        }
    }
}
