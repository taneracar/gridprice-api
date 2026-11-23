using GridPrice.Api.Domain;

using Microsoft.EntityFrameworkCore;

namespace GridPrice.Api.Data;

public static class PriceQueries
{
    /// <summary>
    /// The latest revision of every interval in [startUtc, endUtc) that had been received by
    /// <paramref name="asOfUtc"/>. Runs as one SQL query with NOT EXISTS, on SQLite and SQL Server.
    /// </summary>
    public static IQueryable<PriceRevision> Latest(
        GridDbContext db, IEnumerable<string> nodeIds, MarketRun run, DateTime startUtc, DateTime endUtc, DateTime asOfUtc)
    {
        var ids = nodeIds.ToArray();
        var known = db.Prices.Where(p =>
            ids.Contains(p.NodeId) && p.Run == run &&
            p.IntervalStartUtc >= startUtc && p.IntervalStartUtc < endUtc &&
            p.ReceivedAtUtc <= asOfUtc);

        return known
            .Where(p => !known.Any(o =>
                o.NodeId == p.NodeId && o.Run == p.Run && o.IntervalStartUtc == p.IntervalStartUtc && o.Revision > p.Revision))
            .OrderBy(p => p.NodeId)
            .ThenBy(p => p.IntervalStartUtc)
            .AsNoTracking();
    }
}