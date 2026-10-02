using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.CLI.Show;

/// <summary>
///     Abstraction for a single record-type display renderer used by ShowCommand.
///     <para>
///         Every data-derived string a renderer interpolates into Spectre markup (EditorIDs, names,
///         resolver output, actor-value names, paths) must be escaped — see
///         <see cref="ShowHelpers.Ref" /> and <see cref="ShowHelpers.Plain" />.
///     </para>
/// </summary>
internal interface IRecordDisplayRenderer
{
    /// <summary>
    ///     Render the record matching <paramref name="formId" /> or <paramref name="editorId" /> to
    ///     <see cref="ShowRenderContext.Console" />, returning false when this renderer does not own it.
    /// </summary>
    bool TryShow(RecordCollection records, FormIdResolver resolver, uint? formId, string? editorId,
        ShowRenderContext context);
}
