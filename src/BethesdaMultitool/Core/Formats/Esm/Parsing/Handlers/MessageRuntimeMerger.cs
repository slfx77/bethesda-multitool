using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

/// <summary>Keeps message button text, conditions and source attribution as one representation.</summary>
internal static class MessageRuntimeMerger
{
    internal static MessageRecord Merge(MessageRecord stored, MessageRecord runtime)
    {
        // A later runtime occurrence is separate evidence, not a replacement for
        // the object whose text/conditions were already selected.
        if (stored.RuntimeButtons is not null)
        {
            if (runtime.RuntimeButtons is not { } additional ||
                stored.RuntimeButtons.ObjectFileOffset == additional.ObjectFileOffset ||
                stored.AdditionalRuntimeButtons.Any(item => item.ObjectFileOffset == additional.ObjectFileOffset))
                return stored;
            return stored with { AdditionalRuntimeButtons = [.. stored.AdditionalRuntimeButtons, additional] };
        }
        var filled = (MessageRecord)RecordModelUnion.Fill(stored, runtime);
        var useRuntimeButtons = stored.Buttons.Count == 0 && runtime.Buttons.Count > 0;
        return filled with
        {
            // Lists must move together: an empty stored condition list does not authorize
            // copying conditions from a different runtime menu into the stored buttons.
            Buttons = useRuntimeButtons ? runtime.Buttons : stored.Buttons,
            ButtonConditions = useRuntimeButtons ? runtime.ButtonConditions : stored.ButtonConditions,
            ButtonSource = useRuntimeButtons ? MessageFieldSource.RuntimeObject : stored.ButtonSource,
            StoredButtonCount = stored.StoredButtonCount ??
                (stored.ButtonSource == MessageFieldSource.StoredRecord ? stored.Buttons.Count : null),
            RuntimeButtons = runtime.RuntimeButtons,
            RuntimeEvidence = runtime.RuntimeEvidence,
            Offset = stored.Offset,
            IsBigEndian = stored.IsBigEndian,
            Description = !string.IsNullOrEmpty(stored.Description) ? stored.Description : runtime.Description,
            DescriptionSource = !string.IsNullOrEmpty(stored.Description)
                ? stored.DescriptionSource : runtime.DescriptionSource,
            // A pre-ITXT condition belongs to the accessor representation, even if its
            // primary button list is empty and the runtime list supplies a preview.
            UnassignedConditions = stored.UnassignedConditions
        };
    }
}
