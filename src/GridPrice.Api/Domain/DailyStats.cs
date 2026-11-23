namespace GridPrice.Api.Domain;

public sealed record PricePoint(DateTime StartUtc, int Minutes, decimal Lmp);

public sealed record DayStats(
    DateOnly Date,
    int HoursInDay,
    int Intervals,
    int ExpectedIntervals,
    bool Complete,
    decimal? Average,
    decimal? OnPeakAverage,
    decimal? OffPeakAverage,
    decimal? Min,
    decimal? Max,
    int OnPeakHours);

public static class DailyStats
{
    /// <summary>
    /// Per local market day: time-weighted averages (so 5- and 60-minute data weigh by duration),
    /// on-/off-peak split, and whether every interval of the day is present. A day is 23, 24 or 25
    /// hours long, so "expected intervals" is computed per day, never assumed to be 24.
    /// </summary>
    public static IReadOnlyList<DayStats> Compute(Market market, IEnumerable<PricePoint> points, DateOnly from, DateOnly to, int intervalMinutes)
    {
        var byDay = points
            .Select(p => (Point: p, Label: MarketClock.ToLocal(market, p.StartUtc)))
            .GroupBy(x => x.Label.Date)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<DayStats>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var hours = MarketClock.HoursInDay(market, day);
            var expected = hours * 60 / intervalMinutes;
            byDay.TryGetValue(day, out var items);
            items ??= [];

            var on = items.Where(x => PeakCalendar.IsOnPeak(x.Label)).Select(x => x.Point).ToList();
            var off = items.Where(x => !PeakCalendar.IsOnPeak(x.Label)).Select(x => x.Point).ToList();
            var onPeakHours = PeakCalendar.IsPeakDay(day) ? PeakCalendar.LastPeakHourEnding - PeakCalendar.FirstPeakHourEnding + 1 : 0;

            result.Add(new DayStats(
                day,
                hours,
                items.Count,
                expected,
                items.Count == expected,
                Weighted(items.Select(x => x.Point)),
                Weighted(on),
                Weighted(off),
                items.Count > 0 ? items.Min(x => x.Point.Lmp) : null,
                items.Count > 0 ? items.Max(x => x.Point.Lmp) : null,
                onPeakHours));
        }
        return result;
    }

    public static decimal? Weighted(IEnumerable<PricePoint> points)
    {
        decimal sum = 0;
        long minutes = 0;
        foreach (var p in points)
        {
            sum += p.Lmp * p.Minutes;
            minutes += p.Minutes;
        }
        return minutes == 0 ? null : Math.Round(sum / minutes, 5, MidpointRounding.ToEven);
    }
}