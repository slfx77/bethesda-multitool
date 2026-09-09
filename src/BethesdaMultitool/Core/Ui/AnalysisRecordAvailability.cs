using BethesdaMultitool.Core.Analysis;

namespace BethesdaMultitool.Core.Ui;

/// <summary>
///     Whether an analyzed source has records the record-backed sub-tabs can browse.
///     <para>
///         This exists because the session's <c>HasEsmRecords</c> answers a narrower question than
///         most of its call sites were asking. It is true only when an ESM byte-scan produced an
///         <c>EsmRecordScanResult</c> — a stream of offset-addressed record descriptors. A classic
///         install has no such stream: <c>ClassicGameAnalyzer</c> synthesizes a finished
///         <c>RecordCollection</c> without scanning any bytes, so <c>EsmRecords</c> is null by
///         construction and every tab gated on it stayed empty for all nine pre-plugin games.
///     </para>
///     <para>
///         ⛔ The tempting fix — assigning a synthetic <c>EsmRecords</c> for classic sources — is
///         wrong and was rejected. Consumers of that property read each record's <c>Offset</c> to
///         seek into a mapped file, and a synthesized record has none; worse, the NPC browser
///         treats a non-null value as permission to go looking for a meshes archive beside the
///         source, which for an install directory means hunting for something that cannot be there.
///         Two questions need two names, which is what this supplies.
///     </para>
///     <para>
///         In <c>Core/</c> for the same reason as <see cref="AnalysisSubTabPolicy" />: <c>App/</c>
///         is excluded from the <c>net10.0</c> target framework, so a predicate kept beside the tab
///         could only be covered by source-text pins rather than behavioural tests.
///     </para>
/// </summary>
public static class AnalysisRecordAvailability
{
    /// <summary>
    ///     Whether the summary, record and dialogue tabs can be populated for this source.
    ///     <paramref name="hasEsmRecords" /> is the session's own scan result; a classic install
    ///     qualifies on its file type alone, because its analyzer always produces a collection.
    /// </summary>
    /// <remarks>
    ///     A classic source whose load failed still answers true, and that is deliberate: each
    ///     populate path re-checks its own data and degrades to a "nothing to show" message. Gating
    ///     on the file type keeps a failure visible in the tab that owns it, rather than hiding
    ///     every tab and leaving the window looking like it loaded nothing at all.
    /// </remarks>
    public static bool SupportsRecordBrowsing(AnalysisFileType fileType, bool hasEsmRecords)
    {
        return hasEsmRecords || fileType == AnalysisFileType.ClassicGameData;
    }
}
