using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using GridPrice.Api.Data;
using GridPrice.Api.Domain;
using GridPrice.Api.Ingest;

namespace GridPrice.Tests;

/// <summary>
/// HTTP contract tests, run once per database provider. The SQL Server run needs
/// GRIDPRICE_SQLSERVER (set in CI against a SQL Server service container) and is skipped without it.
/// </summary>
public abstract class ApiTests : IAsyncLifetime
{
    protected abstract string Provider { get; }
    private GridApiFactory _factory = null!;
    private HttpClient _http = null!;

    public ValueTask InitializeAsync()
    {
        if (Provider == "SqlServer" && Environment.GetEnvironmentVariable(GridApiFactory.SqlServerVariable) is null)
            Assert.Skip($"{GridApiFactory.SqlServerVariable} not set; SQL Server tests run in CI.");
        _factory = new GridApiFactory(Provider);
        _http = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    private static readonly Market Ercot = Markets.Find("ERCOT")!;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PriceRow Row(string node, string date, int he, decimal lmp, bool repeated = false, decimal congestion = 1m, decimal loss = 0.5m) =>
        new(node, DateOnly.Parse(date), he, repeated, 0, null, lmp, lmp - congestion - loss, congestion, loss);

    private static List<PriceRow> FullDay(string node, string date, Func<int, bool, decimal> price) =>
        SeedData.Hours(Ercot, DateOnly.Parse(date)).Select(h => Row(node, date, h.HourEnding, price(h.HourEnding, h.Repeated), h.Repeated)).ToList();

    private async Task<IngestResult> Post(IEnumerable<PriceRow> rows, string run = "DA")
    {
        var res = await _http.PostAsJsonAsync("/markets/ERCOT/prices", new { run, rows }, Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<IngestResult>(Ct))!;
    }

    private async Task<JsonElement> Get(string url)
    {
        var res = await _http.GetAsync(url, Ct);
        Assert.True(res.IsSuccessStatusCode, $"{url} -> {(int)res.StatusCode} {await res.Content.ReadAsStringAsync(Ct)}");
        return await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<JsonElement> Problem(HttpResponseMessage res, HttpStatusCode expected)
    {
        Assert.Equal(expected, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        return await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    [Fact]
    public async Task Lists_markets_and_seeded_nodes()
    {
        var markets = await Get("/markets");
        Assert.Equal(3, markets.GetArrayLength());
        var nodes = await Get("/markets/ercot/nodes");
        Assert.Contains(nodes.EnumerateArray(), n => n.GetProperty("id").GetString() == "HB_HOUSTON");
        Assert.All(nodes.EnumerateArray(), n => Assert.Equal("ERCOT", n.GetProperty("market").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/markets/MISO/nodes", Ct)).StatusCode);
    }

    [Fact]
    public async Task Fall_back_day_round_trips_with_25_labelled_hours()
    {
        var result = await Post(FullDay("HB_HOUSTON", "2026-11-01", (he, rep) => he + (rep ? 0.25m : 0m)));
        Assert.Equal(25, result.Inserted);

        var prices = (await Get("/nodes/HB_HOUSTON/prices?run=DA&from=2026-11-01")).EnumerateArray().ToList();
        Assert.Equal(25, prices.Count);
        var he02 = prices.Where(p => p.GetProperty("hourEnding").GetInt32() == 2).ToList();
        Assert.Equal(2, he02.Count);
        Assert.Equal(2m, he02[0].GetProperty("lmp").GetDecimal());
        Assert.False(he02[0].GetProperty("repeatedHour").GetBoolean());
        Assert.Equal(2.25m, he02[1].GetProperty("lmp").GetDecimal());
        Assert.True(he02[1].GetProperty("repeatedHour").GetBoolean());
        Assert.Equal("2026-11-01T06:00:00Z", he02[0].GetProperty("startUtc").GetString());
        Assert.Equal("2026-11-01T07:00:00Z", he02[1].GetProperty("startUtc").GetString());
        Assert.Equal("2026-11-01T01:00:00-06:00", he02[1].GetProperty("localStart").GetString());

        var daily = (await Get("/nodes/HB_HOUSTON/daily?run=DA&from=2026-11-01")).EnumerateArray().Single();
        Assert.Equal(25, daily.GetProperty("hoursInDay").GetInt32());
        Assert.True(daily.GetProperty("complete").GetBoolean());
        Assert.Equal(0, daily.GetProperty("onPeakHours").GetInt32()); // Sunday
    }

    [Fact]
    public async Task Spring_forward_hour_that_does_not_exist_rejects_the_whole_batch()
    {
        var rows = FullDay("HB_HOUSTON", "2026-03-08", (he, _) => he);
        rows.Insert(5, Row("HB_HOUSTON", "2026-03-08", 3, 3m));

        var problem = await Problem(await _http.PostAsJsonAsync("/markets/ERCOT/prices", new { run = "DA", rows }, Ct), HttpStatusCode.UnprocessableEntity);
        var message = problem.GetProperty("errors").GetProperty("rows[5].hourEnding")[0].GetString();
        Assert.Contains("does not exist on 2026-03-08", message);

        // Nothing from the batch was written.
        Assert.Equal(0, (await Get("/nodes/HB_HOUSTON/prices?from=2026-03-08")).GetArrayLength());
    }

    [Fact]
    public async Task Row_level_errors_name_the_row_and_field()
    {
        PriceRow[] rows =
        [
            Row("HB_HOUSTON", "2026-06-16", 1, 20m),
            Row("HB_NOWHERE", "2026-06-16", 1, 20m),
            Row("HB_HOUSTON", "2026-06-16", 2, 20m) with { Energy = 99m }, // components do not add up
            Row("HB_HOUSTON", "2026-06-16", 1, 21m), // duplicate of rows[0]
            Row("HB_HOUSTON", "2026-06-16", 4, 20m) with { Minute = 15 },
            Row("HB_HOUSTON", "2026-06-16", 5, 20.123456m, congestion: 0m, loss: 0m),
            Row("HB_HOUSTON", "2026-06-16", 6, 20m, repeated: true),
        ];
        var errors = (await Problem(await _http.PostAsJsonAsync("/markets/ERCOT/prices", new { run = "DA", rows }, Ct), HttpStatusCode.UnprocessableEntity))
            .GetProperty("errors");
        Assert.True(errors.TryGetProperty("rows[1].node", out _));
        Assert.True(errors.TryGetProperty("rows[2].lmp", out _));
        Assert.Contains("rows[0]", errors.GetProperty("rows[3].hourEnding")[0].GetString());
        Assert.True(errors.TryGetProperty("rows[4].minute", out _));
        Assert.True(errors.TryGetProperty("rows[5].lmp", out _)); // six decimals would be rounded by decimal(12,5)
        Assert.Contains("not a repeated hour", errors.GetProperty("rows[6].hourEnding")[0].GetString());
        Assert.False(errors.TryGetProperty("rows[0].node", out _));
    }

    [Fact]
    public async Task Resending_the_same_batch_is_a_no_op()
    {
        var rows = FullDay("HB_NORTH", "2026-06-16", (he, _) => 30m + he);
        Assert.Equal(24, (await Post(rows)).Inserted);
        var again = await Post(rows);
        Assert.Equal(0, again.Inserted);
        Assert.Equal(0, again.Revised);
        Assert.Equal(24, again.Unchanged);
        await using var db = _factory.NewDbContext();
        Assert.Equal(24, db.Prices.Count());
    }

    [Fact]
    public async Task Correction_is_a_new_revision_and_as_of_returns_what_was_known_then()
    {
        var rows = FullDay("HB_WEST", "2026-06-16", (_, _) => 25m);
        await Post(rows);
        var firstPublished = _factory.Clock.GetUtcNow();

        _factory.Clock.Advance(TimeSpan.FromHours(30));
        var corrected = rows.Select(r => r.HourEnding == 18 ? Row("HB_WEST", "2026-06-16", 18, 412.5m) : r).ToList();
        var result = await Post(corrected);
        Assert.Equal(1, result.Revised);
        Assert.Equal(23, result.Unchanged);

        var now = (await Get("/nodes/HB_WEST/prices?from=2026-06-16")).EnumerateArray().Single(p => p.GetProperty("hourEnding").GetInt32() == 18);
        Assert.Equal(412.5m, now.GetProperty("lmp").GetDecimal());
        Assert.Equal(2, now.GetProperty("revision").GetInt32());

        var asOf = Uri.EscapeDataString(firstPublished.AddMinutes(1).ToString("O"));
        var then = (await Get($"/nodes/HB_WEST/prices?from=2026-06-16&asOf={asOf}")).EnumerateArray().ToList();
        Assert.Equal(24, then.Count);
        var he18 = then.Single(p => p.GetProperty("hourEnding").GetInt32() == 18);
        Assert.Equal(25m, he18.GetProperty("lmp").GetDecimal());
        Assert.Equal(1, he18.GetProperty("revision").GetInt32());

        var before = Uri.EscapeDataString(firstPublished.AddMinutes(-1).ToString("O"));
        Assert.Equal(0, (await Get($"/nodes/HB_WEST/prices?from=2026-06-16&asOf={before}")).GetArrayLength());

        var daily = (await Get("/nodes/HB_WEST/daily?from=2026-06-16")).EnumerateArray().Single();
        Assert.Equal(412.5m, daily.GetProperty("max").GetDecimal());
        var dailyThen = (await Get($"/nodes/HB_WEST/daily?from=2026-06-16&asOf={asOf}")).EnumerateArray().Single();
        Assert.Equal(25m, dailyThen.GetProperty("max").GetDecimal());
    }

    [Fact]
    public async Task Csv_upload_matches_json_upload()
    {
        var csv = new StringBuilder("node,date,hour_ending,repeated_hour,lmp,energy,congestion,loss\n");
        foreach (var (he, rep) in SeedData.Hours(Ercot, new(2026, 11, 1)))
            csv.Append($"LZ_SOUTH,2026-11-01,{he},{(rep ? "Y" : "N")},{40 + he}.12345,{38 + he}.62345,1.00000,0.50000\n");
        var res = await _http.PostAsync("/markets/ERCOT/prices/csv?run=DA", new StringContent(csv.ToString(), Encoding.UTF8, "text/csv"), Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(25, (await res.Content.ReadFromJsonAsync<IngestResult>(Ct))!.Inserted);

        var prices = (await Get("/nodes/LZ_SOUTH/prices?from=2026-11-01")).EnumerateArray().ToList();
        Assert.Equal(25, prices.Count);
        Assert.Equal(41.12345m, prices[0].GetProperty("lmp").GetDecimal()); // five decimals survive the database
    }

    [Fact]
    public async Task Bad_csv_lines_are_reported_by_line_number()
    {
        const string csv = "node,date,hour_ending,lmp,energy,congestion,loss\nHB_NORTH,2026-06-16,1,20,18.5,1,0.5\nHB_NORTH,16/06/2026,2,20,18.5,1,0.5\nHB_NORTH,2026-06-16,3,\"20\",18.5,1,0.5\n";
        var res = await _http.PostAsync("/markets/ERCOT/prices/csv?run=DA", new StringContent(csv, Encoding.UTF8, "text/csv"), Ct);
        var errors = (await Problem(res, HttpStatusCode.UnprocessableEntity)).GetProperty("errors");
        Assert.Contains("date", errors.GetProperty("line 3")[0].GetString());
        Assert.True(errors.TryGetProperty("line 4", out _));
        Assert.Equal(0, (await Get("/nodes/HB_NORTH/prices?from=2026-06-16")).GetArrayLength());
    }

    [Fact]
    public async Task Real_time_five_minute_prices_and_daily_completeness()
    {
        var rows = new List<PriceRow>();
        for (var he = 1; he <= 24; he++)
            for (var m = 0; m < 60; m += 5)
                rows.Add(Row("HB_HOUSTON", "2026-06-16", he, 20m + he, congestion: 0m, loss: 0m) with { Minute = m });
        rows.RemoveAt(100); // one missing interval
        Assert.Equal(287, (await Post(rows, run: "RT")).Inserted);

        var daily = (await Get("/nodes/HB_HOUSTON/daily?run=RT&from=2026-06-16")).EnumerateArray().Single();
        Assert.Equal(288, daily.GetProperty("expectedIntervals").GetInt32());
        Assert.False(daily.GetProperty("complete").GetBoolean());

        // DA and RT are separate series.
        Assert.Equal(0, (await Get("/nodes/HB_HOUSTON/prices?run=DA&from=2026-06-16")).GetArrayLength());

        var wrongLength = await _http.PostAsJsonAsync("/markets/ERCOT/prices", new { run = "RT", rows = new[] { Row("HB_HOUSTON", "2026-06-17", 1, 20m) with { IntervalMinutes = 60 } } }, Ct);
        await Problem(wrongLength, HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Spread_is_sink_minus_source_where_both_are_priced()
    {
        await Post(FullDay("HB_HOUSTON", "2026-06-16", (he, _) => 30m + he));
        await Post(FullDay("HB_WEST", "2026-06-16", (he, _) => 10m).Where(r => r.HourEnding != 12));

        var spread = (await Get("/spread?source=HB_WEST&sink=HB_HOUSTON&from=2026-06-16")).EnumerateArray().ToList();
        Assert.Equal(23, spread.Count); // HE12 is missing at the source: a gap, not a zero
        var he13 = spread.Single(s => s.GetProperty("hourEnding").GetInt32() == 13);
        Assert.Equal(33m, he13.GetProperty("lmpSpread").GetDecimal());
        Assert.Equal(0m, he13.GetProperty("congestionSpread").GetDecimal());

        var cross = await _http.GetAsync("/spread?source=HB_WEST&sink=WESTERN_HUB&from=2026-06-16", Ct);
        await Problem(cross, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("/nodes/HB_HOUSTON/prices", "from")]
    [InlineData("/nodes/HB_HOUSTON/prices?from=2026-06-20&to=2026-06-10", "to")]
    [InlineData("/nodes/HB_HOUSTON/prices?from=2026-01-01&to=2026-03-01", "to")]
    [InlineData("/nodes/HB_HOUSTON/prices?from=2026-06-16&run=XX", "run")]
    [InlineData("/nodes/HB_HOUSTON/prices?from=2026-06-16&asOf=2026-06-16", "asOf")]
    public async Task Bad_queries_return_validation_problems(string url, string field)
    {
        var problem = await Problem(await _http.GetAsync(url, Ct), HttpStatusCode.BadRequest);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.ToString());
    }

    [Fact]
    public async Task Unknown_node_is_404()
    {
        await Problem(await _http.GetAsync("/nodes/NOPE/prices?from=2026-06-16", Ct), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Health_endpoint_checks_the_database() =>
        Assert.Equal("Healthy", await _http.GetStringAsync("/health", Ct));
}

public sealed class SqliteApiTests : ApiTests
{
    protected override string Provider => "Sqlite";
}

public sealed class SqlServerApiTests : ApiTests
{
    protected override string Provider => "SqlServer";
}