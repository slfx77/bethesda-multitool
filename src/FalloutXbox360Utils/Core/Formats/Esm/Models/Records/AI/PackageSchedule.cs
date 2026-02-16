namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Package schedule from PSDT subrecord (8 bytes).
///     Defines when the AI package is active.
/// </summary>
public record PackageSchedule
{
    /// <summary>Month: -1=Any, 0=January..11=December.</summary>
    public sbyte Month { get; init; }

    /// <summary>Day of week: -1=Any, 0=Sunday..6=Saturday.</summary>
    public sbyte DayOfWeek { get; init; }

    /// <summary>Day of month: 0=Any, 1-31=specific day.</summary>
    public sbyte Date { get; init; }

    /// <summary>Hour of day: -1=Any, 0-23=specific hour.</summary>
    public sbyte Time { get; init; }

    /// <summary>Duration in hours (GECK wiki: "cannot be in less than one hour blocks").</summary>
    public int Duration { get; init; }

    /// <summary>Human-readable month name.</summary>
    public string MonthName => Month switch
    {
        -1 => "Any",
        0 => "January",
        1 => "February",
        2 => "March",
        3 => "April",
        4 => "May",
        5 => "June",
        6 => "July",
        7 => "August",
        8 => "September",
        9 => "October",
        10 => "November",
        11 => "December",
        _ => $"Unknown ({Month})"
    };

    /// <summary>Human-readable day of week name.</summary>
    public string DayOfWeekName => DayOfWeek switch
    {
        -1 => "Any",
        0 => "Sunday",
        1 => "Monday",
        2 => "Tuesday",
        3 => "Wednesday",
        4 => "Thursday",
        5 => "Friday",
        6 => "Saturday",
        _ => $"Unknown ({DayOfWeek})"
    };

    /// <summary>Human-readable schedule summary.</summary>
    public string Summary
    {
        get
        {
            var when = BuildWhenPart();
            var hour12 = Time % 12 == 0 ? 12 : Time % 12;
            var amPm = Time < 12 ? "AM" : "PM";
            var time = Time >= 0 ? $"{hour12}:00 {amPm}" : "Any time";
            var duration = BuildDurationPart();

            return duration.Length > 0
                ? $"{when}, {time} for {duration}"
                : $"{when}, {time}";
        }
    }

    private string BuildWhenPart()
    {
        if (Month == -1 && DayOfWeek == -1 && Date == 0)
        {
            return "Every day";
        }

        if (DayOfWeek >= 0)
        {
            return $"Every {DayOfWeekName}";
        }

        if (Month >= 0 && Date > 0)
        {
            return $"{MonthName} {Date}";
        }

        if (Month >= 0)
        {
            return MonthName;
        }

        if (Date > 0)
        {
            return $"Day {Date}";
        }

        return "Any";
    }

    private string BuildDurationPart()
    {
        if (Duration <= 0)
        {
            return "";
        }

        return Duration == 1 ? "1 hour" : $"{Duration} hours";
    }
}
