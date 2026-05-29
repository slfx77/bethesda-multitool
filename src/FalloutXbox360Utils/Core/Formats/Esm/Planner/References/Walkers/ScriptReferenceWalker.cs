using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers;

/// <summary>
///     Walks SCRO subrecords (referenced objects) on a parsed <see cref="ScriptRecord" />.
///     Each referenced FormID yields one <see cref="RawReference" /> with field path
///     <c>SCRO[i]</c>. Paired with the SCPT entry in <see cref="DegradationPolicy" /> set
///     to drop the SCRO subrecord when the target isn't in the emit set — the v54 SCRO
///     dangle fix.
/// </summary>
public sealed class ScriptReferenceWalker : IRecordReferenceWalker
{
    public string RecordType => "SCPT";

    public Type ModelType => typeof(ScriptRecord);

    public IEnumerable<RawReference> Walk(object model)
    {
        if (model is not ScriptRecord script)
        {
            yield break;
        }

        for (var i = 0; i < script.ReferencedObjects.Count; i++)
        {
            var formId = script.ReferencedObjects[i];
            yield return new RawReference
            {
                FieldPath = FieldPath.Indexed("SCRO", i),
                FormId = formId,
            };
        }
    }
}
