namespace BtcForecaster.Eval.Scoring;

/// <summary>How well one range level matched reality.</summary>
/// <param name="TargetPct">The range's level: a calibrated 90% range contains the price 90% of the time.</param>
/// <param name="Samples">Scored forecasts that had this range.</param>
/// <param name="EffectiveSamples">Samples ÷ overlap: roughly how many independent outcomes they amount to.</param>
/// <param name="HitRatePct">Percent whose outcome landed inside the range (edges count as inside).</param>
/// <param name="Margin95">How far the hit rate can drift from the target by chance (95%), in percentage points.</param>
/// <param name="MeanWidthPct">Average full width of the range, % of the reference price.</param>
public sealed record Coverage(double TargetPct, int Samples, double EffectiveSamples, double HitRatePct, double Margin95, double MeanWidthPct)
{
    /// <summary>The hit rate is within its margin of the target.</summary>
    public bool IsCalibrated => Math.Abs(HitRatePct - TargetPct) <= Margin95;
}

public sealed record DailyCoverage(DateOnly Day, int Samples, double? Hit90Pct, double? Hit50Pct);

/// <summary>Coverage over the trailing <c>days</c> days ending with <paramref name="Day"/> (outcomes with target times in (end of day − days, end of day]).</summary>
/// <param name="FullWindow">The data covers the whole window (earlier days are partial).</param>
public sealed record RollingCoverage(DateOnly Day, int Samples, double EffectiveSamples, double? Hit90Pct, double? Margin90, double? Hit50Pct, double? Margin50, bool FullWindow);

/// <summary>Everything BtcForecaster publishes about one model at one horizon.</summary>
/// <param name="IntervalScorePct">Mean interval score of the 90% plus the 50% range, % of price. Lower is better.</param>
/// <param name="Misses90Above">Outcomes above the 90% range; roughly equal to <paramref name="Misses90Below"/> if the centre is unbiased.</param>
/// <param name="Daily">Per calendar day of the target time (UTC). Noisy at long horizons: use <paramref name="Rolling7d"/>.</param>
public sealed record HorizonReport(
    int HorizonMinutes, int Overlap, int Samples,
    Coverage? Range90, Coverage? Range50,
    double? IntervalScorePct, double? IntervalScore90Pct, double? IntervalScore50Pct,
    int Misses90Above, int Misses90Below,
    IReadOnlyList<DailyCoverage> Daily, IReadOnlyList<RollingCoverage> Rolling7d);

/// <summary>Turns scored forecasts into the report BtcForecaster publishes (its live-stats), one horizon at a time.</summary>
public static class Evaluation
{
    /// <summary>
    /// Scores the forecasts at one horizon. Only <see cref="ForecastRecord.IsScored"/> records count; pending and excluded ones
    /// are ignored. Overlap defaults to horizon ÷ 5 minutes (a forecast every 5 minutes).
    /// </summary>
    public static HorizonReport Evaluate(IEnumerable<ForecastRecord> forecasts, int horizonMinutes, int intervalMinutes = 5)
    {
        var scored = forecasts.Where(f => f.HorizonMinutes == horizonMinutes && f.IsScored).OrderBy(f => f.TargetUtc).ToList();
        var overlap = Score.Overlap(horizonMinutes, intervalMinutes);
        var is90 = Score.MeanIntervalScorePct(scored, Score.Range90, 90);
        var is50 = Score.MeanIntervalScorePct(scored, Score.Range50, 50);
        return new HorizonReport(
            horizonMinutes, overlap, scored.Count,
            Coverage(scored, 90, Score.Range90, overlap),
            Coverage(scored, 50, Score.Range50, overlap),
            is90 is { } a && is50 is { } b ? a + b : null, is90, is50,
            scored.Count(f => f.High90 is { } hi && f.ActualPrice > hi),
            scored.Count(f => f.Low90 is { } lo && f.ActualPrice < lo),
            scored.GroupBy(f => DateOnly.FromDateTime(f.TargetUtc)).OrderBy(g => g.Key)
                .Select(g => new DailyCoverage(g.Key, g.Count(), Score.HitRatePct(g, Score.Range90), Score.HitRatePct(g, Score.Range50)))
                .ToList(),
            Rolling(scored, overlap, 7));
    }

    /// <summary>Hit rate, margin and mean width of one range level over the forecasts that have both its edges; null if none.</summary>
    public static Coverage? Coverage(IReadOnlyCollection<ForecastRecord> scored, double targetPct, Func<ForecastRecord, (double? Low, double? High)> range, int overlap)
    {
        int inside = 0, n = 0;
        double widthSum = 0;
        foreach (var f in scored)
        {
            if (range(f) is not ({ } lo, { } hi) || f.ActualPrice is not { } actual) continue;
            n++;
            if (Score.Inside(lo, hi, actual)) inside++;
            widthSum += (hi - lo) / f.ReferencePrice * 100;
        }
        return n == 0 ? null : new Coverage(targetPct, n, Score.EffectiveSampleSize(n, overlap), 100.0 * inside / n, Score.Margin95(targetPct, n, overlap), widthSum / n);
    }

    /// <summary>
    /// Coverage over trailing windows of <paramref name="days"/> days, one per UTC calendar day from the first outcome to the
    /// last. Each window holds outcomes with target times in (end of day − days, end of day], never anything later.
    /// </summary>
    public static List<RollingCoverage> Rolling(IReadOnlyList<ForecastRecord> scored, int overlap, int days)
    {
        var result = new List<RollingCoverage>();
        if (scored.Count == 0) return result;
        var sorted = scored.OrderBy(f => f.TargetUtc).ToArray();
        var first = sorted[0].TargetUtc;
        for (var day = DateOnly.FromDateTime(first); day <= DateOnly.FromDateTime(sorted[^1].TargetUtc); day = day.AddDays(1))
        {
            var end = day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var start = end.AddDays(-days);
            var window = sorted.Where(f => f.TargetUtc > start && f.TargetUtc <= end).ToList();
            if (window.Count == 0) continue;
            var hit90 = Score.HitRatePct(window, Score.Range90);
            var hit50 = Score.HitRatePct(window, Score.Range50);
            var n90 = window.Count(f => f.Low90 is not null && f.High90 is not null);
            var n50 = window.Count(f => f.Low50 is not null && f.High50 is not null);
            result.Add(new RollingCoverage(day, window.Count, Score.EffectiveSampleSize(window.Count, overlap),
                hit90, hit90 is null ? null : Score.Margin95(90, n90, overlap),
                hit50, hit50 is null ? null : Score.Margin95(50, n50, overlap),
                FullWindow: start >= first.AddMinutes(-1)));
        }
        return result;
    }
}
