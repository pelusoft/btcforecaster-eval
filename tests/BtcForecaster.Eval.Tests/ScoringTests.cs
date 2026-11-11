using System.Text.Json;
using BtcForecaster.Eval.Scoring;

namespace BtcForecaster.Eval.Tests;

public class ScoringTests
{
    private static string Sample(string file) => Path.Combine(AppContext.BaseDirectory, "sample", file);

    [Theory]
    [InlineData(10, 12, 1.0)]       // fewer forecasts than the overlap still counts as one outcome
    [InlineData(120, 12, 10.0)]
    [InlineData(2016, 12, 168.0)]   // a week of 1-hour forecasts every 5 minutes
    [InlineData(2016, 288, 7.0)]    // a week of 24-hour forecasts: only 7 independent outcomes
    public void Effective_sample_size_is_n_over_overlap(int n, int overlap, double expected) =>
        Assert.Equal(expected, Score.EffectiveSampleSize(n, overlap), 12);

    [Theory]
    [InlineData(60, 12)]
    [InlineData(240, 48)]
    [InlineData(1440, 288)]
    public void Overlap_is_horizon_over_interval(int horizonMinutes, int expected) => Assert.Equal(expected, Score.Overlap(horizonMinutes));

    [Fact]
    public void Margin95_uses_the_effective_sample_size()
    {
        // 90% target, 2016 forecasts, overlap 12: n_eff = 168, margin = 1.96 × √(0.09 / 168) × 100 = 4.5365…
        Assert.Equal(1.96 * Math.Sqrt(0.9 * 0.1 / 168) * 100, Score.Margin95(90, 2016, 12), 12);
        Assert.Equal(4.5365, Score.Margin95(90, 2016, 12), 4);
        // Same forecasts at 24 hours: n_eff = 7, about ±22 points.
        Assert.Equal(22.22, Score.Margin95(90, 2016, 288), 2);
        // Ignoring the overlap would claim a margin √12 times too small.
        Assert.Equal(Score.Margin95(90, 2016, 12) / Math.Sqrt(12), Score.Margin95(90, 2016, 1), 12);
    }

    [Theory]
    [InlineData(99, 101, 100, 90, 2)]                 // inside: just the width
    [InlineData(99, 101, 99, 90, 2)]                  // on the edge counts as inside
    [InlineData(99, 101, 102, 90, 2 + 20 * 1)]        // 1 above a 90% range: + (2 ÷ 0.1) × 1
    [InlineData(99, 101, 97, 90, 2 + 20 * 2)]         // 2 below
    [InlineData(99.5, 100.5, 101.5, 50, 1 + 4 * 1)]   // 50% range: penalty (2 ÷ 0.5) = 4 per unit
    public void Interval_score_is_width_plus_scaled_miss_distance(double lo, double hi, double actual, double level, double expected) =>
        Assert.Equal(expected, Score.IntervalScore(lo, hi, actual, level), 10);

    [Fact]
    public void Evaluation_counts_coverage_misses_and_scores_only_scored_forecasts()
    {
        var t = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        ForecastRecord F(int i, double? actual, string status = "scored") =>
            new(t.AddMinutes(5 * i), t.AddMinutes(5 * i + 60), 60, "test", 100, 99.5, 100.5, 99, 101, status, actual);
        var records = new[]
        {
            F(0, 100), F(1, 100.7), F(2, 101.5), F(3, 98), F(4, 100.2),
            F(5, null, "pending"), F(6, null, "excluded"),
        };

        var r = Evaluation.Evaluate(records, 60);

        Assert.Equal(5, r.Samples);
        Assert.Equal(12, r.Overlap);
        Assert.Equal(60, r.Range90!.HitRatePct, 10);        // 100, 100.7, 100.2 inside [99, 101]
        Assert.Equal(40, r.Range50!.HitRatePct, 10);        // 100, 100.2 inside [99.5, 100.5]
        Assert.Equal(1, r.Misses90Above);
        Assert.Equal(1, r.Misses90Below);
        Assert.Equal(2.0, r.Range90.MeanWidthPct, 10);
        Assert.Equal(1.0, r.Range50.EffectiveSamples, 10);
        Assert.Equal(Score.Margin95(90, 5, 12), r.Range90.Margin95, 12);
        // IS90 per forecast (% of 100): 2, 2, 2 + 20 × 0.5, 2 + 20 × 1, 2.
        Assert.Equal((2 + 2 + 12 + 22 + 2) / 5.0, r.IntervalScore90Pct!.Value, 10);
        Assert.Equal(r.IntervalScore90Pct + r.IntervalScore50Pct, r.IntervalScorePct);
        Assert.Single(r.Daily);
    }

    [Fact]
    public void Rolling_windows_never_include_later_outcomes()
    {
        var day = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var records = Enumerable.Range(0, 10).Select(d =>
            new ForecastRecord(day.AddDays(d).AddHours(11), day.AddDays(d).AddHours(12), 60, "test", 100, 99.5, 100.5, 99, 101, "scored", d < 5 ? 100 : 200)).ToList();

        var rolling = Evaluation.Rolling(records, 12, 7);

        Assert.Equal(10, rolling.Count);
        Assert.All(rolling.Take(5), w => Assert.Equal(100, w.Hit90Pct));            // the misses from day 6 on aren't seen earlier
        Assert.Equal(100.0 * 2 / 7, rolling[^1].Hit90Pct!.Value, 10);                 // last window: days 4–10, 2 of 7 inside
        Assert.False(rolling[0].FullWindow);
        Assert.True(rolling[7].FullWindow);
    }

    [Fact]
    public void Sample_csv_outcome_columns_agree_with_the_ranges()
    {
        var lines = File.ReadAllLines(Sample(SampleFile())).Skip(1).Select(l => l.Split(',')).ToList();
        Assert.NotEmpty(lines);
        foreach (var f in lines.Where(f => f[11] == "scored"))
        {
            double D(int i) => double.Parse(f[i], System.Globalization.CultureInfo.InvariantCulture);
            var actual = D(12);
            Assert.Equal(Score.Inside(D(6), D(7), actual), bool.Parse(f[13]));
            Assert.Equal(Score.Inside(D(8), D(9), actual), bool.Parse(f[14]));
            Assert.Equal(actual > D(9) ? "above" : actual < D(8) ? "below" : "", f[15]);
        }
    }

    /// <summary>
    /// The point of this repository: scoring the sample with this library gives exactly the numbers BtcForecaster's own code
    /// computed for the same forecasts (data/sample/expected-results.json).
    /// </summary>
    [Fact]
    public void Scoring_the_sample_reproduces_the_sites_own_results()
    {
        using var expected = JsonDocument.Parse(File.ReadAllText(Sample("expected-results.json")));
        var records = ForecastCsv.Read(Sample(expected.RootElement.GetProperty("file").GetString()!));

        foreach (var h in expected.RootElement.GetProperty("horizons").EnumerateArray())
        {
            var minutes = h.GetProperty("horizonMinutes").GetInt32();
            var r = Evaluation.Evaluate(records, minutes);
            static double? N(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetDouble();
            void Same(double? want, double? got, string what) =>
                Assert.True(want is null ? got is null : got is { } g && Math.Abs(g - want.Value) < 1e-9, $"{minutes / 60}h {what}: expected {want}, got {got}");

            Assert.Equal(h.GetProperty("overlap").GetInt32(), r.Overlap);
            Assert.Equal(h.GetProperty("samples").GetInt32(), r.Samples);
            Same(N(h.GetProperty("hit90Pct")), r.Range90?.HitRatePct, "hit90");
            Same(N(h.GetProperty("margin90")), r.Range90?.Margin95, "margin90");
            Same(N(h.GetProperty("hit50Pct")), r.Range50?.HitRatePct, "hit50");
            Same(N(h.GetProperty("margin50")), r.Range50?.Margin95, "margin50");
            Same(N(h.GetProperty("meanWidth90Pct")), r.Range90?.MeanWidthPct, "width90");
            Same(N(h.GetProperty("meanWidth50Pct")), r.Range50?.MeanWidthPct, "width50");
            Same(N(h.GetProperty("intervalScorePct")), r.IntervalScorePct, "interval score");
            Same(N(h.GetProperty("intervalScore90Pct")), r.IntervalScore90Pct, "IS90");
            Same(N(h.GetProperty("intervalScore50Pct")), r.IntervalScore50Pct, "IS50");
            Assert.Equal(h.GetProperty("misses90Above").GetInt32(), r.Misses90Above);
            Assert.Equal(h.GetProperty("misses90Below").GetInt32(), r.Misses90Below);

            var daily = h.GetProperty("daily").EnumerateArray().ToList();
            Assert.Equal(daily.Count, r.Daily.Count);
            foreach (var (want, got) in daily.Zip(r.Daily))
            {
                Assert.Equal(want.GetProperty("day").GetString(), got.Day.ToString("yyyy-MM-dd"));
                Assert.Equal(want.GetProperty("samples").GetInt32(), got.Samples);
                Same(N(want.GetProperty("hit90Pct")), got.Hit90Pct, "daily hit90");
                Same(N(want.GetProperty("hit50Pct")), got.Hit50Pct, "daily hit50");
            }
            Assert.Equal(h.GetProperty("excluded").GetInt32(), records.Count(f => f.HorizonMinutes == minutes && f.Status == "excluded"));
            Assert.Equal(h.GetProperty("pending").GetInt32(), records.Count(f => f.HorizonMinutes == minutes && f.Status == "pending"));
        }
    }

    private static string SampleFile()
    {
        using var expected = JsonDocument.Parse(File.ReadAllText(Sample("expected-results.json")));
        return expected.RootElement.GetProperty("file").GetString()!;
    }
}
