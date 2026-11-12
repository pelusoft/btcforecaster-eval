using System.Globalization;
using BtcForecaster.Eval.Baseline;
using BtcForecaster.Eval.Comparison;
using BtcForecaster.Eval.Data;
using BtcForecaster.Eval.Scoring;

// btcf-eval: score forecasts the way BtcForecaster does, and test model changes the way its studies do.
//   score <forecasts.csv>                               coverage, margins and interval scores per horizon
//   download --from 2026-06-01 --to 2026-07-01          public Binance BTC/USDT 5-minute bars into data/cache
//   baseline --from 2026-06-01 --to 2026-07-01          replay the v1 baseline and score it
//   compare  --from 2026-01-01 --to 2026-07-01 --half-life 60
//                                                      tune/holdout comparison of a v1 variant vs v1, with Diebold-Mariano

var command = args.FirstOrDefault()?.ToLowerInvariant();
var options = Options(args.Skip(1).ToArray());
try
{
    return command switch
    {
        "score" => ScoreFile(args.Length > 1 ? args[1] : throw new ArgumentException("score needs a CSV file, e.g. data/sample/forecasts-v2-har-20261103-20261110.csv")),
        "download" => await Download(),
        "baseline" => await Baseline(),
        "compare" => await Compare(),
        _ => Usage(),
    };
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

int Usage()
{
    Console.WriteLine("""
        btcf-eval: score forecasts the way BtcForecaster does.

          score <forecasts.csv>                          Coverage, margin95 and interval scores per horizon.
          download --from YYYY-MM-DD --to YYYY-MM-DD     Public Binance BTC/USDT 5-minute bars into the local cache.
          baseline --from ... --to ... [--stride 15]     Replay the v1 baseline over the period and score it.
          compare  --from ... --to ... [--half-life 60]  Tune on the first half, test once on the second: a v1 variant vs v1.
                   [--vol-conditioning 0.33]             Variant: calibrate on the past outcomes nearest in volatility.
                   [--vol-conditioning-horizons 1,4]     ...at these horizons only (default: all).

        Options: --cache <dir> (default data/cache), --out <file.csv> (baseline: write the forecasts).
        """);
    return 1;
}

int ScoreFile(string path)
{
    var records = ForecastCsv.Read(path);
    Console.WriteLine($"{path}: {records.Count:N0} forecasts ({records.Count(r => r.IsScored):N0} scored), models {string.Join(", ", records.Select(r => r.ModelRelease).Distinct())}");
    Console.WriteLine();
    Print(records);
    return 0;
}

async Task<int> Download()
{
    var (from, to) = Period();
    var bars = await new BinanceBars(Cache()).LoadAsync(from, to, Console.WriteLine);
    Console.WriteLine($"{bars.Count:N0} bars from {bars.FirstOrDefault().Timestamp:yyyy-MM-dd HH:mm} to {bars.LastOrDefault().Timestamp:yyyy-MM-dd HH:mm} UTC, cached in {Cache()}");
    return 0;
}

async Task<int> Baseline()
{
    var (from, to) = Period();
    var stride = TimeSpan.FromMinutes(Int("stride", 15));
    var model = new BaselineModel();
    var bars = await LoadBars(from, to, model.Settings);
    var records = new List<ForecastRecord>();
    foreach (var hours in new[] { 1, 4, 24 })
    {
        var horizon = TimeSpan.FromHours(hours);
        var slots = WalkForward.Slots(new Period(from, to), horizon, stride);
        records.AddRange(WalkForwardRunner.Run(bars, slots, horizon, model.Forecast, model.Settings.HistoryFor(horizon)));
    }
    Console.WriteLine($"v1 baseline, {from:yyyy-MM-dd} to {to:yyyy-MM-dd}, a forecast every {stride.TotalMinutes} minutes (overlap = horizon / stride)");
    Console.WriteLine();
    Print(records, (int)stride.TotalMinutes);
    if (options.TryGetValue("out", out var outPath)) WriteCsv(outPath, records);
    return 0;
}

async Task<int> Compare()
{
    var (from, to) = Period();
    var stride = TimeSpan.FromMinutes(Int("stride", 15));
    var baseline = new BaselineModel();
    // --vol-conditioning on its own tests just that; --half-life on its own (default 60) keeps the original comparison.
    var conditioning = Double("vol-conditioning", 0);
    var halfLife = Int("half-life", conditioning > 0 ? baseline.Settings.HalfLifeMinutes : 60);
    int[]? conditioningHours = options.TryGetValue("vol-conditioning-horizons", out var hs)
        ? hs.Split(',').Select(h => int.Parse(h.Trim().TrimEnd('h'), CultureInfo.InvariantCulture)).ToArray()
        : null;
    var candidate = new BaselineModel(BaselineSettings.Default with
    {
        HalfLifeMinutes = halfLife, VolConditioningFraction = conditioning, VolConditioningHorizonsHours = conditioningHours,
    });
    var split = WalkForward.Halves(from, to);
    var bars = await LoadBars(from, to, baseline.Settings);
    var rule = new DecisionRule();

    var changes = new List<string>();
    if (halfLife != baseline.Settings.HalfLifeMinutes) changes.Add($"a {halfLife}-minute half-life");
    if (conditioning > 0)
        changes.Add($"calibration on the nearest {conditioning:P0} of past outcomes by volatility" +
            (conditioningHours is null ? "" : $" at {string.Join(", ", conditioningHours.Select(h => h + "h"))} only"));
    Console.WriteLine($"Candidate: v1 with {(changes.Count > 0 ? string.Join(" and ", changes) : "no changes")}; baseline: v1 as shipped.");
    Console.WriteLine($"Tune on {split.Tune}, then test once on {split.Test}. Decision rule fixed in advance: {rule}.");
    foreach (var (label, period) in new[] { ("Tuning half", split.Tune), ("Holdout (scored once)", split.Test) })
    {
        Console.WriteLine();
        Console.WriteLine($"== {label}: {period} ==");
        Console.WriteLine($"{"Horizon",-8}{"Baseline",10}{"Candidate",11}{"Change",9}{"p (DM)",9}  Hit90 cand. (±m)   Hit50 cand. (±m)");
        var comparisons = new List<HorizonComparison>();
        foreach (var hours in new[] { 1, 4, 24 })
        {
            var horizon = TimeSpan.FromHours(hours);
            var slots = WalkForward.Slots(period, horizon, stride).ToList();
            var b = WalkForwardRunner.Run(bars, slots, horizon, baseline.Forecast, baseline.Settings.HistoryFor(horizon)).Where(r => r.IsScored).ToDictionary(r => r.ForecastMadeUtc);
            var c = WalkForwardRunner.Run(bars, slots, horizon, candidate.Forecast, candidate.Settings.HistoryFor(horizon)).Where(r => r.IsScored).ToDictionary(r => r.ForecastMadeUtc);
            var both = b.Keys.Intersect(c.Keys).Order().ToList();
            if (both.Count < 2) { Console.WriteLine($"{hours}h: not enough data"); continue; }

            double Combined(ForecastRecord r) =>
                (Score.IntervalScore(r.Low90!.Value, r.High90!.Value, r.ActualPrice!.Value, 90) + Score.IntervalScore(r.Low50!.Value, r.High50!.Value, r.ActualPrice!.Value, 50)) / r.ReferencePrice * 100;
            var diffs = both.Select(t => Combined(c[t]) - Combined(b[t])).ToArray();
            var steps = (int)(horizon / VolatilityBand.BarSize); // DM lag = horizon in 5-minute steps, same as the studies
            var p = DieboldMariano.PValue(diffs, steps);
            var bScore = both.Average(t => Combined(b[t]));
            var cScore = both.Average(t => Combined(c[t]));
            var change = (cScore / bScore - 1) * 100;
            var report = Evaluation.Evaluate(both.Select(t => c[t]), hours * 60, (int)stride.TotalMinutes);
            var months = both.GroupBy(t => t.ToString("yyyy-MM")).Select(g => g.Sum(t => Combined(b[t]) - Combined(c[t]))).ToList();
            comparisons.Add(new HorizonComparison(hours * 60, change, p, report.Range90!.IsCalibrated, report.Range50!.IsCalibrated, months));
            Console.WriteLine($"{hours + "h",-8}{bScore,10:F3}{cScore,11:F3}{change,8:+0.00;-0.00}%{p,9:F3}  {report.Range90.HitRatePct,5:F1} (±{report.Range90.Margin95:F1}){(report.Range90.IsCalibrated ? "" : " !"),-6}  {report.Range50.HitRatePct,5:F1} (±{report.Range50.Margin95:F1}){(report.Range50.IsCalibrated ? "" : " !")}");
        }
        if (period == split.Test && comparisons.Count > 0)
        {
            var v = rule.Evaluate(comparisons);
            Console.WriteLine();
            Console.WriteLine($"Verdict on the holdout: rule 1 {Mark(v.Rule1)}, rule 2 {Mark(v.Rule2)}, rule 3 {Mark(v.Rule3)}, rule 4 {(v.Rule1 ? Mark(v.Rule4) : "n/a")} => {(v.Passes ? "PASSES" : "fails, keep the current model")}");
        }
    }
    return 0;

    static string Mark(bool ok) => ok ? "pass" : "fail";
}

void Print(IReadOnlyList<ForecastRecord> records, int intervalMinutes = 5)
{
    Console.WriteLine($"{"Horizon",-8}{"Scored",8}{"n_eff",8}  {"Hit90 (±margin95)",-20}{"Hit50 (±margin95)",-20}{"Width90",9}{"IS",8}{"IS90",8}{"IS50",8}  Misses below/above");
    foreach (var minutes in records.Select(r => r.HorizonMinutes).Distinct().Order())
    {
        var r = Evaluation.Evaluate(records, minutes, intervalMinutes);
        if (r.Range90 is not { } c90 || r.Range50 is not { } c50) { Console.WriteLine($"{minutes / 60}h: nothing scored yet"); continue; }
        Console.WriteLine($"{minutes / 60 + "h",-8}{r.Samples,8:N0}{c90.EffectiveSamples,8:F1}  {Cov(c90),-20}{Cov(c50),-20}{c90.MeanWidthPct,8:F3}%{r.IntervalScorePct,8:F3}{r.IntervalScore90Pct,8:F3}{r.IntervalScore50Pct,8:F3}  {r.Misses90Below}/{r.Misses90Above}");
    }
    Console.WriteLine();
    Console.WriteLine("Hit rates are % of outcomes inside the range; margin95 uses n_eff = n / overlap. ! = outside its margin (not calibrated).");
    Console.WriteLine("IS = interval score of the 90% + 50% ranges, % of price (lower is better). Width90 = average full width of the 90% range.");

    static string Cov(Coverage c) => $"{c.HitRatePct:F1} (±{c.Margin95:F1}){(c.IsCalibrated ? "" : " !")}";
}

async Task<List<PriceSample>> LoadBars(DateTime from, DateTime to, BaselineSettings settings)
{
    // History before `from` for the first forecasts (24h needs 14 days of calibration), and after `to` for the last outcomes.
    var warmup = settings.HistoryFor(TimeSpan.FromHours(24));
    return await new BinanceBars(Cache()).LoadAsync(from - warmup - TimeSpan.FromDays(1), to + TimeSpan.FromDays(1), Console.Error.WriteLine);
}

void WriteCsv(string path, IEnumerable<ForecastRecord> records)
{
    static string F(double? v) => v is { } x ? x.ToString("R", CultureInfo.InvariantCulture) : "";
    static string B(bool? v) => v is { } b ? (b ? "true" : "false") : "";
    static string T(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    File.WriteAllLines(path, records.Select(r =>
    {
        bool? In(double? lo, double? hi) => r.ActualPrice is { } a && lo is { } l && hi is { } h ? Score.Inside(l, h, a) : null;
        var miss = r.ActualPrice is { } a2 ? a2 > r.High90 ? "above" : a2 < r.Low90 ? "below" : "" : "";
        return string.Join(",", T(r.ForecastMadeUtc), T(r.TargetUtc), $"{r.HorizonMinutes / 60}h", r.HorizonMinutes, r.ModelRelease, F(r.ReferencePrice),
            F(r.Low50), F(r.High50), F(r.Low90), F(r.High90), "binance", r.Status, F(r.ActualPrice), B(In(r.Low50, r.High50)), B(In(r.Low90, r.High90)), miss);
    }).Prepend(ForecastCsv.Header));
    Console.WriteLine($"Wrote {path}");
}

(DateTime From, DateTime To) Period()
{
    DateTime Date(string name) => options.TryGetValue(name, out var v)
        ? DateTime.SpecifyKind(DateTime.Parse(v, CultureInfo.InvariantCulture), DateTimeKind.Utc)
        : throw new ArgumentException($"--{name} YYYY-MM-DD is required.");
    var (from, to) = (Date("from"), Date("to"));
    return from < to ? (from, to) : throw new ArgumentException("--from must be before --to.");
}

int Int(string name, int fallback) => options.TryGetValue(name, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

double Double(string name, double fallback) =>
    options.TryGetValue(name, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;

string Cache() => options.TryGetValue("cache", out var c) ? c : Path.Combine("data", "cache");

static Dictionary<string, string> Options(string[] a)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < a.Length - 1; i++)
        if (a[i].StartsWith("--")) result[a[i][2..]] = a[++i];
    return result;
}
