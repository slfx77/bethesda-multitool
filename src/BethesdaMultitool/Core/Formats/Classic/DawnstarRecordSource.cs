using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Vfs;

namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>
///     Synthesizes browsable records from a Dawnstar (2004, J2ME) install. The tables live inside
///     <c>datfiles.lmp</c> (read as bytes from the mounted <see cref="IGameFileSystem" /> and
///     parsed in memory, so the JAR and an unpacked directory behave alike) plus the loose
///     <c>npcstrings.dat</c>. Reserved <see cref="ClassicFormIdScheme" /> domains <c>0x44–0x47</c>.
///     <para>
///         <b>Measured 2026-09-05:</b> <c>install.TryReadAllBytes("charin.dat")</c> works on an
///         UNPACKED install — the Dawnstar profile lists <c>datfiles.lmp</c> in its
///         <c>ClassicArchiveGlobs</c> and the mount layers it through <c>LmpArchiveBackend</c> —
///         but NOT on the JAR, whose 20 flat entries include the lump and not its members. So the
///         shared synthesizer tries the mounted path first and opens the lump itself when that
///         comes back empty, and both shapes of install produce the same 482 records.
///     </para>
///     <para>
///         Retail (<c>test_dawnstar_176x208_eng.jar</c>): 7 <c>DCLS</c>, 6 <c>DRCE</c>,
///         14 <c>DSKL</c>, 101 <c>DITM</c>, 25 <c>DSPL</c>, 42 <c>DMON</c>, 5 <c>DSET</c>,
///         43 <c>DDRP</c>, 37 <c>DGEO</c>, 35 <c>DHLP</c> and 167 <c>DNPC</c>. The 37 dungeons are
///         nameless: Dawnstar ships no <c>dungnamesin.dat</c> and its dungeon names are believed to
///         live in code, so <c>DGEO</c> carries the graph alone. Its <c>charin.dat</c> differs from
///         Stormhold's in exactly two governing-attribute bytes, and its <c>spellsin.dat</c>,
///         <c>droppeditemsin.dat</c> and <c>geomin.dat</c> are byte-identical to Stormhold's — so a
///         cross-game diff that reports a difference in those three is a pipeline bug, not content.
///     </para>
/// </summary>
internal static class DawnstarRecordSource
{
    /// <summary>First reserved domain byte for Dawnstar records.</summary>
    public const byte FirstDomain = 0x44;

    /// <summary>Last reserved domain byte for Dawnstar records.</summary>
    public const byte LastDomain = 0x47;

    /// <summary>Letter every Dawnstar record signature starts with.</summary>
    public const string SignaturePrefix = "D";

    /// <summary>The lump holding every table but <c>npcstrings.dat</c>.</summary>
    public const string TableLumpName = "datfiles.lmp";

    /// <summary>Signature for a <c>charin.dat</c> class row.</summary>
    public const string ClassRecordType = SignaturePrefix + TravelsRecordSynthesizer.ClassCode;

    /// <summary>Signature for a <c>charin.dat</c> race.</summary>
    public const string RaceRecordType = SignaturePrefix + TravelsRecordSynthesizer.RaceCode;

    /// <summary>Signature for a <c>charin.dat</c> skill.</summary>
    public const string SkillRecordType = SignaturePrefix + TravelsRecordSynthesizer.SkillCode;

    /// <summary>Signature for an <c>itemsin.dat</c> row.</summary>
    public const string ItemRecordType = SignaturePrefix + TravelsRecordSynthesizer.ItemCode;

    /// <summary>Signature for a <c>spellsin.dat</c> row.</summary>
    public const string SpellRecordType = SignaturePrefix + TravelsRecordSynthesizer.SpellCode;

    /// <summary>Signature for a <c>monstersin.dat</c> row.</summary>
    public const string MonsterRecordType = SignaturePrefix + TravelsRecordSynthesizer.MonsterCode;

    /// <summary>Signature for a <c>monsterfilenamesin.dat</c> sprite family.</summary>
    public const string SpriteRecordType = SignaturePrefix + TravelsRecordSynthesizer.SpriteCode;

    /// <summary>Signature for a <c>droppeditemsin.dat</c> loot row.</summary>
    public const string DropRecordType = SignaturePrefix + TravelsRecordSynthesizer.DropCode;

    /// <summary>Signature for a <c>geomin.dat</c> dungeon.</summary>
    public const string DungeonRecordType = SignaturePrefix + TravelsRecordSynthesizer.DungeonCode;

    /// <summary>Signature for a <c>helptext.dat</c> line.</summary>
    public const string HelpRecordType = SignaturePrefix + TravelsRecordSynthesizer.HelpCode;

    /// <summary>Signature for an <c>npcstrings.dat</c> line.</summary>
    public const string StringRecordType = SignaturePrefix + TravelsRecordSynthesizer.StringCode;

    /// <summary>Reads the mounted install and appends every synthesized record.</summary>
    public static void Populate(IGameFileSystem install, RecordCollection records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();

        TravelsRecordSynthesizer.Populate(
            install, records, SignaturePrefix, FirstDomain, TableLumpName, cancellationToken);
    }
}
