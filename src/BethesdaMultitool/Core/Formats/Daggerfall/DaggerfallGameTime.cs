// Calendar constants from daggerfall-unity's DaggerfallDateTime (MIT License),
//   https://github.com/Interkarma/daggerfall-unity — Assets/Scripts/Utility/DaggerfallDateTime.cs
//   (classic epoch 12,566,016,000 s, 30-day months, 12-month years, the month names, the classic
//   start constant 523,530). License texts are collected centrally in THIRD_PARTY_LICENSES.

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>
///     A classic Daggerfall game-clock value — the u32 MINUTE counter the saves carry (SAVEVARS
///     +0x3C9, the AMF header, the character record's time stamps, rumor time limits) — broken
///     into calendar parts.
///     <para>
///         The reference converts <c>minutes</c> as <c>epoch + minutes * 60</c> seconds over a
///         calendar of 60-minute hours, 24-hour days, 30-day months and 12-month years, with the
///         epoch 12,566,016,000 s. That epoch is EXACTLY 404 years of 31,104,000 s, so minute 0 is
///         00:00 on 1 Morning Star 3E404 and the arithmetic below needs no seconds at all.
///     </para>
///     <para>
///         Measured on SAVE0 (2026-09-07): SAVEVARS gameTime 524,043 → 22:03, 4 Morning Star
///         3E405, and <c>isDay</c> at +0x391 is 0 at that hour; the classic start constant
///         523,530 → 13:30 the same day, so the save is 513 game minutes into a new game — which
///         is what a level-1 character with 102 gold standing in Privateer's Hold looks like.
///         ⚠ Only the hour/minute offset is checked against something outside the reference (the
///         isDay flag); the year/month/day names rest on the reference's epoch.
///     </para>
/// </summary>
internal readonly record struct DaggerfallGameTime
{
    /// <summary>Minutes in a game day.</summary>
    public const int MinutesPerDay = 24 * 60;

    /// <summary>Days in every month.</summary>
    public const int DaysPerMonth = 30;

    /// <summary>Months in a year.</summary>
    public const int MonthsPerYear = 12;

    /// <summary>Minutes in a game year (360 days).</summary>
    public const int MinutesPerYear = MinutesPerDay * DaysPerMonth * MonthsPerYear;

    /// <summary>The year minute 0 falls in — the reference epoch is exactly 404 whole years.</summary>
    public const int EpochYear = 404;

    /// <summary>The clock value a new classic game starts at (13:30, 4 Morning Star 3E405).</summary>
    public const uint ClassicStartMinutes = 523_530;

    /// <summary>The twelve month names, in calendar order.</summary>
    public static readonly IReadOnlyList<string> MonthNames =
    [
        "Morning Star", "Sun's Dawn", "First Seed", "Rain's Hand", "Second Seed", "Midyear",
        "Sun's Height", "Last Seed", "Hearthfire", "Frostfall", "Sun's Dusk", "Evening Star"
    ];

    private DaggerfallGameTime(uint minutes, int year, int monthIndex, int day, int hour, int minute)
    {
        Minutes = minutes;
        Year = year;
        MonthIndex = monthIndex;
        Day = day;
        Hour = hour;
        Minute = minute;
    }

    /// <summary>The raw clock value.</summary>
    public uint Minutes { get; }

    /// <summary>Third-Era year (405 for a new game).</summary>
    public int Year { get; }

    /// <summary>0-based month.</summary>
    public int MonthIndex { get; }

    /// <summary>1-based day of the month.</summary>
    public int Day { get; }

    /// <summary>Hour of the day, 0-23.</summary>
    public int Hour { get; }

    /// <summary>Minute of the hour, 0-59.</summary>
    public int Minute { get; }

    /// <summary>The month's name.</summary>
    public string MonthName => MonthNames[MonthIndex];

    /// <summary>Breaks a clock value into calendar parts.</summary>
    public static DaggerfallGameTime FromMinutes(uint minutes)
    {
        var total = (long)EpochYear * MinutesPerYear + minutes;
        var year = (int)(total / MinutesPerYear);
        var ofYear = (int)(total % MinutesPerYear);
        var monthIndex = ofYear / (MinutesPerDay * DaysPerMonth);
        var ofMonth = ofYear % (MinutesPerDay * DaysPerMonth);
        var day = ofMonth / MinutesPerDay + 1;
        var ofDay = ofMonth % MinutesPerDay;
        return new DaggerfallGameTime(minutes, year, monthIndex, day, ofDay / 60, ofDay % 60);
    }

    /// <summary>"22:03, 4 Morning Star 3E405".</summary>
    public override string ToString()
    {
        return $"{Hour:00}:{Minute:00}, {Day} {MonthName} 3E{Year}";
    }
}
