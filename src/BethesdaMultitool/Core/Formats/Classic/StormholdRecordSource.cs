using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Vfs;

namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>
///     Synthesizes browsable records from a Stormhold (2003, J2ME) install — the JAR itself or
///     the directory it was unpacked into, both reached through the mounted
///     <see cref="IGameFileSystem" />. Reserved <see cref="ClassicFormIdScheme" /> domains
///     <c>0x40–0x43</c> (docs/handoff_mobile_travels_2026_09_04.md). The per-table synthesizers
///     land with the Stormhold format layer under <c>Core/Formats/Travels/Stormhold/</c>.
///     <para>
///         Stormhold ships its nine data tables LOOSE in the MIDlet JAR, so every one of them is a
///         direct <see cref="IGameFileSystem.TryReadAllBytes" />. Dawnstar packs the same tables
///         into a lump; the shape of the records is identical, which is why both games run through
///         <see cref="TravelsRecordSynthesizer" /> and differ only in this file's three arguments.
///     </para>
///     <para>
///         Retail (<c>test_stormhold_176x208_eng.jar</c>, measured 2026-09-05): 7 <c>SCLS</c>,
///         6 <c>SRCE</c>, 14 <c>SSKL</c>, 109 <c>SITM</c>, 25 <c>SSPL</c>, 41 <c>SMON</c>,
///         5 <c>SSET</c>, 43 <c>SDRP</c>, 37 <c>SGEO</c> and 153 <c>SNPC</c> — 440 records. No
///         <c>SHLP</c>: <c>helptext.dat</c> is Dawnstar's, and <c>dungnamesin.dat</c> — which names
///         Stormhold's 37 dungeons — is Stormhold's.
///     </para>
/// </summary>
internal static class StormholdRecordSource
{
    /// <summary>First reserved domain byte for Stormhold records.</summary>
    public const byte FirstDomain = 0x40;

    /// <summary>Last reserved domain byte for Stormhold records.</summary>
    public const byte LastDomain = 0x43;

    /// <summary>Letter every Stormhold record signature starts with.</summary>
    public const string SignaturePrefix = "S";

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

    /// <summary>Signature for a <c>geomin.dat</c> dungeon, joined with its <c>dungnamesin.dat</c> name.</summary>
    public const string DungeonRecordType = SignaturePrefix + TravelsRecordSynthesizer.DungeonCode;

    /// <summary>Signature for an <c>npcstrings.dat</c> line.</summary>
    public const string StringRecordType = SignaturePrefix + TravelsRecordSynthesizer.StringCode;

    /// <summary>Reads the mounted install and appends every synthesized record.</summary>
    public static void Populate(IGameFileSystem install, RecordCollection records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();

        // No lump: every table is a loose JAR entry.
        TravelsRecordSynthesizer.Populate(install, records, SignaturePrefix, FirstDomain, null, cancellationToken);
    }
}
