namespace GridPrice.Api.Domain;

/// <summary>
/// Standard eastern/western on-peak definition used for power trading: hour ending 07 through 22
/// (local 06:00-22:00), Monday to Friday, excluding NERC holidays. Everything else is off-peak.
/// </summary>
public static class PeakCalendar
{
    public const int FirstPeakHourEnding = 7;
    public const int LastPeakHourEnding = 22;

    public static bool IsOnPeak(LocalLabel label) =>
        label.HourEnding is >= FirstPeakHourEnding and <= LastPeakHourEnding && IsPeakDay(label.Date);

    public static bool IsPeakDay(DateOnly day) =>
        day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !IsNercHoliday(day);

    /// <summary>
    /// NERC holidays: New Year's Day, Memorial Day, Independence Day, Labor Day, Thanksgiving and
    /// Christmas. A fixed-date holiday that falls on a Sunday is observed on Monday; one that falls
    /// on a Saturday is not moved (the Saturday is already off-peak).
    /// </summary>
    public static bool IsNercHoliday(DateOnly day) => NercHolidays(day.Year).Contains(day);

    public static IReadOnlySet<DateOnly> NercHolidays(int year)
    {
        var set = new HashSet<DateOnly>
        {
            Observed(new DateOnly(year, 1, 1)),
            LastWeekday(year, 5, DayOfWeek.Monday), // Memorial Day
            Observed(new DateOnly(year, 7, 4)),
            NthWeekday(year, 9, DayOfWeek.Monday, 1), // Labor Day
            NthWeekday(year, 11, DayOfWeek.Thursday, 4), // Thanksgiving
            Observed(new DateOnly(year, 12, 25)),
        };
        return set;
    }

    private static DateOnly Observed(DateOnly d) => d.DayOfWeek == DayOfWeek.Sunday ? d.AddDays(1) : d;

    private static DateOnly NthWeekday(int year, int month, DayOfWeek dow, int n)
    {
        var first = new DateOnly(year, month, 1);
        var offset = ((int)dow - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + 7 * (n - 1));
    }

    private static DateOnly LastWeekday(int year, int month, DayOfWeek dow)
    {
        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        var back = ((int)last.DayOfWeek - (int)dow + 7) % 7;
        return last.AddDays(-back);
    }
}