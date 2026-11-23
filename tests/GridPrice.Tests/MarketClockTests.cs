using GridPrice.Api.Domain;

namespace GridPrice.Tests;

public class MarketClockTests
{
    private static readonly Market Ercot = Markets.Find("ERCOT")!; // America/Chicago
    private static readonly Market Caiso = Markets.Find("CAISO")!; // America/Los_Angeles

    private static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public void Ordinary_day_hour_ending_one_starts_at_local_midnight()
    {
        // 2026-06-15 is CDT (UTC-5): HE01 = 00:00-01:00 local = 05:00Z.
        Assert.Equal(Utc(2026, 6, 15, 5), MarketClock.IntervalStartUtc(Ercot, new(2026, 6, 15), 1, false));
        Assert.Equal(Utc(2026, 6, 16, 4), MarketClock.IntervalStartUtc(Ercot, new(2026, 6, 15), 24, false));
        Assert.Equal(24, MarketClock.HoursInDay(Ercot, new(2026, 6, 15)));
    }

    [Fact]
    public void Spring_forward_day_has_23_hours_and_no_hour_ending_three()
    {
        var day = new DateOnly(2026, 3, 8);
        Assert.Equal(23, MarketClock.HoursInDay(Ercot, day));
        Assert.Equal(Utc(2026, 3, 8, 7), MarketClock.IntervalStartUtc(Ercot, day, 2, false)); // 01:00 CST
        Assert.Null(MarketClock.IntervalStartUtc(Ercot, day, 3, false)); // 02:00 local never happens
        Assert.Equal(Utc(2026, 3, 8, 8), MarketClock.IntervalStartUtc(Ercot, day, 4, false)); // 03:00 CDT, one hour after HE02
    }

    [Fact]
    public void Fall_back_day_has_25_hours_and_hour_ending_two_twice()
    {
        var day = new DateOnly(2026, 11, 1);
        Assert.Equal(25, MarketClock.HoursInDay(Ercot, day));
        Assert.Equal(Utc(2026, 11, 1, 6), MarketClock.IntervalStartUtc(Ercot, day, 2, false)); // 01:00 CDT
        Assert.Equal(Utc(2026, 11, 1, 7), MarketClock.IntervalStartUtc(Ercot, day, 2, true)); // 01:00 CST, the repeat
        Assert.Equal(Utc(2026, 11, 1, 8), MarketClock.IntervalStartUtc(Ercot, day, 3, false));
    }

    [Fact]
    public void Repeated_flag_on_an_ordinary_hour_is_rejected()
    {
        Assert.Null(MarketClock.IntervalStartUtc(Ercot, new(2026, 11, 1), 3, true));
        Assert.Null(MarketClock.IntervalStartUtc(Ercot, new(2026, 6, 15), 2, true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    public void Hour_ending_outside_1_to_24_is_rejected(int he) =>
        Assert.Null(MarketClock.IntervalStartUtc(Ercot, new(2026, 6, 15), he, false));

    [Fact]
    public void Every_interval_of_the_fall_back_day_round_trips_to_the_same_label()
    {
        var day = new DateOnly(2026, 11, 1);
        var (start, end) = MarketClock.DayRangeUtc(Ercot, day, day);
        var labels = new List<(int, bool)>();
        for (var t = start; t < end; t = t.AddMinutes(5))
        {
            var l = MarketClock.ToLocal(Ercot, t);
            Assert.Equal(day, l.Date);
            Assert.Equal(t, MarketClock.IntervalStartUtc(Ercot, l.Date, l.HourEnding, l.RepeatedHour, l.Minute));
            if (l.Minute == 0) labels.Add((l.HourEnding, l.RepeatedHour));
        }
        Assert.Equal(25, labels.Count);
        Assert.Contains((2, true), labels);
        Assert.Single(labels, x => x == (2, false));
    }

    [Fact]
    public void Same_utc_instant_has_different_labels_in_different_markets()
    {
        // 2026-03-08 09:00Z: Chicago already on CDT (04:00 -> HE05), Los Angeles still on PST (01:00 -> HE02).
        var t = Utc(2026, 3, 8, 9);
        Assert.Equal(5, MarketClock.ToLocal(Ercot, t).HourEnding);
        Assert.Equal(2, MarketClock.ToLocal(Caiso, t).HourEnding);
    }

    [Fact]
    public void Local_start_carries_the_offset_in_force()
    {
        var day = new DateOnly(2026, 11, 1);
        var first = MarketClock.ToLocal(Ercot, MarketClock.IntervalStartUtc(Ercot, day, 2, false)!.Value);
        var repeat = MarketClock.ToLocal(Ercot, MarketClock.IntervalStartUtc(Ercot, day, 2, true)!.Value);
        Assert.Equal(TimeSpan.FromHours(-5), first.LocalStart.Offset);
        Assert.Equal(TimeSpan.FromHours(-6), repeat.LocalStart.Offset);
        Assert.False(first.RepeatedHour);
        Assert.True(repeat.RepeatedHour);
    }
}