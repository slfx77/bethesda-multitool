using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles;

/// <summary>
///     A per-record-type presentation profile: builds the curated <see cref="RecordDetailModel" /> (the
///     same sectioned model the GUI and CLI already render) from a schema-decoded <see cref="DecodedNode" />
///     tree, for ANY game. This is the unification point — one profile reproduces the hand-written
///     <see cref="RecordDetailBuilders" /> output for FNV (proven by an FNV parity test) and lights the same
///     curated display up for the other games' records, which otherwise show a flat field tree.
///     <para>
///         Identity (FormID / EditorID / display name) is passed in rather than read from the tree, mirroring
///         how the typed builders read it from the record carrier — the carrier already resolved localized
///         names and editor ids the raw tree node may not.
///     </para>
/// </summary>
internal interface IRecordProfile
{
    /// <summary>The 4-char record type this profile renders (e.g. "NPC_").</summary>
    string RecordType { get; }

    RecordDetailModel Build(
        uint formId,
        string? editorId,
        string? displayName,
        IReadOnlyList<DecodedNode> tree,
        BethesdaGame game,
        FormIdResolver resolver);
}
