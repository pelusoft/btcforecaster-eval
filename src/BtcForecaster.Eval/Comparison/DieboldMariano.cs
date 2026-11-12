using BtcForecaster.Eval.Baseline;

namespace BtcForecaster.Eval.Comparison;

/// <summary>
/// Diebold-Mariano test, as used in BtcForecaster's studies: is one forecaster really better than another, or just lucky on
/// this sample? Takes the per-forecast loss differences d_i = loss(candidate)_i - loss(baseline)_i (here, interval scores)
/// and tests whether their mean is zero. Overlapping forecasts make the d_i strongly autocorrelated, so the variance of the
/// mean is the Newey-West (HAC, Bartlett kernel) long-run variance with <c>lag</c> = the horizon in forecast steps
/// (12 / 48 / 288 for 1h / 4h / 24h at 5 minutes), not the naive variance.
/// </summary>
public static class DieboldMariano
{
    /// <param name="MeanDifference">Mean of d (negative = the candidate's loss is lower).</param>
    /// <param name="Statistic">z = mean / sqrt(long-run variance / n).</param>
    /// <param name="PValue">Two-sided p-value against the standard normal.</param>
    public sealed record Result(int N, double MeanDifference, double LongRunVariance, double Statistic, double PValue);

    /// <summary>Two-sided p-value that the mean loss difference is zero; NaN with fewer than 2 values or zero variance.</summary>
    public static double PValue(IReadOnlyList<double> differences, int lag) => Test(differences, lag)?.PValue ?? double.NaN;

    public static Result? Test(IReadOnlyList<double> d, int lag)
    {
        var n = d.Count;
        if (n < 2) return null;
        var lrv = NeweyWestVariance(d, lag);
        if (!(lrv > 0)) return null;
        var mean = d.Average();
        var z = mean / Math.Sqrt(lrv / n);
        return new Result(n, mean, lrv, z, 2 * (1 - NormalMath.Cdf(Math.Abs(z))));
    }

    /// <summary>
    /// Newey-West long-run variance: g(0) + 2 * sum_{k=1..L} (1 - k / (L + 1)) * g(k), where g(k) is the lag-k autocovariance
    /// (divided by n) and L = min(<paramref name="lag"/>, n - 1).
    /// </summary>
    public static double NeweyWestVariance(IReadOnlyList<double> d, int lag)
    {
        var n = d.Count;
        var mean = d.Average();
        double Gamma(int k)
        {
            var sum = 0.0;
            for (var i = k; i < n; i++) sum += (d[i] - mean) * (d[i - k] - mean);
            return sum / n;
        }
        var lrv = Gamma(0);
        for (var k = 1; k <= Math.Min(lag, n - 1); k++) lrv += 2 * (1 - k / (lag + 1.0)) * Gamma(k);
        return lrv;
    }

    // Kept for existing callers; same implementation as NormalMath.Cdf.
    public static double NormalCdf(double x) => NormalMath.Cdf(x);
}
