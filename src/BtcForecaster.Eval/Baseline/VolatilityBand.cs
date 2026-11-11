namespace BtcForecaster.Eval.Baseline;

/// <summary>A price observation (UTC). For 5-minute bars, the close, stamped at the bar's close time.</summary>
public readonly record struct PriceSample(DateTime Timestamp, double Price);

/// <summary>Central 90% and 50% ranges for the price at the horizon.</summary>
public sealed record PriceRange(double Lower90, double Upper90, double Lower50, double Upper50);

/// <summary>
/// The forecast distribution of the log price move to the horizon: a scale <see cref="Sigma"/> times a symmetric standardised
/// move whose size follows <see cref="SortedAbsRatios"/> (empirical, calibrated on past outcomes) or, without enough history,
/// a bell curve.
/// </summary>
/// <param name="Sigma">Forecast standard deviation of the log move to the horizon.</param>
/// <param name="SortedAbsRatios">Past |log move ÷ σ predicted at the time|, ascending; null = bell curve.</param>
public sealed record MoveDistribution(double Sigma, double[]? SortedAbsRatios)
{
    public bool IsCalibrated => SortedAbsRatios is not null;

    /// <summary>Size of standardised move that a fraction <paramref name="q"/> of outcomes stay within.</summary>
    public double AbsQuantile(double q) => SortedAbsRatios is { } s ? VolatilityBand.Quantile(s, q) : NormalMath.InverseAbsCdf(q);

    public PriceRange RangeAround(double centre)
    {
        double Edge(double q, int sign) => centre * Math.Exp(sign * AbsQuantile(q) * Sigma);
        return new PriceRange(Edge(0.90, -1), Edge(0.90, 1), Edge(0.50, -1), Edge(0.50, 1));
    }
}

/// <summary>
/// The v1 baseline's ranges (BtcForecaster's <c>v1-baseline</c>), in two steps:
/// <list type="number">
/// <item>Scale: an exponentially weighted σ of 5-minute log returns, projected to the horizon. Optionally seasonal: returns are
/// first divided by the typical activity of their UTC hour, and the forecast adds up the typical activity of the hours the
/// horizon covers.</item>
/// <item>Shape: the empirical |past move ÷ σ predicted at the time| over the calibration window, which captures Bitcoin's fat
/// tails. Falls back to a bell curve without at least a day of outcomes.</item>
/// </list>
/// Only prices up to <c>createdAt</c> are used. The ranges are symmetric (on a log scale) around the centre and deliberately not
/// shifted by recent drift, which would be a hidden momentum bet.
/// </summary>
public static class VolatilityBand
{
    /// <summary>Below this many past outcomes the empirical percentiles are too noisy; use the bell curve.</summary>
    public const int MinCalibrationSamples = 288;

    /// <summary>Pseudo-count (in 5-minute bars) shrinking each hour's activity toward the daily average.</summary>
    private const double SeasonalShrinkage = 36;

    public static readonly TimeSpan BarSize = TimeSpan.FromMinutes(5);

    /// <summary>History needed for the full calibration window.</summary>
    public static TimeSpan RequiredHistory(TimeSpan lookback, TimeSpan horizon, TimeSpan calibrationWindow) =>
        calibrationWindow + lookback + horizon + BarSize * 2;

    /// <param name="history">Prices up to <paramref name="createdAt"/> (never later), oldest first.</param>
    /// <param name="lookback">Warm-up window for σ (or the whole equal-weight window when the half-life is zero).</param>
    /// <param name="calibrationWindow">How far back to measure past outcomes; zero = bell-curve multipliers.</param>
    /// <param name="volatilityHalfLife">Exponential weighting half-life; zero = equal weights over the lookback.</param>
    /// <param name="seasonal">Adjust for typical activity by UTC hour of day (needs calibration history).</param>
    public static MoveDistribution? Distribution(
        IReadOnlyList<PriceSample> history, DateTime createdAt, TimeSpan horizon,
        TimeSpan lookback, TimeSpan calibrationWindow = default, TimeSpan volatilityHalfLife = default, bool seasonal = false)
    {
        var volBars = (int)(lookback / BarSize);
        var steps = Math.Max(1, (int)Math.Ceiling(horizon / BarSize));
        if (volBars < 2) return null;

        // Use the full calibration window if the history reaches back that far, otherwise what there is.
        var calibrationCount = (int)(calibrationWindow / BarSize);
        if (calibrationCount > 0 && history.Count > 0)
        {
            var availableBars = (int)((createdAt - history[0].Timestamp) / BarSize) - volBars - steps - 1;
            calibrationCount = Math.Min(calibrationCount, availableBars);
        }
        var calibrate = calibrationCount >= MinCalibrationSamples;
        var bars = calibrate ? ResampleLast(history, createdAt, BarSize, calibrationCount + volBars + steps + 1) : null;
        bars ??= ResampleLast(history, createdAt, BarSize, volBars + 1);
        if (bars is null) return null;
        calibrate &= bars.Length > volBars + steps + 1;

        var n = bars.Length;
        var squared = new double[n];
        for (var i = 1; i < n; i++)
        {
            var r = Math.Log(bars[i] / bars[i - 1]);
            squared[i] = r * r;
        }

        // Typical activity per bar, including the future bars the horizon covers (the clock is known in advance).
        int HourOf(int k) => (createdAt - BarSize * (n - 1 - k)).Hour;
        var activity = seasonal && calibrate ? HourOfDayActivity(squared, HourOf) : null;
        var weight = new double[n + steps];
        for (var k = 0; k < weight.Length; k++) weight[k] = activity?[HourOf(k)] ?? 1;

        var adjusted = activity is null ? squared : squared.Select((s, k) => s / weight[k]).ToArray();
        var variance = volatilityHalfLife > TimeSpan.Zero
            ? EwmaOfSquares(adjusted, volBars, volatilityHalfLife)
            : RollingMeanOfSquares(adjusted, volBars);

        var weightSums = new double[weight.Length + 1];
        for (var k = 0; k < weight.Length; k++) weightSums[k + 1] = weightSums[k] + weight[k];
        // Variance over bars j+1 … j+steps = per-bar level at j × the activity those bars usually have.
        double SigmaHorizonAt(int j) => Math.Sqrt(variance[j] * (weightSums[j + steps + 1] - weightSums[j + 1]));

        var ratios = calibrate ? CalibrationRatios(bars, volBars, steps, SigmaHorizonAt) : null;
        return new MoveDistribution(SigmaHorizonAt(n - 1), ratios);
    }

    /// <summary>
    /// Mean squared return per UTC hour, shrunk toward the overall mean and normalised so the 24 hours average 1.
    /// Busy hours get values above 1, quiet hours below.
    /// </summary>
    private static double[] HourOfDayActivity(double[] squared, Func<int, int> hourOf)
    {
        var sum = new double[24];
        var count = new double[24];
        for (var k = 1; k < squared.Length; k++)
        {
            sum[hourOf(k)] += squared[k];
            count[hourOf(k)]++;
        }
        var overall = sum.Sum() / Math.Max(1, count.Sum());
        if (overall <= 0) return Enumerable.Repeat(1.0, 24).ToArray();

        var activity = new double[24];
        for (var h = 0; h < 24; h++)
            activity[h] = (sum[h] + SeasonalShrinkage * overall) / (count[h] + SeasonalShrinkage) / overall;
        var mean = activity.Average();
        return activity.Select(a => a / mean).ToArray();
    }

    private static double[] RollingMeanOfSquares(double[] squared, int window)
    {
        var prefix = new double[squared.Length];
        for (var i = 1; i < squared.Length; i++) prefix[i] = prefix[i - 1] + squared[i];
        var mean = new double[squared.Length];
        for (var i = window; i < squared.Length; i++) mean[i] = (prefix[i] - prefix[i - window]) / window;
        return mean;
    }

    /// <summary>v_i = λ·v_(i−1) + (1 − λ)·x_i with λ = 0.5^(bar ÷ half-life), seeded with the plain mean of the warm-up values.</summary>
    private static double[] EwmaOfSquares(double[] squared, int warmup, TimeSpan halfLife)
    {
        var lambda = Math.Pow(0.5, BarSize / halfLife);
        var variance = new double[squared.Length];
        var seed = 0.0;
        for (var i = 1; i <= warmup; i++) seed += squared[i];
        variance[warmup] = seed / warmup;
        for (var i = warmup + 1; i < squared.Length; i++)
            variance[i] = lambda * variance[i - 1] + (1 - lambda) * squared[i];
        return variance;
    }

    /// <summary>|log move over the horizon ÷ σ predicted at its start| for every past bar whose outcome is already known, sorted.</summary>
    private static double[]? CalibrationRatios(double[] bars, int volBars, int steps, Func<int, double> sigmaHorizonAt)
    {
        var last = bars.Length - 1;
        var ratios = new List<double>(Math.Max(0, last - steps - volBars + 1));
        for (var j = volBars; j + steps <= last; j++)
        {
            var sigma = sigmaHorizonAt(j);
            if (sigma <= 0) continue;
            ratios.Add(Math.Abs(Math.Log(bars[j + steps] / bars[j])) / sigma);
        }
        if (ratios.Count < MinCalibrationSamples) return null;
        ratios.Sort();
        return ratios.ToArray();
    }

    /// <summary>Linear-interpolated quantile of an ascending-sorted list.</summary>
    public static double Quantile(IReadOnlyList<double> sorted, double q)
    {
        var pos = q * (sorted.Count - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    /// <summary>
    /// Evenly spaced bars ending at <paramref name="end"/>, each the last price at or before its time (forward fill).
    /// Null if the history doesn't reach back to the first bar.
    /// </summary>
    public static double[]? ResampleLast(IReadOnlyList<PriceSample> history, DateTime end, TimeSpan step, int count)
    {
        if (history.Count == 0 || count <= 0) return null;
        var firstBar = end - step * (count - 1);
        if (history[0].Timestamp > firstBar + step) return null;

        var result = new double[count];
        var i = 0;
        var current = history[0].Price;
        for (var k = 0; k < count; k++)
        {
            var barTime = firstBar + step * k;
            while (i < history.Count && history[i].Timestamp <= barTime) current = history[i++].Price;
            result[k] = current;
        }
        return result;
    }
}

/// <summary>Standard normal helpers for the bell-curve fallback.</summary>
public static class NormalMath
{
    /// <summary>Standard normal CDF (Abramowitz &amp; Stegun 7.1.26; absolute error below 1.5e-7).</summary>
    public static double Cdf(double x)
    {
        var t = 1 / (1 + 0.3275911 * Math.Abs(x) / Math.Sqrt(2));
        var y = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x / 2);
        return x >= 0 ? (1 + y) / 2 : (1 - y) / 2;
    }

    /// <summary>P(|Z| ≤ k) for a standard normal Z.</summary>
    public static double AbsCdf(double k) => k <= 0 ? 0 : 2 * Cdf(k) - 1;

    /// <summary>k such that P(|Z| ≤ k) = q, by bisection.</summary>
    public static double InverseAbsCdf(double q)
    {
        double lo = 0, hi = 10;
        for (var i = 0; i < 80; i++)
        {
            var mid = (lo + hi) / 2;
            if (AbsCdf(mid) < q) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }
}
