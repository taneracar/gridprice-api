namespace GridPrice.Api.Domain;

/// <summary>
/// Converts between market-local "hour ending" labels and UTC instants.
///
/// Power markets label hours by when they END in local prevailing time: HE01 is 00:00-01:00.
/// On the spring-forward day the local hour 02:00-03:00 does not exist, so HE03 is missing and the
/// day has 23 hours. On the fall-back day 01:00-02:00 happens twice; the second one is HE02 with
/// the repeated-hour (DST) flag, and the day has 25 hours.
/// </summary>
public static class MarketClock
{
    /// <summary>
    /// UTC start of the interval that begins <paramref name="minute"/> minutes into the given
    /// hour-ending. Returns null when that local time does not exist, or when
    /// <paramref name="repeatedHour"/> is set for an hour that is not repeated.
    /// </summary>
    public static DateTime? IntervalStartUtc(Market market, DateOnly localDate, int hourEnding, bool repeatedHour, int minute = 0)
    {
        if (hourEnding is < 1 or > 24 || minute is < 0 or > 59)
            return null;

        var tz = market.TimeZone;
        var local = localDate.ToDateTime(new TimeOnly(hourEnding - 1, minute), DateTimeKind.Unspecified);

        if (tz.IsInvalidTime(local))
            return null; // the skipped hour on the spring-forward day

        if (tz.IsAmbiguousTime(local))
        {
            // Two offsets are valid. The first pass uses the daylight offset (the larger one).
            var offsets = tz.GetAmbiguousTimeOffsets(local).OrderByDescending(o => o).ToArray();
            var offset = repeatedHour ? offsets[^1] : offsets[0];
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }

        if (repeatedHour)
            return null; // only an ambiguous hour can be the repeated one

        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }

    /// <summary>Local label of a UTC interval start: date, hour ending, minute, repeated flag.</summary>
    public static LocalLabel ToLocal(Market market, DateTime utcStart)
    {
        var utc = DateTime.SpecifyKind(utcStart, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, market.TimeZone);
        var offset = market.TimeZone.GetUtcOffset(utc);
        // The second pass through an ambiguous local hour is the one on the smaller (standard) offset.
        var repeated = market.TimeZone.IsAmbiguousTime(local)
            && offset == market.TimeZone.GetAmbiguousTimeOffsets(local).Min();
        return new LocalLabel(DateOnly.FromDateTime(local), local.Hour + 1, local.Minute, repeated,
            new DateTimeOffset(local, offset));
    }

    /// <summary>UTC range [start, end) covering whole local days from..to (inclusive).</summary>
    public static (DateTime StartUtc, DateTime EndUtc) DayRangeUtc(Market market, DateOnly from, DateOnly to)
    {
        var start = TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), market.TimeZone);
        var end = TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), market.TimeZone);
        return (start, end);
    }

    /// <summary>Number of hours in a local market day: 23, 24 or 25.</summary>
    public static int HoursInDay(Market market, DateOnly day)
    {
        var (start, end) = DayRangeUtc(market, day, day);
        return (int)(end - start).TotalHours;
    }
}

public sealed record LocalLabel(DateOnly Date, int HourEnding, int Minute, bool RepeatedHour, DateTimeOffset LocalStart);