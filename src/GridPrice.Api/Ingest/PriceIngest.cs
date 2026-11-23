using GridPrice.Api.Data;
using GridPrice.Api.Domain;

using Microsoft.EntityFrameworkCore;

namespace GridPrice.Api.Ingest;

/// <summary>One published price, labelled the way ISOs publish it: local date + hour ending.</summary>
public sealed record PriceRow(
    string Node,
    DateOnly Date,
    int HourEnding,
    bool RepeatedHour,
    int Minute,
    int? IntervalMinutes,
    decimal Lmp,
    decimal Energy,
    decimal Congestion,
    decimal Loss);

public sealed record IngestResult(int Received, int Inserted, int Revised, int Unchanged);

public sealed class IngestRejected(IReadOnlyDictionary<string, string[]> errors) : Exception("Batch rejected")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

public sealed class IngestConflict() : Exception("Another ingest wrote the same intervals at the same time; retry the batch.");

/// <summary>
/// Validates a whole batch, then writes it in one transaction: all rows or none. Re-sending the
/// same values is a no-op (idempotent); different values for a known interval become a new revision.
/// </summary>
public sealed class PriceIngest(GridDbContext db, TimeProvider clock)
{
    /// <summary>Components may differ from the LMP by this much, because ISOs round each one separately.</summary>
    public const decimal ComponentTolerance = 0.0001m;
    public const decimal MinPrice = -2_000m;
    public const decimal MaxPrice = 10_000m;
    public const int MaxRows = 50_000;

    public async Task<IngestResult> IngestAsync(Market market, MarketRun run, IReadOnlyList<PriceRow> rows, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, List<string>>();
        void Error(int i, string field, string message)
        {
            var key = $"rows[{i}].{field}";
            if (!errors.TryGetValue(key, out var list)) errors[key] = list = [];
            list.Add(message);
        }

        if (rows.Count == 0)
            throw new IngestRejected(new Dictionary<string, string[]> { ["rows"] = ["The batch is empty."] });
        if (rows.Count > MaxRows)
            throw new IngestRejected(new Dictionary<string, string[]> { ["rows"] = [$"At most {MaxRows} rows per batch."] });

        var nodeIds = rows.Select(r => r.Node).Distinct(StringComparer.Ordinal).ToArray();
        var knownNodes = (await db.Nodes.Where(n => n.MarketCode == market.Code && nodeIds.Contains(n.Id)).Select(n => n.Id).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var expectedMinutes = run == MarketRun.DayAhead ? 60 : 5;
        var parsed = new List<(PriceRow Row, DateTime StartUtc, int Minutes)>(rows.Count);
        var seen = new Dictionary<(string, DateTime), int>();

        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var ok = true;
            if (!knownNodes.Contains(r.Node)) { Error(i, "node", $"Unknown {market.Code} node '{r.Node}'."); ok = false; }

            var minutes = r.IntervalMinutes ?? expectedMinutes;
            if (minutes != expectedMinutes) { Error(i, "intervalMinutes", $"{run.Code()} prices are {expectedMinutes}-minute intervals."); ok = false; }
            if (r.Minute < 0 || r.Minute >= 60 || r.Minute % expectedMinutes != 0) { Error(i, "minute", $"Minute must be a multiple of {expectedMinutes} below 60."); ok = false; }

            DateTime? start = null;
            if (r.HourEnding is < 1 or > 24)
            {
                Error(i, "hourEnding", "Hour ending must be 1 to 24."); ok = false;
            }
            else if (r.Minute is >= 0 and < 60)
            {
                start = MarketClock.IntervalStartUtc(market, r.Date, r.HourEnding, r.RepeatedHour, r.Minute);
                if (start is null)
                {
                    Error(i, "hourEnding", r.RepeatedHour
                        ? $"HE{r.HourEnding:00} on {r.Date:yyyy-MM-dd} is not a repeated hour in {market.TimeZoneId}."
                        : $"HE{r.HourEnding:00} does not exist on {r.Date:yyyy-MM-dd} in {market.TimeZoneId} (clocks moved forward).");
                    ok = false;
                }
            }

            foreach (var (name, value) in new[] { ("lmp", r.Lmp), ("energy", r.Energy), ("congestion", r.Congestion), ("loss", r.Loss) })
            {
                // The column is decimal(12,5). A sixth decimal would be rounded silently by the database.
                if (value.Scale > 5 && value != Math.Round(value, 5)) { Error(i, name, "At most 5 decimal places."); ok = false; }
            }
            if (r.Lmp < MinPrice || r.Lmp > MaxPrice) { Error(i, "lmp", $"LMP must be between {MinPrice} and {MaxPrice} $/MWh."); ok = false; }
            if (Math.Abs(r.Energy + r.Congestion + r.Loss - r.Lmp) > ComponentTolerance)
            {
                Error(i, "lmp", $"Energy + congestion + loss = {r.Energy + r.Congestion + r.Loss} but LMP = {r.Lmp}."); ok = false;
            }

            if (start is { } s)
            {
                if (seen.TryGetValue((r.Node, s), out var first)) { Error(i, "hourEnding", $"Same node and interval as rows[{first}]."); ok = false; }
                else seen[(r.Node, s)] = i;
            }

            if (ok) parsed.Add((r, start!.Value, minutes));
        }

        if (errors.Count > 0)
            throw new IngestRejected(errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));

        var minStart = parsed.Min(p => p.StartUtc);
        var maxStart = parsed.Max(p => p.StartUtc);
        var now = clock.GetUtcNow().UtcDateTime;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var latest = (await PriceQueries.Latest(db, nodeIds, run, minStart, maxStart.AddTicks(1), DateTime.MaxValue).ToListAsync(ct))
            .ToDictionary(p => (p.NodeId, p.IntervalStartUtc));

        int inserted = 0, revised = 0, unchanged = 0;
        foreach (var (r, startUtc, minutes) in parsed)
        {
            latest.TryGetValue((r.Node, startUtc), out var current);
            if (current is not null && current.SameValues(r.Lmp, r.Energy, r.Congestion, r.Loss, minutes))
            {
                unchanged++;
                continue;
            }
            db.Prices.Add(new PriceRevision
            {
                NodeId = r.Node,
                Run = run,
                IntervalStartUtc = startUtc,
                IntervalMinutes = minutes,
                Revision = (current?.Revision ?? 0) + 1,
                ReceivedAtUtc = now,
                Lmp = r.Lmp,
                Energy = r.Energy,
                Congestion = r.Congestion,
                Loss = r.Loss,
            });
            if (current is null) inserted++; else revised++;
        }

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Unique (node, run, interval, revision) was hit: someone else revised these intervals first.
            db.ChangeTracker.Clear();
            throw new IngestConflict();
        }

        return new IngestResult(rows.Count, inserted, revised, unchanged);
    }
}