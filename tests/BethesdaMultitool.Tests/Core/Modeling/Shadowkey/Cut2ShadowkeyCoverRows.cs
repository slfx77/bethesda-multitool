namespace BethesdaMultitool.Tests.Core.Modeling.Shadowkey;

/// <summary>A pinned file: its byte length and lowercase SHA-256.</summary>
/// <param name="Size">The byte length.</param>
/// <param name="Sha256">The lowercase SHA-256.</param>
internal sealed record Cut2ShadowkeyPin(long Size, string Sha256);

/// <summary>
///     One pack-slot row of the cut-2 Shadowkey cover manifest: a cover or edge record, pinned by slot, entry name,
///     offset in <c>models.huge</c>, size and SHA-256, with the other slots holding the same bytes.
/// </summary>
/// <param name="Role"><c>cover</c> or <c>edge</c>.</param>
/// <param name="Name">The row's unique name (<c>slot N name.bin</c>).</param>
/// <param name="Slot">The slot index.</param>
/// <param name="Entry">The archive entry name (<c>NNN_name.bin</c>).</param>
/// <param name="Offset">The slot's offset in <c>models.huge</c>.</param>
/// <param name="Size">The record's byte length.</param>
/// <param name="Sha256">The record's SHA-256.</param>
/// <param name="AlsoInSlots">Other slots with byte-identical payloads.</param>
/// <param name="Cell">The cover cell.</param>
/// <param name="Tags">The cover tags.</param>
/// <param name="Edges">The edge labels this row serves.</param>
internal sealed record Cut2ShadowkeySlotRow(
    string Role,
    string Name,
    int Slot,
    string Entry,
    long Offset,
    long Size,
    string Sha256,
    IReadOnlyList<int> AlsoInSlots,
    string Cell,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Edges);

/// <summary>One zone row: every file of the zone pinned, the <c>.zmp</c> first.</summary>
/// <param name="Role"><c>cover</c> or <c>edge</c>.</param>
/// <param name="Name">The row's unique name (<c>zone stem</c>).</param>
/// <param name="Stem">The zone stem.</param>
/// <param name="Sha256">The <c>.zmp</c>'s SHA-256 (the row's key in the expectations).</param>
/// <param name="Size">The <c>.zmp</c>'s byte length.</param>
/// <param name="Cell">The cover cell.</param>
/// <param name="Tags">The cover tags.</param>
/// <param name="Edges">The edge labels this row serves.</param>
/// <param name="Files">Every file of the zone by name.</param>
internal sealed record Cut2ShadowkeyZoneRow(
    string Role,
    string Name,
    string Stem,
    string Sha256,
    long Size,
    string Cell,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Edges,
    IReadOnlyDictionary<string, Cut2ShadowkeyPin> Files);

/// <summary>One decline control: an empty pack slot or a tree file both probes must refuse.</summary>
/// <param name="Name">The row's unique name.</param>
/// <param name="Kind"><c>empty-slot</c> or <c>file</c>.</param>
/// <param name="Slot">The slot, for an empty slot.</param>
/// <param name="Path">The tree-relative path, for a file.</param>
/// <param name="Size">The byte length (0 for an empty slot).</param>
/// <param name="Sha256">The SHA-256.</param>
/// <param name="MeshReason">The Python probe's mesh refusal reason, for a file.</param>
/// <param name="ZoneReason">The Python probe's zone refusal reason, for a file.</param>
internal sealed record Cut2ShadowkeyDeclineRow(
    string Name,
    string Kind,
    int? Slot,
    string? Path,
    long Size,
    string Sha256,
    string? MeshReason,
    string? ZoneReason)
{
    /// <summary>The kind of an empty-slot control.</summary>
    public const string EmptySlotKind = "empty-slot";

    /// <summary>The kind of a tree-file control.</summary>
    public const string FileKind = "file";
}
