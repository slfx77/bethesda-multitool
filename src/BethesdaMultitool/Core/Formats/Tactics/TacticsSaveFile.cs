namespace BethesdaMultitool.Core.Formats.Tactics;

/// <summary>One archived file inside a save: its virtual path and its bytes, sliced without copying.</summary>
internal readonly record struct TacticsSaveEntry(string Path, ReadOnlyMemory<byte> Bytes);

/// <summary>
///     A Fallout Tactics save (<c>core\user\save\*.sav</c>). Original RE 2026-09-07 on
///     <c>Snake.sav</c> (273,875 B, written by the user that day); every Tactics reference is GPL,
///     so nothing is ported — fot-save-edit's README was read for prose and documents nothing
///     beyond "ESH = entity property values".
///     <para>
///         ⚑⚑
///         <b>
///             A save is NOT one mission container — it is an ARCHIVE of the game's
///             <c>user/$$current$$</c> directory
///         </b>
///         , which is empty on disk after the game exits.
///         Outer layout: a <c>&lt;saveh&gt;</c> v2 header (<see cref="TacticsSaveHeader" />: u8, five
///         wide strings, eight <c>&lt;zar&gt;</c> slots, six floats — 65,019 B on the fixture), then
///         <c>&lt;campaign_save&gt;</c> v1: u32 count, and per entry a wide path, a u32 byte length
///         and that many bytes of a whole file. Two entries on the fixture:
///         <c>user/$$current$$/mission01.sav</c> (104,330 B — <see cref="TacticsMissionSnapshot" />, a
///         second <c>&lt;saveh&gt;</c> plus a <c>&lt;world&gt;</c> v70 container inflating to exactly
///         2,607,657 B) and <c>user/$$current$$/save.cam</c> (104,378 B —
///         <see cref="TacticsCampaignState" />, a <c>&lt;campaign&gt;</c> v21 that tiles to its last
///         byte). <b>The entries sum to EOF: 273,875/273,875.</b>
///     </para>
///     <para>
///         ⚑ Oracles from OUTSIDE the file: the save name equals the file stem (<c>Snake</c>); the
///         mission display name <c>Brahmin Wood</c> pairs with the snapshot's mission path
///         <c>campaigns/missions/core/mission01.mis</c>, an entry of <c>mis-core_A.bos</c> whose
///         nine <c>&lt;Team&gt;</c> chunks equal the save's by name and flag; the snapshot's
///         1,732-character briefing is the <c>MISSION_BRIEF</c> block of the speech file its own
///         header names in <c>loc-mis_A.bos</c> (⚠ once the shipped file's literal <c>\n</c> escapes
///         and line wrapping are normalised — not byte-for-byte, see
///         <see cref="TacticsMissionSnapshot" />); the campaign's 65 x 34 grid equals retail
///         <c>bos.cam</c>'s header; and the 280x165
///         <c>&lt;zar&gt;</c> renders as the Brahmin Wood scene.
///     </para>
///     <para>
///         ⚠ The backlog's "saves share the MIS world codec" is HALF right — the <c>&lt;world&gt;</c>
///         container is reused but its payload is not a mission's (see
///         <see cref="TacticsMissionSnapshot" />). ⚠ One fixture: whether a later save carries MORE
///         entries (one snapshot per visited mission is plausible from the <c>mission01.sav</c>
///         naming) is untested, so the entry list is exposed as read and the two known kinds are
///         routed by CONTENT, never by index.
///     </para>
/// </summary>
internal sealed class TacticsSaveFile
{
    /// <summary>The archive directory's tag.</summary>
    public const string ArchiveTag = "campaign_save";

    /// <summary>The only archive version measured.</summary>
    public const string ArchiveVersion = "1";

    private TacticsSaveFile(
        string name, TacticsSaveHeader header, IReadOnlyList<TacticsSaveEntry> entries,
        TacticsMissionSnapshot? snapshot, string snapshotError, TacticsCampaignState? campaign, string campaignError)
    {
        Name = name;
        Header = header;
        Entries = entries;
        Snapshot = snapshot;
        SnapshotError = snapshotError;
        Campaign = campaign;
        CampaignError = campaignError;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The outer <c>&lt;saveh&gt;</c>: title, save name, mission name, game time, images.</summary>
    public TacticsSaveHeader Header { get; }

    /// <summary>Every archived file, in directory order; the two known kinds are also parsed below.</summary>
    public IReadOnlyList<TacticsSaveEntry> Entries { get; }

    /// <summary>The first entry that is a <c>&lt;saveh&gt;</c> + <c>&lt;world&gt;</c> snapshot and tiled; null otherwise.</summary>
    public TacticsMissionSnapshot? Snapshot { get; }

    /// <summary>Why <see cref="Snapshot" /> is null — empty when it is not.</summary>
    public string SnapshotError { get; }

    /// <summary>The first entry that is a <c>&lt;campaign&gt;</c> and tiled to its last byte; null otherwise.</summary>
    public TacticsCampaignState? Campaign { get; }

    /// <summary>Why <see cref="Campaign" /> is null — empty when it is not.</summary>
    public string CampaignError { get; }

    /// <summary>Content probe: the outer header's framing.</summary>
    public static bool IsSave(ReadOnlySpan<byte> bytes)
    {
        return TacticsSaveHeader.IsSaveHeader(bytes);
    }

    /// <summary>Parses a save, throwing unless the header and the archive directory tile to EOF.</summary>
    public static TacticsSaveFile Parse(ReadOnlyMemory<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var cursor = new TacticsCursor(bytes, name);
        var header = TacticsSaveHeader.Read(cursor);

        var directory = cursor.Tag(ArchiveTag);
        if (!string.Equals(directory.Version, ArchiveVersion, StringComparison.Ordinal))
        {
            throw cursor.Fail(cursor.Position,
                $"<{ArchiveTag}> version '{directory.Version}' is not the measured '{ArchiveVersion}'");
        }

        var count = cursor.Count(1024, "archived file");
        var entries = new List<TacticsSaveEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var path = cursor.WideString();
            var length = cursor.Count(int.MaxValue, $"entry '{path}' byte");
            entries.Add(new TacticsSaveEntry(path, cursor.Bytes(length, $"entry '{path}'")));
        }

        // The directory must account for every byte: that is what makes it an archive and not a
        // header followed by unknown data.
        cursor.RequireEnd($"<{ArchiveTag}> directory");

        TacticsMissionSnapshot? snapshot = null;
        var snapshotError = "no entry opens with a <saveh> header";
        TacticsCampaignState? campaign = null;
        var campaignError = "no entry opens with a <campaign> tag";

        foreach (var entry in entries)
        {
            var entryName = $"{name}:{entry.Path}";
            if (snapshot is null && TacticsMissionSnapshot.IsSnapshot(entry.Bytes.Span))
            {
                if (!TacticsMissionSnapshot.TryParse(entry.Bytes, entryName, out snapshot, out snapshotError))
                {
                    snapshot = null;
                }
            }
            else if (campaign is null && TacticsCampaignState.IsCampaign(entry.Bytes.Span)
                     && !TacticsCampaignState.TryParse(entry.Bytes, entryName, out campaign, out campaignError))
            {
                campaign = null;
            }
        }

        return new TacticsSaveFile(
            name, header, entries,
            snapshot, snapshot is null ? snapshotError : string.Empty,
            campaign, campaign is null ? campaignError : string.Empty);
    }

    /// <summary>Parses a save, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlyMemory<byte> bytes, string name, out TacticsSaveFile save, out string error)
    {
        try
        {
            save = Parse(bytes, name);
            error = string.Empty;
            return true;
        }
        catch (InvalidDataException e)
        {
            save = null!;
            error = e.Message;
            return false;
        }
    }
}
