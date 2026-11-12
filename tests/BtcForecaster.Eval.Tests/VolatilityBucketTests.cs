using BtcForecaster.Eval.Baseline;
using BtcForecaster.Eval.Scoring;

namespace BtcForecaster.Eval.Tests;

/// <summary>
/// Misses by volatility bucket and tail. The worked example is the same as in BtcForecaster's own tests, so the two
/// implementations of byVolatilityBucket agree on it.
/// </summary>
public class VolatilityBucketTests
{
    // Reference 100, 90% range 95..105.
    private static TailSample T(double actual, VolatilityBucket bucket) => new(100, actual, 95, 105, bucket);

    private static VolatilityTailCell Cell(VolatilityBucketReport r, string bucket, string tail) =>
        r.Cells.Single(c => c.Bucket == bucket && c.Tail == tail);

    [Fact]
    public void Counts_misses_coverage_and_signed_miss_size_per_bucket_and_tail()
    {
        var samples = new[]
        {
            T(100, VolatilityBucket.Calm), T(94, VolatilityBucket.Calm), T(107, VolatilityBucket.Calm), T(93, VolatilityBucket.Calm),
            T(100, VolatilityBucket.Normal), T(95, VolatilityBucket.Normal),   // the edge counts as inside
            T(110, VolatilityBucket.Active),
            T(50, VolatilityBucket.Unknown),
        };

        var r = VolatilityBuckets.Build(samples, overlap: 1);

        Assert.Equal(6, r.Cells.Count);
        Assert.Equal(1, r.Unclassified);
        var calmBelow = Cell(r, "calm", "below");
        Assert.Equal((4, 2), (calmBelow.Samples, calmBelow.Misses));
        Assert.Equal(50, calmBelow.TailCoveragePct!.Value, 9);
        Assert.Equal(-1.5, calmBelow.MeanSignedMissPct!.Value, 9);   // 1% and 2% below the lower edge
        Assert.Equal(75, Cell(r, "calm", "above").TailCoveragePct!.Value, 9);
        Assert.Equal(2, Cell(r, "calm", "above").MeanSignedMissPct!.Value, 9);
        Assert.All(r.Cells.Where(c => c.Bucket == "normal"), c =>
        {
            Assert.Equal(0, c.Misses);
            Assert.Null(c.MeanSignedMissPct);
        });
        Assert.Equal(0, Cell(r, "active", "above").TailCoveragePct!.Value, 9);
        Assert.Equal(5, Cell(r, "active", "above").MeanSignedMissPct!.Value, 9);
        Assert.Equal(Score.Margin95(95, 4, 1), calmBelow.Margin95, 12);
    }

    [Fact]
    public void Overlap_shrinks_the_effective_sample_and_an_empty_bucket_has_no_rates()
    {
        var samples = Enumerable.Range(0, 120).Select(i => T(i % 20 == 0 ? 106 : 100, VolatilityBucket.Normal)).ToList();
        var r = VolatilityBuckets.Build(samples, overlap: 12);

        Assert.Equal(10, Cell(r, "normal", "above").EffectiveSamples, 9);
        var calm = Cell(r, "calm", "below");
        Assert.Equal((0, 0.0), (calm.Samples, calm.EffectiveSamples));
        Assert.Null(calm.TailCoveragePct);
    }

    [Theory]
    [InlineData(0.738, VolatilityBucket.Calm)]
    [InlineData(0.7381, VolatilityBucket.Normal)]
    [InlineData(1.126, VolatilityBucket.Normal)]
    [InlineData(1.1261, VolatilityBucket.Active)]
    [InlineData(double.NaN, VolatilityBucket.Unknown)]
    public void Thresholds_are_the_fixed_january_to_may_terciles(double ratio, VolatilityBucket expected) =>
        Assert.Equal(expected, VolatilityBuckets.Classify(ratio));

    /// <summary>5-minute bars with volatility that switches every few days, so every bucket occurs.</summary>
    private static List<PriceSample> Bars(int days)
    {
        var rng = new Random(5);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var price = 80_000.0;
        var bars = new List<PriceSample>();
        for (var i = 1; i <= days * 288; i++)
        {
            var sigma = (i / 288 / 3 % 3) switch { 0 => 0.0006, 1 => 0.0012, _ => 0.0025 };
            var z = Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
            price *= Math.Exp(sigma * z);
            bars.Add(new PriceSample(start.AddMinutes(5 * i), price));
        }
        return bars;
    }

    [Fact]
    public void The_bucket_at_a_forecast_time_ignores_every_later_bar()
    {
        var bars = Bars(45);
        var clean = VolatilityBuckets.Classifier(bars);
        var seen = new HashSet<VolatilityBucket>();
        foreach (var cutoff in new[] { 32, 35, 38, 41 }.SelectMany(d => new[] { 0, 5, 65, 600 }.Select(m => bars[0].Timestamp.AddDays(d).AddMinutes(m))))
        {
            var poisoned = bars.Select((b, i) => b.Timestamp <= cutoff ? b : b with { Price = b.Price * (1 + 0.05 * (i % 13)) }).ToList();
            Assert.Equal(clean(cutoff), VolatilityBuckets.Classifier(poisoned)(cutoff));
            seen.Add(clean(cutoff));
        }
        Assert.DoesNotContain(VolatilityBucket.Unknown, seen);
        Assert.True(seen.Count >= 2, string.Join(", ", seen));
        Assert.Equal(VolatilityBucket.Unknown, clean(bars[0].Timestamp.AddDays(20)));   // under 30 days + 24 hours of bars
    }

    [Fact]
    public void Per_horizon_report_uses_scored_forecasts_and_the_bucket_when_each_was_made()
    {
        var t = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        ForecastRecord F(int i, double? actual, string status = "scored") =>
            new(t.AddMinutes(5 * i), t.AddMinutes(5 * i + 60), 60, "test", 100, 99.5, 100.5, 99, 101, status, actual);
        var records = new[] { F(0, 100), F(1, 98), F(2, 101.5), F(3, null, "pending"), F(4, 97, "excluded") };
        VolatilityBucket At(DateTime made) => made < t.AddMinutes(10) ? VolatilityBucket.Calm : VolatilityBucket.Active;

        var r = VolatilityBuckets.ForHorizon(records, 60, At);

        Assert.Equal((2, 1), (Cell(r, "calm", "below").Samples, Cell(r, "calm", "below").Misses));
        Assert.Equal(-1, Cell(r, "calm", "below").MeanSignedMissPct!.Value, 9);
        Assert.Equal((1, 1), (Cell(r, "active", "above").Samples, Cell(r, "active", "above").Misses));
        Assert.Equal(0, r.Cells.Where(c => c.Bucket == "normal").Sum(c => c.Samples));
    }
}
