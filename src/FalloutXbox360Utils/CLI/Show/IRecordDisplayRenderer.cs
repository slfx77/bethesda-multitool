using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.CLI.Show;

/// <summary>
///     Abstraction for a single record-type display renderer used by ShowCommand.
/// </summary>
internal interface IRecordDisplayRenderer
{
    bool TryShow(RecordCollection records, FormIdResolver resolver, uint? formId, string? editorId);
}
