using System.Collections.Immutable;

namespace BethesdaMultitool.Core.Formats.Travels;

/// <summary>
///     <c>charin.dat</c> — the character-creation tables of the J2ME Elder Scrolls Travels games:
///     the on-screen labels, the class roster and each class's starting attributes and skills.
///     Original RE from the bytes (2026-09-05).
///     <para>
///         Layout (big-endian, Java stream semantics — see <see cref="TravelsDataReader" />):
///     </para>
///     <list type="number">
///         <item>
///             five counted string lists — stat labels (10), attribute labels (16: each of the
///             8 attributes followed by its "&lt;Attr&gt; Increases" counter), class names (7), race
///             names (6) and skill names (14);
///         </item>
///         <item>
///             <see cref="SkillCount" /> shorts, one per skill: an EVEN index into the attribute
///             label list, i.e. the attribute that governs the skill;
///         </item>
///         <item>one <see cref="ClassRowLength" />-short row per class name, row-major.</item>
///     </list>
///     <para>
///         The row width is <c>13 + 2 x skills</c>, so the file is only self-describing once the
///         skill count is known — and the engine refuses any count but 14, which is why
///         <see cref="SkillCount" /> is enforced here rather than taken from the file.
///     </para>
///     <para>
///         Retail census (both games, 1,260 bytes each): 10/16/7/6/14 strings occupying 658 bytes,
///         28 bytes of governing attributes and 7 x 41 x 2 = 574 bytes of class rows — 1,260
///         exactly. The two files differ in exactly TWO bytes, both in the governing-attribute
///         table (entries 1 and 10, Stormhold 4 where Dawnstar has 2, at payload offsets 661 and
///         679); every string and every class row is identical between the games.
///     </para>
/// </summary>
internal sealed record TravelsCharacterTable(
    ImmutableArray<string> StatLabels,
    ImmutableArray<string> AttributeLabels,
    ImmutableArray<string> ClassNames,
    ImmutableArray<string> RaceNames,
    ImmutableArray<string> SkillNames,
    ImmutableArray<short> GoverningAttributes,
    ImmutableArray<TravelsClassRow> Classes)
{
    /// <summary>Skills the engine hard-asserts; the class row width is derived from it.</summary>
    public const int SkillCount = 14;

    /// <summary>Attributes carried in a class row, columns 2..9.</summary>
    public const int AttributeCount = 8;

    /// <summary>Shorts before the per-skill pairs in a class row.</summary>
    public const int ClassRowPrefixLength = 13;

    /// <summary>Shorts in a class row: 13 fixed columns plus a rank/percentage pair per skill.</summary>
    public const int ClassRowLength = ClassRowPrefixLength + 2 * SkillCount;

    /// <summary>String lists at the head of the file, in order.</summary>
    public const int StringListCount = 5;

    /// <summary>
    ///     Parses <c>charin.dat</c>, throwing <see cref="InvalidDataException" /> — naming the file
    ///     and the offending byte position — when the layout does not tile the payload exactly.
    /// </summary>
    public static TravelsCharacterTable Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var reader = new TravelsDataReader(bytes, name);
        var statLabels = reader.ReadUtfList16();
        var attributeLabels = reader.ReadUtfList16();
        var classNames = reader.ReadUtfList16();
        var raceNames = reader.ReadUtfList16();

        var skillListStart = reader.Position;
        var skillNames = reader.ReadUtfList16();
        if (skillNames.Length != SkillCount)
        {
            throw new InvalidDataException(
                $"'{name}': the skill list at byte {skillListStart} holds {skillNames.Length} names; "
                + $"the engine requires exactly {SkillCount} and sizes the class row from it.");
        }

        var governing = ImmutableArray.CreateBuilder<short>(SkillCount);
        for (var i = 0; i < SkillCount; i++)
        {
            governing.Add(reader.ReadInt16());
        }

        var classes = ImmutableArray.CreateBuilder<TravelsClassRow>(classNames.Length);
        for (var i = 0; i < classNames.Length; i++)
        {
            classes.Add(ReadClassRow(ref reader, i, classNames[i]));
        }

        reader.ExpectEnd();

        return new TravelsCharacterTable(
            statLabels,
            attributeLabels,
            classNames,
            raceNames,
            skillNames,
            governing.MoveToImmutable(),
            classes.MoveToImmutable());
    }

    private static TravelsClassRow ReadClassRow(ref TravelsDataReader reader, int index, string className)
    {
        // Column 0 repeats the row index; it is kept as data (exposed as Index) rather than
        // enforced, so a build that renumbers its classes still parses and can be diffed.
        reader.ReadInt16();
        var defaultRace = reader.ReadInt16();

        var attributes = ImmutableArray.CreateBuilder<short>(AttributeCount);
        for (var a = 0; a < AttributeCount; a++)
        {
            attributes.Add(reader.ReadInt16());
        }

        var magicka = reader.ReadInt16();
        var field11 = reader.ReadInt16();
        var field12 = reader.ReadInt16();

        var skills = ImmutableArray.CreateBuilder<TravelsClassSkill>(SkillCount);
        for (var s = 0; s < SkillCount; s++)
        {
            var rank = reader.ReadInt16();
            var percent = reader.ReadInt16();
            skills.Add(new TravelsClassSkill(rank, percent));
        }

        return new TravelsClassRow(
            index,
            className,
            defaultRace,
            attributes.MoveToImmutable(),
            magicka,
            field11,
            field12,
            skills.MoveToImmutable());
    }
}
