using System.Globalization;

namespace GridPrice.Api.Ingest;

/// <summary>
/// Reads the CSV layout ISOs publish settlement-point prices in:
/// <c>node,date,hour_ending,repeated_hour,lmp,energy,congestion,loss</c>, plus optional
/// <c>minute</c> and <c>interval_minutes</c> columns for real-time data. Column order is free.
/// Fields are plain values: quoted fields are rejected rather than half-parsed.
/// </summary>
public static class PriceCsv
{
    private static readonly string[] Required = ["node", "date", "hour_ending", "lmp", "energy", "congestion", "loss"];

    public static IReadOnlyList<PriceRow> Parse(TextReader reader)
    {
        var errors = new Dictionary<string, string[]>();
        var header = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(header))
            throw new IngestRejected(new Dictionary<string, string[]> { ["csv"] = ["The file is empty."] });

        var cols = header.Split(',').Select(c => c.Trim().ToLowerInvariant()).ToArray();
        var index = cols.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
        var missing = Required.Where(r => !index.ContainsKey(r)).ToArray();
        if (missing.Length > 0)
            throw new IngestRejected(new Dictionary<string, string[]> { ["csv"] = [$"Missing columns: {string.Join(", ", missing)}."] });

        var rows = new List<PriceRow>();
        string? line;
        var lineNo = 1;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (line.Trim().Length == 0) continue;
            var f = line.Split(',');
            var key = $"line {lineNo}";
            if (line.Contains('"')) { errors[key] = ["Quoted fields are not supported."]; continue; }
            if (f.Length != cols.Length) { errors[key] = [$"Expected {cols.Length} fields, found {f.Length}."]; continue; }

            string Get(string name) => f[index[name]].Trim();
            string? Opt(string name) => index.TryGetValue(name, out var i) ? f[i].Trim() : null;

            var problems = new List<string>();
            if (!DateOnly.TryParseExact(Get("date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                problems.Add("date must be yyyy-MM-dd");
            if (!int.TryParse(Get("hour_ending"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var he))
                problems.Add("hour_ending must be a whole number");
            var repeated = ParseFlag(Opt("repeated_hour"), problems);
            var minute = 0;
            if (Opt("minute") is { Length: > 0 } m && !int.TryParse(m, NumberStyles.Integer, CultureInfo.InvariantCulture, out minute))
                problems.Add("minute must be a whole number");
            int? intervalMinutes = null;
            if (Opt("interval_minutes") is { Length: > 0 } im)
            {
                if (int.TryParse(im, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) intervalMinutes = v;
                else problems.Add("interval_minutes must be a whole number");
            }
            decimal Dec(string name)
            {
                if (decimal.TryParse(Get(name), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var d)) return d;
                problems.Add($"{name} must be a number");
                return 0;
            }
            var lmp = Dec("lmp");
            var energy = Dec("energy");
            var congestion = Dec("congestion");
            var loss = Dec("loss");

            if (problems.Count > 0) { errors[key] = [.. problems]; continue; }
            rows.Add(new PriceRow(Get("node"), date, he, repeated, minute, intervalMinutes, lmp, energy, congestion, loss));
        }

        if (errors.Count > 0) throw new IngestRejected(errors);
        return rows;
    }

    private static bool ParseFlag(string? value, List<string> problems)
    {
        switch (value?.ToUpperInvariant())
        {
            case null or "" or "N" or "FALSE" or "0": return false;
            case "Y" or "TRUE" or "1": return true;
            default:
                problems.Add("repeated_hour must be Y or N");
                return false;
        }
    }
}