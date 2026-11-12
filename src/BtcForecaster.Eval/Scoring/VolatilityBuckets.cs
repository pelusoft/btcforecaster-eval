using BtcForecaster.Eval.Baseline;

namespace BtcForecaster.Eval.Scoring;

public enum VolatilityBucket { Unknown, Calm, Normal, Active }

/// <summary>One scored forecast for the tail table: its 90% range, outcome and volatility bucket at forecast time.</summary>
/// <param name="ReferencePrice">Price when the forecast was made; miss sizes are a % of it.</param>
public readonly record struct TailSample(double ReferencePrice, double Actual, double Lower90, double Upper90, VolatilityBucket Bucket);

/// <summary>One tail of the 90% range within one volatility bucket.</summary>
/// <param name="Bucket">"calm", "normal" or "active".</param>
/// <param name="Tail">"below" or "above" the 90% range.</param>
/// <param name="Samples">Scored forecasts in the bucket (the same for both tails).</param>
/// <param name="EffectiveSamples">Samples / overlap: roughly how many independent outcomes they amount to.</param>
/// <param name="Misses">Outcomes beyond this edge of the 90% range.</param>
/// <param name="TailCoveragePct">Percent of the bucket's outcomes not beyond this edge. A calibrated central 90% range holds 95% on each side.</param>
/// <param name="Margin95">How far tail coverage can drift from 95% by chance, in percentage points, using the effective sample size.</param>
/// <param name="MeanSignedMissPct">Mean distance of this tail's misses from the edge, % of the reference price: negative below, positive above.</param>
public sealed record VolatilityTailCell(
    string Bucket, string Tail, int Samples, double EffectiveSamples, int Misses,
    double? TailCoveragePct, double Margin95, double? MeanSignedMissPct);

/// <param name="Unclassified">Scored forecasts left out because the bucket wasn't known at forecast time (under 31 days of prices before it).</param>
public sealed record VolatilityBucketReport(
    double CalmMax, double ActiveAbove, double TailTargetPct, int Unclassified, IReadOnlyList<VolatilityTailCell> Cells);

/// <summary>
/// Misses of the 90% range by volatility bucket at forecast time and by tail: the <c>byVolatilityBucket</c> block of
/// BtcForecaster's live-stats, computed the same way. Reporting only; it changes no score.
/// <para>
/// Buckets: the last 24 hours' realised variance of 5-minute returns, divided by the median of that same measure at the
/// hourly checkpoints of the trailing 30 days, read at the latest hourly checkpoint at or before the forecast. Calm up to
/// <see cref="CalmMax"/>, active above <see cref="ActiveAbove"/>: the terciles of January-May 2026, fixed since.
/// </para>
/// </summary>
public static class VolatilityBuckets
{
    public const double CalmMax = 0.738, ActiveAbove = 1.126;

    /// <summary>Each tail of a central 90% range should hold 95%: 5% of outcomes beyond each edge.</summary>
    public const double TailTargetPct = 95;

    private static readonly (VolatilityBucket Bucket, string Name)[] Buckets =
        [(VolatilityBucket.Calm, "calm"), (VolatilityBucket.Normal, "normal"), (VolatilityBucket.Active, "active")];

    public static VolatilityBucket Classify(double ratio) =>
        !double.IsFinite(ratio) ? VolatilityBucket.Unknown
        : ratio <= CalmMax ? VolatilityBucket.Calm
        : ratio <= ActiveAbove ? VolatilityBucket.Normal
        : VolatilityBucket.Active;

    /// <param name="overlap">Consecutive forecasts sharing each outcome window (horizon / forecast interval).</param>
    public static VolatilityBucketReport Build(IReadOnlyCollection<TailSample> samples, int overlap)
    {
        var cells = new List<VolatilityTailCell>(6);
        foreach (var (bucket, name) in Buckets)
        {
            var inBucket = samples.Where(s => s.Bucket == bucket).ToList();
            cells.Add(Cell(name, "below", inBucket, s => s.Actual < s.Lower90 ? s.Actual - s.Lower90 : null, overlap));
            cells.Add(Cell(name, "above", inBucket, s => s.Actual > s.Upper90 ? s.Actual - s.Upper90 : null, overlap));
        }
        return new VolatilityBucketReport(CalmMax, ActiveAbove, TailTargetPct, samples.Count(s => s.Bucket == VolatilityBucket.Unknown), cells);
    }

    /// <summary>The report for the scored forecasts at one horizon, each bucketed at the time it was made.</summary>
    public static VolatilityBucketReport ForHorizon(
        IEnumerable<ForecastRecord> forecasts, int horizonMinutes, Func<DateTime, VolatilityBucket> bucketAt, int intervalMinutes = 5) =>
        Build(forecasts
                .Where(f => f.HorizonMinutes == horizonMinutes && f.IsScored && f.Low90 is not null && f.High90 is not null && f.ReferencePrice > 0)
                .Select(f => new TailSample(f.ReferencePrice, f.ActualPrice!.Value, f.Low90!.Value, f.High90!.Value, bucketAt(f.ForecastMadeUtc)))
                .ToList(),
            Score.Overlap(horizonMinutes, intervalMinutes));

    private static VolatilityTailCell Cell(string bucket, string tail, List<TailSample> inBucket, Func<TailSample, double?> beyond, int overlap)
    {
        var n = inBucket.Count;
        var misses = inBucket.Select(s => beyond(s) is { } d ? d / s.ReferencePrice * 100 : (double?)null).OfType<double>().ToList();
        return new VolatilityTailCell(bucket, tail, n,
            n == 0 ? 0 : Score.EffectiveSampleSize(n, overlap),
            misses.Count,
            n == 0 ? null : 100.0 * (n - misses.Count) / n,
            Score.Margin95(TailTargetPct, n, overlap),
            misses.Count == 0 ? null : misses.Average());
    }

    /// <summary>
    /// The bucket at any time, from 5-minute bars (oldest first, e.g. <see cref="Data.BinanceBars"/>). Only bars at or before
    /// a time affect its bucket; times without 30 days + 24 hours of bars before them are <see cref="VolatilityBucket.Unknown"/>.
    /// </summary>
    public static Func<DateTime, VolatilityBucket> Classifier(IReadOnlyList<PriceSample> bars, int trailingDays = 30)
    {
        const int barsPerDay = 288, barsPerHour = 12;
        const double varianceFloor = 1e-10;   // as on the site, so a flat stretch doesn't divide by zero
        static VolatilityBucket None(DateTime _) => VolatilityBucket.Unknown;
        if (bars.Count < 2) return None;

        var step = VolatilityBand.BarSize;
        var end = new DateTime(bars[^1].Timestamp.Ticks / step.Ticks * step.Ticks, DateTimeKind.Utc);
        var count = (int)((end - bars[0].Timestamp) / step) - 1;
        if (count <= barsPerDay || VolatilityBand.ResampleLast(bars, end, step, count) is not { } close) return None;
        var start = end - step * (count - 1);

        var squares = new double[count + 1];
        for (var i = 0; i < count; i++)
        {
            var r = i == 0 || close[i - 1] <= 0 || close[i] <= 0 ? 0 : Math.Log(close[i] / close[i - 1]);
            squares[i + 1] = squares[i] + r * r;
        }
        double DayVariance(int bar) => Math.Max(varianceFloor, (squares[bar + 1] - squares[bar + 1 - barsPerDay]) / barsPerDay);

        // Hourly checkpoints: bars closing on the hour.
        var first = 0;
        while (first < count && (start + step * first).Minute != 0) first++;
        var hours = first >= count ? 0 : (count - 1 - first) / barsPerHour + 1;
        var rv = new double[hours];
        for (var k = 0; k < hours; k++)
        {
            var bar = first + k * barsPerHour;
            rv[k] = bar - barsPerDay >= 0 ? DayVariance(bar) : double.NaN;
        }
        var window = trailingDays * 24;
        var ratio = new double[hours];
        var buffer = new double[window];
        for (var k = 0; k < hours; k++)
        {
            ratio[k] = double.NaN;
            if (k - window + 1 < 0) continue;
            Array.Copy(rv, k - window + 1, buffer, 0, window);
            if (buffer.Any(v => !double.IsFinite(v))) continue;
            Array.Sort(buffer);
            var median = window % 2 == 1 ? buffer[window / 2] : (buffer[window / 2 - 1] + buffer[window / 2]) / 2;
            if (median > 0) ratio[k] = rv[k] / median;
        }

        return t =>
        {
            var bar = (int)Math.Floor((t - start) / step);
            if (bar < first || bar >= count) return VolatilityBucket.Unknown;
            return Classify(ratio[(bar - first) / barsPerHour]);
        };
    }
}
