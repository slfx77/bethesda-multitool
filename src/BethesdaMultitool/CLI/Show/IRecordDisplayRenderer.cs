using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.CLI.Show;

/// <summary>
///     Abstraction for a single record-type display renderer used by ShowCommand.
/// </summary>
internal interface IRecordDisplayRenderer
{
    bool TryShow(RecordCollection records, FormIdResolver resolver, uint? formId, string? editorId);
}

