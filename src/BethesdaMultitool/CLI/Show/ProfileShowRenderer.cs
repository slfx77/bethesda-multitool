using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles;

namespace BethesdaMultitool.CLI.Show;

/// <summary>
///     Renders a schema-decoded record (a <see cref="GenericEsmRecord" /> carrying a DecodedTree) through
///     its presentation profile, so games without typed handlers (Oblivion/Skyrim/FO4/FO76/Morrowind) get
///     the SAME curated, sectioned display the typed FNV renderers produce — instead of the generic flat
///     field dump. Sits just before <see cref="GenericShowRenderer" /> in the chain, and after the typed
///     renderers, so FNV/FO3 (whose NPCs are typed records, not GenericRecords) keep their existing path.
/// </summary>
internal sealed class ProfileShowRenderer : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver, uint? formId, string? editorId)
    {
        var record = records.GenericRecords.FirstOrDefault(r =>
            r.DecodedTree is { Count: > 0 } &&
            RecordProfiles.Has(r.RecordType) &&
            ((formId.HasValue && r.FormId == formId.Value) ||
             (!string.IsNullOrEmpty(editorId) &&
              string.Equals(r.EditorId, editorId, StringComparison.OrdinalIgnoreCase))));

        if (record?.DecodedTree is null || RecordProfiles.Get(record.RecordType) is not { } profile)
        {
            return false;
        }

        var model = profile.Build(
            record.FormId, record.EditorId, record.FullName, record.DecodedTree, records.Game, resolver, records);
        SharedRecordDetailShowRenderer.Render(model);
        return true;
    }
}
