# gridprice-api

An ASP.NET Core minimal API for wholesale electricity prices (LMPs): publish day-ahead and
real-time prices per pricing node, then query them by market-local day, with daily on-/off-peak
statistics, node-to-node spreads, and "as of" queries that return the data as it was known at
any past moment.

C# / .NET 11, EF Core 11 (SQL Server in production, SQLite for zero-setup local runs), xUnit v3.

> The prices in the demo seed are **synthetic**. Hub names are real, the numbers are invented.

## Why this domain

Market data is mostly about time. Three details that are easy to get wrong:

- **Hour ending, local prevailing time.** ISOs label hours by when they end, in the market's
  local time: HE01 is 00:00-01:00. ERCOT publishes in Central time, CAISO in Pacific, PJM in
  Eastern.
- **Days are not always 24 hours.** On the spring-forward day there is no HE03 and the day has
  23 hours. On the fall-back day HE02 happens twice (the second one carries a repeated-hour/DST
  flag) and the day has 25 hours. A table keyed on local date + hour silently merges or drops
  those hours.
- **Prices get corrected.** Settlement prices are revised after publication. Overwriting them
  makes yesterday's report impossible to reproduce.

## Design

- **Store UTC, label local.** Each interval is stored by its UTC start. Market-local date, hour
  ending, repeated-hour flag and the local offset are derived on the way in and out
  (`Domain/MarketClock.cs`), using the market's IANA time zone.
- **Append-only revisions.** A price row is never updated. A changed value for a known interval
  becomes a new row with `revision + 1` and its own `receivedAtUtc`. "Latest as of T" is one SQL
  query (`NOT EXISTS` a higher revision received by T) that EF Core translates for both providers
  (`Data/PriceQueries.cs`). A unique index on (node, run, interval, revision) makes two writers
  racing on the same interval fail with 409 instead of both "winning".
- **Idempotent, all-or-nothing ingest.** A batch is fully validated first and written in one
  transaction. Re-sending the same values writes nothing. Any bad row rejects the whole batch with
  a 422 problem response that names the row and field (`rows[5].hourEnding`, `line 3`).
- **Validation that matches the data:** the hour must exist on that local day, the repeated flag is
  only valid for the repeated hour, energy + congestion + loss must equal the LMP (within 0.0001,
  since ISOs round components separately), at most 5 decimals (the column is `decimal(12,5)`, and a
  sixth digit would be rounded silently), interval length must match the run (DA 60 min, RT 5 min),
  no duplicate intervals in one batch.
- **Money-safe arithmetic.** `decimal` everywhere, never `double`. Daily averages are
  time-weighted (5- and 60-minute intervals weigh by duration) and rounded half-to-even at 5
  decimals.
- **Peak calendar.** On-peak is HE07-HE22 Monday-Friday excluding NERC holidays. A holiday that
  falls on Sunday is observed Monday; one that falls on Saturday is not moved
  (`Domain/PeakCalendar.cs`).
- **Completeness, not guesses.** Daily stats report `expectedIntervals` for that day (23, 24 or 25
  hours' worth) and `complete`. A spread is returned only where both nodes are priced; a gap stays
  a gap, never a zero.
- **Two providers, one model.** SQL Server is the production target and is versioned with EF
  migrations (`Data/Migrations`); CI checks that the migrations match the model. SQLite is created
  from the model with `EnsureCreated` for local runs and fast tests.

## API

| Method | Path | |
|---|---|---|
| GET | `/markets` | Markets and their time zones |
| GET | `/markets/{market}/nodes` | Pricing nodes in a market |
| POST | `/markets/{market}/prices` | JSON batch: `{ "run": "DA", "rows": [...] }` |
| POST | `/markets/{market}/prices/csv?run=DA` | ISO-style CSV: `node,date,hour_ending,repeated_hour,lmp,energy,congestion,loss` (+ optional `minute`, `interval_minutes`) |
| GET | `/nodes/{node}/prices?run=DA&from=&to=&asOf=` | Intervals for local days `from..to` (max 31 days) |
| GET | `/nodes/{node}/daily?run=DA&from=&to=&asOf=` | Daily average, on-/off-peak averages, min, max, completeness |
| GET | `/spread?source=&sink=&run=&from=&to=&asOf=` | Sink minus source LMP and congestion per interval |
| GET | `/health` | Database health check |
| GET | `/openapi/v1.json` | OpenAPI document |

Errors are RFC 9457 problem details. `asOf` must be an instant with an offset
(`2026-03-09T12:00:00Z`); a bare date is rejected rather than read in the server's time zone.

Example, the ERCOT fall-back day (synthetic seed):

```
GET /nodes/HB_HOUSTON/prices?run=DA&from=2026-11-01
...
{ "startUtc": "2026-11-01T06:00:00Z", "localStart": "2026-11-01T01:00:00-05:00", "hourEnding": 2, "repeatedHour": false, ... }
{ "startUtc": "2026-11-01T07:00:00Z", "localStart": "2026-11-01T01:00:00-06:00", "hourEnding": 2, "repeatedHour": true,  ... }
...
GET /nodes/HB_HOUSTON/daily?run=DA&from=2026-03-08
[{ "date": "2026-03-08", "hoursInDay": 23, "intervals": 23, "expectedIntervals": 23, "complete": true, ... }]
```

## Run it

Requires the .NET 11 SDK.

```bash
dotnet run --project src/GridPrice.Api --launch-profile http
# http://localhost:5217/markets
```

The Development profile uses a local SQLite file and seeds synthetic ERCOT day-ahead prices around
both 2026 DST changes. For SQL Server set `Database__Provider=SqlServer` and
`ConnectionStrings__Grid=...`; migrations run at startup.

## How it was tested

- `dotnet test`: 49 tests on SQLite.
  - Clock: DST days in Chicago (23 and 25 hours, missing HE03, both HE02s), every 5-minute
    interval of the fall-back day round-trips UTC -> label -> UTC, the same instant in two markets.
  - Peak calendar: 2026 holidays, Sunday observance (2022, 2023), Saturday July 4, 2026 not moved.
  - Daily stats: 23/25-hour completeness, peak split, time weighting, half-to-even rounding.
  - API, through the real app in memory (`WebApplicationFactory`, fake clock): DST round trip,
    whole-batch rejection, per-row errors, idempotent resend, revisions and `asOf`, CSV upload and
    CSV errors, 5-minute real-time data, spreads with gaps, query validation, 404s, health.
- The same 17 API tests run against **SQL Server 2022** in CI (service container); that job fails if
  they were skipped. They are skipped locally, where there is no SQL Server (no Docker on the dev
  Mac), so **the SQL Server path has only been exercised in CI**.
- Deliberate breakages, each caught by the suite and then reverted: swapped DST offsets, no Sunday
  holiday observance, latest-revision filter dropped, `asOf` ignored, idempotency removed, every
  day assumed 24 hours, sixth decimal accepted, unweighted average, partial batch written, on-peak
  starting at HE08 (10/10).
- Run by hand against the seeded API: 25 intervals on 2026-11-01 with both HE02s labelled,
  23-hour 2026-03-08, spread values, a bad `asOf` rejected, OpenAPI document served.
- CI: format check, warnings as errors, tests on SQLite, migrations-match-model check, tests on
  SQL Server.

## Limits

- Synthetic data, three markets, a handful of nodes. No real ISO feed is downloaded.
- One on-peak definition (the eastern 5x16). Some markets and products use others (for example
  7x8, 2x16, or CAISO-specific blocks).
- Ingest is synchronous and capped at 50,000 rows per batch; a real feed would stream into a
  staging table and use bulk copy.
- No authentication or rate limiting.
- Concurrency is protected by the unique index, but there is no test that drives two writers at once.

## License

MIT
