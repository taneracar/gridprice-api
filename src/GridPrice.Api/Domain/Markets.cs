namespace GridPrice.Api.Domain;

/// <summary>Which auction a price comes from.</summary>
public enum MarketRun
{
    DayAhead,
    RealTime,
}

/// <summary>
/// An ISO/RTO. Prices are published in the market's local prevailing time, so every market
/// carries its IANA time zone; all storage is in UTC.
/// </summary>
public sealed record Market(string Code, string Name, string TimeZoneId)
{
    public TimeZoneInfo TimeZone { get; } = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
}

public static class Markets
{
    public static readonly IReadOnlyList<Market> All =
    [
        new("ERCOT", "Electric Reliability Council of Texas", "America/Chicago"),
        new("CAISO", "California ISO", "America/Los_Angeles"),
        new("PJM", "PJM Interconnection", "America/New_York"),
    ];

    public static Market? Find(string code) =>
        All.FirstOrDefault(m => string.Equals(m.Code, code, StringComparison.OrdinalIgnoreCase));

    public static bool TryParseRun(string? value, out MarketRun run)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case "DA" or "DAM" or "DAYAHEAD":
                run = MarketRun.DayAhead;
                return true;
            case "RT" or "RTM" or "REALTIME":
                run = MarketRun.RealTime;
                return true;
            default:
                run = default;
                return false;
        }
    }

    public static string Code(this MarketRun run) => run == MarketRun.DayAhead ? "DA" : "RT";
}