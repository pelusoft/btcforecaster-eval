using BtcForecaster.Eval.Comparison;

namespace BtcForecaster.Eval.Tests;

public class ComparisonTests
{
    [Theory]
    [InlineData(0, 0.5)]
    [InlineData(1.959963985, 0.975)]
    [InlineData(-1.959963985, 0.025)]
    [InlineData(1, 0.841344746)]
    public void Normal_cdf_is_accurate(double x, double expected) => Assert.Equal(expected, DieboldMariano.NormalCdf(x), 6);

    [Fact]
    public void Newey_west_with_no_lags_is_the_plain_variance()
    {
        var d = new[] { 1.0, -2, 3, 0.5, -1.5, 2 };
        var mean = d.Average();
        Assert.Equal(d.Sum(x => (x - mean) * (x - mean)) / d.Length, DieboldMariano.NeweyWestVariance(d, 0), 12);
    }

    [Fact]
    public void Newey_west_grows_with_positive_autocorrelation()
    {
        // Overlapping forecasts: each loss difference repeats for 12 steps, as with 1-hour forecasts every 5 minutes.
        var rng = new Random(7);
        var shocks = Enumerable.Range(0, 200).Select(_ => rng.NextDouble() - 0.5).ToArray();
        var d = Enumerable.Range(0, 2400).Select(i => shocks[i / 12]).ToArray();
        Assert.True(DieboldMariano.NeweyWestVariance(d, 12) > 5 * DieboldMariano.NeweyWestVariance(d, 0));
    }

    [Fact]
    public void A_real_improvement_is_significant_and_noise_is_not()
    {
        var rng = new Random(42);
        double Noise() => rng.NextDouble() - 0.5;
        var better = Enumerable.Range(0, 2000).Select(_ => -0.05 + Noise()).ToArray();   // candidate's loss lower by 0.05 on average
        var same = Enumerable.Range(0, 2000).Select(_ => Noise()).ToArray();

        var b = DieboldMariano.Test(better, lag: 12)!;
        Assert.True(b.MeanDifference < 0);
        Assert.True(b.PValue < 0.01, $"p = {b.PValue}");
        Assert.True(DieboldMariano.PValue(same, lag: 12) > 0.05);
    }

    [Fact]
    public void Overlap_makes_the_same_mean_difference_less_convincing()
    {
        // The same 200 independent differences, each repeated 12 times (overlapping forecasts) vs once.
        var rng = new Random(3);
        var independent = Enumerable.Range(0, 200).Select(_ => 0.03 + rng.NextDouble() - 0.5).ToArray();
        var repeated = independent.SelectMany(x => Enumerable.Repeat(x, 12)).ToArray();
        var naive = DieboldMariano.PValue(repeated, lag: 0);      // pretends 2,400 independent forecasts
        var hac = DieboldMariano.PValue(repeated, lag: 12);       // allows for the overlap
        Assert.True(hac > naive * 3, $"naive p {naive}, HAC p {hac}");
    }

    [Fact]
    public void Degenerate_inputs_give_no_result()
    {
        Assert.Null(DieboldMariano.Test([1.0], 12));
        Assert.Null(DieboldMariano.Test([0.5, 0.5, 0.5], 12));
        Assert.True(double.IsNaN(DieboldMariano.PValue([], 12)));
    }

    [Fact]
    public void Halves_split_on_the_first_of_a_month_like_the_studies()
    {
        var start = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        var split = WalkForward.Halves(start, end);
        Assert.Equal(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), split.Tune.End);
        Assert.Equal(split.Tune.End, split.Test.Start);
        Assert.Equal(new DateTime(2026, 5, 30, 0, 0, 0, DateTimeKind.Utc), WalkForward.Midpoint(start, end, rawMidpoint: true));
    }

    [Fact]
    public void Rolling_folds_test_after_tuning_and_never_overlap()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var folds = WalkForward.Rolling(start, start.AddDays(100), TimeSpan.FromDays(30), TimeSpan.FromDays(10)).ToList();
        Assert.Equal(7, folds.Count);
        Assert.All(folds, f => Assert.Equal(f.Tune.End, f.Test.Start));
        Assert.All(folds.Zip(folds.Skip(1)), p => Assert.Equal(p.First.Test.End, p.Second.Test.Start));
    }

    [Fact]
    public void Slots_keep_every_outcome_inside_the_period()
    {
        var period = new Period(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc));
        var slots = WalkForward.Slots(period, TimeSpan.FromHours(4), TimeSpan.FromMinutes(15)).ToList();
        Assert.All(slots, s => Assert.True(period.Contains(s + TimeSpan.FromHours(4))));
        Assert.Equal(new DateTime(2026, 6, 1, 19, 45, 0, DateTimeKind.Utc), slots[^1]);
    }

    [Fact]
    public void The_outside_data_study_fails_the_decision_rule()
    {
        // A1 (+DVOL) on the Jun-Oct holdout: -0.22% (p 0.008), -0.31% (p 0.21), +0.93% (p 0.38). Calibrated everywhere.
        var a1 = new[]
        {
            new HorizonComparison(60, -0.22, 0.008, true, true, [1, 1, 1, 1, 1]),
            new HorizonComparison(240, -0.31, 0.21, true, true, [1, -1, 1, 1, -1]),
            new HorizonComparison(1440, 0.93, 0.38, true, true, [-1, 1, -1, 1, -1]),
        };
        var v = new DecisionRule().Evaluate(a1);
        Assert.False(v.Passes);
        Assert.False(v.Rule1);                 // nowhere near 2% better
        Assert.True(v.Rule2);
        Assert.True(v.Rule3);
    }

    [Fact]
    public void A_broad_significant_gain_passes_and_a_one_month_gain_does_not()
    {
        HorizonComparison H(int m, double change, double p, double[] months) => new(m, change, p, true, true, months);
        var rule = new DecisionRule();

        Assert.True(rule.Evaluate([H(60, -3, 0.01, [2, 3, 2, 1, 2]), H(240, 0.5, 0.6, [0]), H(1440, -0.2, 0.7, [0])]).Passes);
        // Same headline gain, but one month supplies most of it.
        Assert.False(rule.Evaluate([H(60, -3, 0.01, [9, 0.5, 0.5, -0.5, 0.5]), H(240, 0.5, 0.6, [0]), H(1440, -0.2, 0.7, [0])]).Rule4);
        // A win at one horizon paid for with a loss of more than 1% at another.
        Assert.False(rule.Evaluate([H(60, -3, 0.01, [2, 3, 2, 1, 2]), H(240, 1.5, 0.2, [0]), H(1440, 0, 0.9, [0])]).Rule2);
        // Better but miscalibrated.
        Assert.False(rule.Evaluate([new HorizonComparison(60, -3, 0.01, false, true, [2, 3, 2])]).Rule3);
    }
}
