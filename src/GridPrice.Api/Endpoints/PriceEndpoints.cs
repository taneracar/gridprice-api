using System.Globalization;

using GridPrice.Api.Data;
using GridPrice.Api.Domain;
using GridPrice.Api.Ingest;

using Microsoft.EntityFrameworkCore;

namespace GridPrice.Api.Endpoints;

public sealed record MarketDto(string Code, string Name, string TimeZone);
public sealed record NodeDto(string Id, string Market, string Name, string Kind);
public sealed record IngestRequest(string Run, IReadOnlyList<PriceRow> Rows);

public sealed record IntervalDto(
    DateTime StartUtc,
    DateTimeOffset LocalStart,
    DateOnly LocalDate,
    int HourEnding,
    bool RepeatedHour,
    int Minute,
    int IntervalMinutes,
    bool OnPeak,
    decimal Lmp,
    decimal Energy,
    decimal Congestion,
    decimal Loss,
    int Revision,
    DateTime ReceivedAtUtc);

public sealed record SpreadDto(DateTime StartUtc, DateTimeOffset LocalStart, int HourEnding, bool RepeatedHour, decimal SourceLmp, decimal SinkLmp, decimal LmpSpread, decimal CongestionSpread);

public static class PriceEndpoints
{
    /// <summary>Longest range one request may ask for, so a query cannot pull millions of 5-minute rows.</summary>
    public const int MaxDays = 31;

    public static void MapPriceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/markets", () => Markets.All.Select(m => new MarketDto(m.Code, m.Name, m.TimeZoneId)))
            .WithSummary("Markets and the local time zone their prices are labelled in");

        app.MapGet("/markets/{market}/nodes", async (string market, GridDbContext db, CancellationToken ct) =>
        {
            var m = Markets.Find(market);
            if (m is null) return Results.Problem(statusCode: 404, title: $"Unknown market '{market}'.");
            var nodes = await db.Nodes.Where(n => n.MarketCode == m.Code).OrderBy(n => n.Id)
                .Select(n => new NodeDto(n.Id, n.MarketCode, n.Name, n.Kind)).ToListAsync(ct);
            return Results.Ok(nodes);
        });

        app.MapPost("/markets/{market}/prices", async (string market, IngestRequest body, PriceIngest ingest, CancellationToken ct) =>
        {
            var m = Markets.Find(market);
            if (m is null) return Results.Problem(statusCode: 404, title: $"Unknown market '{market}'.");
            if (!Markets.TryParseRun(body.Run, out var run))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["run"] = ["Use DA or RT."] });
            return await Ingest(() => ingest.IngestAsync(m, run, body.Rows ?? [], ct));
        }).WithSummary("Publish prices as JSON rows (whole batch or nothing)");

        app.MapPost("/markets/{market}/prices/csv", async (string market, string? run, HttpRequest request, PriceIngest ingest, CancellationToken ct) =>
        {
            var m = Markets.Find(market);
            if (m is null) return Results.Problem(statusCode: 404, title: $"Unknown market '{market}'.");
            if (!Markets.TryParseRun(run, out var r))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["run"] = ["Use ?run=DA or ?run=RT."] });
            return await Ingest(async () =>
            {
                using var reader = new StreamReader(request.Body);
                var text = await reader.ReadToEndAsync(ct);
                var rows = PriceCsv.Parse(new StringReader(text));
                return await ingest.IngestAsync(m, r, rows, ct);
            });
        }).Accepts<string>("text/csv").WithSummary("Publish prices as an ISO-style CSV file");

        app.MapGet("/nodes/{node}/prices", async (string node, string? run, string? from, string? to, string? asOf, GridDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var q = await Resolve(db, clock, [node], run, from, to, asOf, ct);
            if (q.Error is not null) return q.Error;
            var (m, r, f, t, asOfUtc) = q.Value!.Value;
            var (start, end) = MarketClock.DayRangeUtc(m, f, t);
            var rows = await PriceQueries.Latest(db, [node], r, start, end, asOfUtc).ToListAsync(ct);
            return Results.Ok(rows.Select(p => ToDto(m, p)));
        }).WithSummary("Interval prices for local days from..to, as they were known at asOf (default: now)");

        app.MapGet("/nodes/{node}/daily", async (string node, string? run, string? from, string? to, string? asOf, GridDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var q = await Resolve(db, clock, [node], run, from, to, asOf, ct);
            if (q.Error is not null) return q.Error;
            var (m, r, f, t, asOfUtc) = q.Value!.Value;
            var (start, end) = MarketClock.DayRangeUtc(m, f, t);
            var rows = await PriceQueries.Latest(db, [node], r, start, end, asOfUtc).ToListAsync(ct);
            var minutes = r == MarketRun.DayAhead ? 60 : 5;
            return Results.Ok(DailyStats.Compute(m, rows.Select(p => new PricePoint(p.IntervalStartUtc, p.IntervalMinutes, p.Lmp)), f, t, minutes));
        }).WithSummary("Daily average, on-/off-peak average, min, max and completeness per local day");

        app.MapGet("/spread", async (string? source, string? sink, string? run, string? from, string? to, string? asOf, GridDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(sink))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["source"] = ["source and sink are required."] });
            var q = await Resolve(db, clock, [source, sink], run, from, to, asOf, ct);
            if (q.Error is not null) return q.Error;
            var (m, r, f, t, asOfUtc) = q.Value!.Value;
            var (start, end) = MarketClock.DayRangeUtc(m, f, t);
            var rows = await PriceQueries.Latest(db, [source, sink], r, start, end, asOfUtc).ToListAsync(ct);
            var src = rows.Where(p => p.NodeId == source).ToDictionary(p => p.IntervalStartUtc);
            // Only intervals priced at both ends; a missing side is a gap, not a zero.
            var spread = rows.Where(p => p.NodeId == sink && src.ContainsKey(p.IntervalStartUtc)).Select(k =>
            {
                var s = src[k.IntervalStartUtc];
                var label = MarketClock.ToLocal(m, k.IntervalStartUtc);
                return new SpreadDto(k.IntervalStartUtc, label.LocalStart, label.HourEnding, label.RepeatedHour, s.Lmp, k.Lmp, k.Lmp - s.Lmp, k.Congestion - s.Congestion);
            });
            return Results.Ok(spread);
        }).WithSummary("Sink minus source LMP and congestion per interval (the value of moving power from source to sink)");
    }

    private static async Task<IResult> Ingest(Func<Task<IngestResult>> run)
    {
        try
        {
            return Results.Ok(await run());
        }
        catch (IngestRejected e)
        {
            return Results.ValidationProblem(e.Errors.ToDictionary(x => x.Key, x => x.Value), statusCode: 422, title: "The batch was rejected; nothing was written.");
        }
        catch (IngestConflict e)
        {
            return Results.Problem(statusCode: 409, title: e.Message);
        }
    }

    private static IntervalDto ToDto(Market m, PriceRevision p)
    {
        var l = MarketClock.ToLocal(m, p.IntervalStartUtc);
        return new IntervalDto(p.IntervalStartUtc, l.LocalStart, l.Date, l.HourEnding, l.RepeatedHour, l.Minute, p.IntervalMinutes,
            PeakCalendar.IsOnPeak(l), p.Lmp, p.Energy, p.Congestion, p.Loss, p.Revision, p.ReceivedAtUtc);
    }

    private sealed record Resolved(IResult? Error, (Market, MarketRun, DateOnly, DateOnly, DateTime)? Value);

    private static async Task<Resolved> Resolve(GridDbContext db, TimeProvider clock, string[] nodeIds, string? run, string? from, string? to, string? asOf, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (!Markets.TryParseRun(run ?? "DA", out var r)) errors["run"] = ["Use DA or RT."];
        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f)) errors["from"] = ["from must be a local date, yyyy-MM-dd."];
        if (!DateOnly.TryParseExact(to ?? from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) errors["to"] = ["to must be a local date, yyyy-MM-dd."];
        var asOfUtc = clock.GetUtcNow().UtcDateTime;
        if (asOf is not null)
        {
            if (DateTimeOffset.TryParse(asOf, CultureInfo.InvariantCulture, DateTimeStyles.None, out var a) && HasOffset(asOf)) asOfUtc = a.UtcDateTime;
            else errors["asOf"] = ["asOf must be an ISO 8601 instant with an offset, e.g. 2026-03-09T12:00:00Z."];
        }
        if (errors.Count == 0)
        {
            if (t < f) errors["to"] = ["to is before from."];
            else if (t.DayNumber - f.DayNumber + 1 > MaxDays) errors["to"] = [$"At most {MaxDays} days per request."];
        }
        if (errors.Count > 0) return new(Results.ValidationProblem(errors), null);

        var nodes = await db.Nodes.Where(n => nodeIds.Contains(n.Id)).ToListAsync(ct);
        var missing = nodeIds.Except(nodes.Select(n => n.Id)).ToArray();
        if (missing.Length > 0) return new(Results.Problem(statusCode: 404, title: $"Unknown node: {string.Join(", ", missing)}."), null);
        var markets = nodes.Select(n => n.MarketCode).Distinct().ToArray();
        if (markets.Length > 1) return new(Results.ValidationProblem(new Dictionary<string, string[]> { ["sink"] = ["source and sink must be in the same market."] }), null);

        return new(null, (Markets.Find(markets[0])!, r, f, t, asOfUtc));
    }

    // "2026-03-09" parses as a DateTimeOffset in the server's zone. Require an explicit offset or Z.
    private static bool HasOffset(string s) => s.EndsWith('Z') || s.EndsWith('z') || System.Text.RegularExpressions.Regex.IsMatch(s, @"[+-]\d\d:?\d\d$");
}