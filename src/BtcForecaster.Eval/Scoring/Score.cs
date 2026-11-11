namespace BtcForecaster.Eval.Scoring;

/// <summary>
/// The building blocks BtcForecaster scores itself with. Pure functions, no I/O; the same formulas as the live site.
/// </summary>
public static class Score
{
    /// <summary>
    /// Forecasts made every few minutes overlap: with a forecast every 5 minutes and a 1-hour horizon, 12 consecutive forecasts
    /// share each outcome window, so they are far from independent. The effective sample size is n ÷ overlap (at least 1).
    /// </summary>
    public static double EffectiveSampleSize(int n, int overlap) => Math.Max(1.0, (double)n / Math.Max(1, overlap));

    /// <summary>Overlap for a horizon and forecast interval: horizon ÷ interval (12 for 1 hour every 5 minutes; 288 for 24 hours).</summary>
    public static int Overlap(int horizonMinutes, int intervalMinutes = 5) => Math.Max(1, horizonMinutes / Math.Max(1, intervalMinutes));

    /// <summary>
    /// 95% margin of error, in percentage points, for a hit rate whose true value is <paramref name="targetPct"/>:
    /// 1.96 × √(p(1 − p) / n_eff) × 100, with n_eff = n ÷ overlap. A range is calibrated when |hit rate − target| ≤ margin95.
    /// </summary>
    public static double Margin95(double targetPct, int n, int overlap = 1)
    {
        var p = targetPct / 100;
        return 1.96 * Math.Sqrt(p * (1 - p) / EffectiveSampleSize(n, overlap)) * 100;
    }

    /// <summary>
    /// Interval score (Gneiting &amp; Raftery 2007) of a central range at <paramref name="levelPct"/>: its width, plus
    /// (2 ÷ α) × how far outside the range the price landed, with α = 1 − level. Lower is better; same units as the prices.
    /// It rewards narrow ranges but punishes misses in proportion to their size, so a forecaster can't game it by being
    /// too narrow (big penalties) or too wide (big widths).
    /// </summary>
    public static double IntervalScore(double lower, double upper, double actual, double levelPct)
    {
        var alpha = 1 - levelPct / 100;
        var score = upper - lower;
        if (actual < lower) score += 2 / alpha * (lower - actual);
        if (actual > upper) score += 2 / alpha * (actual - upper);
        return score;
    }

    /// <summary>Inside the range, edges included.</summary>
    public static bool Inside(double lower, double upper, double actual) => actual >= lower && actual <= upper;

    /// <summary>Percent of forecasts (that have the range) whose outcome landed inside it; null if none have it.</summary>
    public static double? HitRatePct(IEnumerable<ForecastRecord> scored, Func<ForecastRecord, (double? Low, double? High)> range)
    {
        int n = 0, inside = 0;
        foreach (var f in scored)
        {
            if (range(f) is not ({ } lo, { } hi) || f.ActualPrice is not { } actual) continue;
            n++;
            if (Inside(lo, hi, actual)) inside++;
        }
        return n == 0 ? null : 100.0 * inside / n;
    }

    /// <summary>Mean interval score of one range level, as % of the reference price (the range centre); null if no forecast has the range.</summary>
    public static double? MeanIntervalScorePct(IEnumerable<ForecastRecord> scored, Func<ForecastRecord, (double? Low, double? High)> range, double levelPct)
    {
        double sum = 0;
        var n = 0;
        foreach (var f in scored)
        {
            if (range(f) is not ({ } lo, { } hi) || f.ActualPrice is not { } actual || f.ReferencePrice <= 0) continue;
            sum += IntervalScore(lo, hi, actual, levelPct) / f.ReferencePrice * 100;
            n++;
        }
        return n == 0 ? null : sum / n;
    }

    public static (double? Low, double? High) Range50(ForecastRecord f) => (f.Low50, f.High50);
    public static (double? Low, double? High) Range90(ForecastRecord f) => (f.Low90, f.High90);
}
