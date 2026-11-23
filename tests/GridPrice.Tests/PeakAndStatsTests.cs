using GridPrice.Api.Domain;

namespace GridPrice.Tests;

public class PeakCalendarTests
{
    [Theory]
    [InlineData("2026-01-01")] // Thursday
    [InlineData("2026-05-25")] // last Monday of May
    [InlineData("2026-09-07")] // first Monday of September
    [InlineData("2026-11-26")] // fourth Thursday of November
    [InlineData("2026-12-25")] // Friday
    [InlineData("2022-12-26")] // Christmas 2022 was a Sunday: observed Monday
    [InlineData("2023-01-02")] // New Year 2023 was a Sunday: observed Monday
    public void Nerc_holidays(string date) => Assert.True(PeakCalendar.IsNercHoliday(DateOnly.Parse(date)));

    [Theory]
    [InlineData("2026-07-03")] // July 4, 2026 is a Saturday; NERC does not move it to Friday
    [InlineData("2026-11-27")] // the day after Thanksgiving is not a NERC holiday
    [InlineData("2026-05-18")]
    public void Not_nerc_holidays(string date) => Assert.False(PeakCalendar.IsNercHoliday(DateOnly.Parse(date)));

    [Fact]
    public void Each_year_has_six_holidays() => Assert.Equal(6, PeakCalendar.NercHolidays(2026).Count);

    [Theory]
    [InlineData("2026-06-16", 6, false)] // HE06 is off-peak
    [InlineData("2026-06-16", 7, true)]
    [InlineData("2026-06-16", 22, true)]
    [InlineData("2026-06-16", 23, false)]
    [InlineData("2026-06-20", 15, false)] // Saturday
    [InlineData("2026-11-26", 15, false)] // Thanksgiving
    public void On_peak_is_he07_to_he22_on_weekdays(string date, int he, bool onPeak) =>
        Assert.Equal(onPeak, PeakCalendar.IsOnPeak(new LocalLabel(DateOnly.Parse(date), he, 0, false, default)));
}

public class DailyStatsTests
{
    private static readonly Market Ercot = Markets.Find("ERCOT")!;

    private static IEnumerable<PricePoint> Hourly(DateOnly day, Func<int, decimal> price)
    {
        var (start, end) = MarketClock.DayRangeUtc(Ercot, day, day);
        var i = 0;
        for (var t = start; t < end; t = t.AddHours(1)) yield return new PricePoint(t, 60, price(++i));
    }

    [Fact]
    public void Fall_back_day_expects_25_hourly_intervals()
    {
        var day = new DateOnly(2026, 11, 1);
        var stats = DailyStats.Compute(Ercot, Hourly(day, _ => 20m), day, day, 60).Single();
        Assert.Equal(25, stats.HoursInDay);
        Assert.Equal(25, stats.ExpectedIntervals);
        Assert.Equal(25, stats.Intervals);
        Assert.True(stats.Complete);
        Assert.Equal(20m, stats.Average);
    }

    [Fact]
    public void Spring_forward_day_is_complete_with_23_hours()
    {
        var day = new DateOnly(2026, 3, 8);
        var stats = DailyStats.Compute(Ercot, Hourly(day, _ => 1m), day, day, 60).Single();
        Assert.Equal(23, stats.ExpectedIntervals);
        Assert.True(stats.Complete);
    }

    [Fact]
    public void Missing_interval_marks_the_day_incomplete_and_empty_days_are_still_listed()
    {
        var day = new DateOnly(2026, 6, 16);
        var stats = DailyStats.Compute(Ercot, Hourly(day, _ => 1m).Skip(1), day, day.AddDays(1), 60);
        Assert.False(stats[0].Complete);
        Assert.Equal(23, stats[0].Intervals);
        Assert.Equal(0, stats[1].Intervals);
        Assert.Null(stats[1].Average);
    }

    [Fact]
    public void Peak_and_off_peak_averages_split_by_hour_ending()
    {
        var day = new DateOnly(2026, 6, 16); // Tuesday
        // Price = hour ending: peak HE07..22 averages 14.5; off-peak HE01..06 and HE23..24 averages (21 + 47) / 8 = 8.5.
        var stats = DailyStats.Compute(Ercot, Hourly(day, he => he), day, day, 60).Single();
        Assert.Equal(14.5m, stats.OnPeakAverage);
        Assert.Equal(8.5m, stats.OffPeakAverage);
        Assert.Equal(12.5m, stats.Average);
        Assert.Equal(1m, stats.Min);
        Assert.Equal(24m, stats.Max);
        Assert.Equal(16, stats.OnPeakHours);
    }

    [Fact]
    public void Average_is_weighted_by_interval_length()
    {
        var t = new DateTime(2026, 6, 16, 5, 0, 0, DateTimeKind.Utc);
        var avg = DailyStats.Weighted([new PricePoint(t, 60, 10m), new PricePoint(t.AddHours(1), 5, 130m)]);
        Assert.Equal(Math.Round((10m * 60 + 130m * 5) / 65, 5), avg);
    }

    [Fact]
    public void Rounds_half_to_even_at_five_decimals()
    {
        var t = new DateTime(2026, 6, 16, 5, 0, 0, DateTimeKind.Utc);
        // (0.00001 + 0.00002) / 2 = 0.000015 -> 0.00002 (even); 0.000025 -> 0.00002.
        Assert.Equal(0.00002m, DailyStats.Weighted([new PricePoint(t, 60, 0.00001m), new PricePoint(t, 60, 0.00002m)]));
        Assert.Equal(0.00002m, DailyStats.Weighted([new PricePoint(t, 60, 0.00002m), new PricePoint(t, 60, 0.00003m)]));
    }
}