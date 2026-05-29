using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers;

/// <summary>
///     Walks outgoing FormID references on a parsed INFO (<see cref="DialogueRecord" />):
///     parent topic + parent quest, speaker + speaker-related condition FormIDs, topic-link
///     chains (TCLT/TCLF/NAME), the PNAM previous-info chain, and SCRO refs inside each
///     result-script block. Per-CTDA Reference fields are yielded so dialogue conditions
///     pointing at deleted refs can be sanitized via the degradation policy.
/// </summary>
public sealed class InfoReferenceWalker : IRecordReferenceWalker
{
    public string RecordType => "INFO";

    public Type ModelType => typeof(DialogueRecord);

    public IEnumerable<RawReference> Walk(object model)
    {
        if (model is not DialogueRecord info)
        {
            yield break;
        }

        if (info.QuestFormId is uint quest && quest != 0)
        {
            yield return new RawReference
            {
                FieldPath = FieldPath.Subrecord("QSTI"),
                FormId = quest,
            };
        }

        if (info.SpeakerFormId is uint speaker && speaker != 0)
        {
            yield return new RawReference
            {
                FieldPath = FieldPath.Subrecord("ANAM"),
                FormId = speaker,
            };
        }

        if (info.SpeakerAnimationFormId is uint anim && anim != 0)
        {
            yield return new RawReference
            {
                FieldPath = FieldPath.Subrecord("SNAM"),
                FormId = anim,
            };
        }

        if (info.PreviousInfo is uint prev && prev != 0)
        {
            yield return new RawReference
            {
                FieldPath = FieldPath.Subrecord("PNAM"),
                FormId = prev,
            };
        }

        for (var i = 0; i < info.LinkToTopics.Count; i++)
        {
            yield return new RawReference
            {
                FieldPath = FieldPath.Indexed("TCLT", i),
                FormId = info.LinkToTopics[i],
            };
        }

        for (var i = 0; i < info.LinkFromTopics.Count; i++)
        {
            yield return new RawReference
            {
                FieldPath = FieldPath.Indexed("TCLF", i),
                FormId = info.LinkFromTopics[i],
            };
        }

        for (var i = 0; i < info.AddTopics.Count; i++)
        {
            yield return new RawReference
            {
                FieldPath = FieldPath.Indexed("NAME", i),
                FormId = info.AddTopics[i],
            };
        }

        for (var c = 0; c < info.Conditions.Count; c++)
        {
            var condition = info.Conditions[c];
            if (condition.Reference != 0)
            {
                yield return new RawReference
                {
                    FieldPath = FieldPath.IndexedMember("CTDA", c, "Reference"),
                    FormId = condition.Reference,
                };
            }
        }

        for (var s = 0; s < info.ResultScripts.Count; s++)
        {
            var script = info.ResultScripts[s];
            for (var i = 0; i < script.ReferencedObjects.Count; i++)
            {
                yield return new RawReference
                {
                    FieldPath = $"ResultScripts[{s}].SCRO[{i}]",
                    FormId = script.ReferencedObjects[i],
                };
            }
        }
    }
}
