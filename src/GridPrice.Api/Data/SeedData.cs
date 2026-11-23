using GridPrice.Api.Domain;
using GridPrice.Api.Ingest;

using Microsoft.EntityFrameworkCore;

namespace GridPrice.Api.Data;

/// <summary>
/// SYNTHETIC demo data: real hub names, invented prices. It covers both 2026 DST changes so the
/// 23- and 25-hour days can be seen through the API. Not market data.
/// </summary>
public static class SeedData
{
    public static readonly PriceNode[] Nodes =
    [
        new() { Id = "HB_HOUSTON", MarketCode = "ERCOT", Name = "Houston Hub", Kind = "Hub" },
        new() { Id = "HB_NORTH", MarketCode = "ERCOT", Name = "North Hub", Kind = "Hub" },
        new() { Id = "HB_WEST", MarketCode = "ERCOT", Name = "West Hub", Kind = "Hub" },
        new() { Id = "LZ_SOUTH", MarketCode = "ERCOT", Name = "South Load Zone", Kind = "Zone" },
        new() { Id = "TH_NP15_GEN-APND", MarketCode = "CAISO", Name = "NP15 Trading Hub", Kind = "Hub" },
        new() { Id = "TH_SP15_GEN-APND", MarketCode = "CAISO", Name = "SP15 Trading Hub", Kind = "Hub" },
        new() { Id = "WESTERN_HUB", MarketCode = "PJM", Name = "PJM Western Hub", Kind = "Hub" },
    ];

    public static async Task EnsureNodesAsync(GridDbContext db, CancellationToken ct = default)
    {
        var existing = await db.Nodes.Select(n => n.Id).ToListAsync(ct);
        foreach (var n in Nodes.Where(n => !existing.Contains(n.Id)))
            db.Nodes.Add(new PriceNode { Id = n.Id, MarketCode = n.MarketCode, Name = n.Name, Kind = n.Kind });
        await db.SaveChangesAsync(ct);
    }

    public static async Task SeedPricesAsync(PriceIngest ingest, CancellationToken ct = default)
    {
        var ercot = Markets.Find("ERCOT")!;
        DateOnly[] days =
        [
            new(2026, 3, 6), new(2026, 3, 7), new(2026, 3, 8), new(2026, 3, 9),
            new(2026, 10, 31), new(2026, 11, 1), new(2026, 11, 2),
        ];
        var rows = new List<PriceRow>();
        foreach (var day in days)
            foreach (var (node, congestionBias) in new[] { ("HB_HOUSTON", 1.25m), ("HB_NORTH", -0.80m), ("HB_WEST", -3.10m) })
                foreach (var (he, repeated) in Hours(ercot, day))
                    rows.Add(Row(node, day, he, repeated, congestionBias));
        await ingest.IngestAsync(ercot, MarketRun.DayAhead, rows, ct);
    }

    /// <summary>Every (hour ending, repeated) label that exists on a local day.</summary>
    public static IEnumerable<(int HourEnding, bool Repeated)> Hours(Market market, DateOnly day)
    {
        for (var he = 1; he <= 24; he++)
        {
            if (MarketClock.IntervalStartUtc(market, day, he, false) is not null) yield return (he, false);
            if (MarketClock.IntervalStartUtc(market, day, he, true) is not null) yield return (he, true);
        }
    }

    private static PriceRow Row(string node, DateOnly day, int he, bool repeated, decimal congestionBias)
    {
        // Night trough, afternoon peak, a little day-to-day variation. Deterministic.
        var shape = he switch { <= 6 => 18m, <= 15 => 28m + he, <= 20 => 55m + 3 * (he - 15), _ => 30m };
        var energy = shape + day.Day % 5 + (repeated ? -0.5m : 0m);
        var congestion = he is >= 14 and <= 20 ? congestionBias * 3 : congestionBias;
        var loss = Math.Round(energy * 0.012m, 5);
        return new PriceRow(node, day, he, repeated, 0, 60, energy + congestion + loss, energy, congestion, loss);
    }
}