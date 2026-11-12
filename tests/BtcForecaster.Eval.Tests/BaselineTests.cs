using BtcForecaster.Eval.Baseline;
using BtcForecaster.Eval.Comparison;
using BtcForecaster.Eval.Scoring;

namespace BtcForecaster.Eval.Tests;

/// <summary>The v1 baseline on synthetic prices (no downloads): shape of the ranges, no look-ahead, and calibration.</summary>
public class BaselineTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A random walk of 5-minute bars with fat-ish tails (Student-like mixture), seeded so the tests are repeatable.</summary>
    private static PriceSample[] Walk(int days, int seed = 1, double sigma = 0.0015)
    {
        var rng = new Random(seed);
        var bars = new PriceSample[days * 288];
        var price = 80_000.0;
        for (var i = 0; i < bars.Length; i++)
        {
            var z = Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
            var scale = rng.NextDouble() < 0.05 ? 3 : 1;
            price *= Math.Exp(sigma * scale * z / Math.Sqrt(1.4));
            bars[i] = new PriceSample(Start.AddMinutes(5 * (i + 1)), price);
        }
        return bars;
    }

    [Fact]
    public void Ranges_are_centred_on_the_price_with_the_90_range_outside_the_50_range()
    {
        var bars = Walk(20);
        var at = bars[^1].Timestamp;
        var model = new BaselineModel();
        foreach (var hours in new[] { 1, 4, 24 })
        {
            var f = model.Forecast(bars, at, TimeSpan.FromHours(hours))!.Value;
            Assert.Equal(bars[^1].Price, f.Centre);
            Assert.True(f.Range.Lower90 < f.Range.Lower50 && f.Range.Lower50 < f.Centre && f.Centre < f.Range.Upper50 && f.Range.Upper50 < f.Range.Upper90);
            // Symmetric on a log scale: no direction call.
            Assert.Equal(Math.Log(f.Centre / f.Range.Lower90), Math.Log(f.Range.Upper90 / f.Centre), 9);
        }
        // Longer horizons, wider ranges.
        double Width(int h) { var r = model.Forecast(bars, at, TimeSpan.FromHours(h))!.Value.Range; return r.Upper90 / r.Lower90; }
        Assert.True(Width(1) < Width(4) && Width(4) < Width(24));
    }

    [Fact]
    public void Prices_after_the_forecast_time_cannot_change_it()
    {
        var bars = Walk(20);
        var slot = bars[^300].Timestamp;
        var model = new BaselineModel();
        var horizon = TimeSpan.FromHours(4);
        var history = model.Settings.HistoryFor(horizon);

        var original = WalkForwardRunner.Run(bars, [slot], horizon, model.Forecast, history).Single();
        // Replace every price after the slot with absurd values: the forecast must not move (only its outcome can).
        var tampered = bars.Select(b => b.Timestamp > slot ? b with { Price = b.Price * 10 } : b).ToArray();
        var again = WalkForwardRunner.Run(tampered, [slot], horizon, model.Forecast, history).Single();

        Assert.Equal(original.ReferencePrice, again.ReferencePrice);
        Assert.Equal(original.Low90, again.Low90);
        Assert.Equal(original.High50, again.High50);
        Assert.NotEqual(original.ActualPrice, again.ActualPrice);
        // And the model refuses history that runs past the forecast time.
        Assert.Null(model.Forecast(bars, slot, horizon));
    }

    [Fact]
    public void Without_a_day_of_outcomes_it_falls_back_to_the_bell_curve()
    {
        var bars = Walk(1);
        var d = VolatilityBand.Distribution(bars, bars[^1].Timestamp, TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromDays(7), TimeSpan.FromMinutes(15));
        Assert.NotNull(d);
        Assert.False(d.IsCalibrated);
        Assert.Equal(1.6448536, d.AbsQuantile(0.90), 5);

        var full = VolatilityBand.Distribution(Walk(10), Walk(10)[^1].Timestamp, TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromDays(7), TimeSpan.FromMinutes(15));
        Assert.True(full!.IsCalibrated);
    }

    [Fact]
    public void Half_life_scales_with_the_horizon_and_calibration_is_longer_beyond_an_hour()
    {
        var s = BaselineSettings.Default;
        Assert.Equal(TimeSpan.FromMinutes(15), s.HalfLifeFor(TimeSpan.FromHours(1)));
        Assert.Equal(TimeSpan.FromMinutes(60), s.HalfLifeFor(TimeSpan.FromHours(4)));
        Assert.Equal(TimeSpan.FromHours(6), s.HalfLifeFor(TimeSpan.FromHours(24)));
        Assert.Equal(TimeSpan.FromDays(7), s.CalibrationWindowFor(TimeSpan.FromHours(1)));
        Assert.Equal(TimeSpan.FromDays(14), s.CalibrationWindowFor(TimeSpan.FromHours(24)));
        Assert.False(s.SeasonalFor(TimeSpan.FromHours(1)));
        Assert.True(s.SeasonalFor(TimeSpan.FromHours(4)));
    }

    [Fact]
    public void Vol_conditioning_keeps_the_outcomes_nearest_in_volatility()
    {
        // Ratio i came from a moment with variance e^(i/100): the nearest third to e^4.5 are i = 300..599.
        var past = Enumerable.Range(0, 900).Select(i => ((double)i, Math.Exp(i / 100.0))).ToList();
        var kept = VolatilityBand.NearestInVolatility(past, Math.Exp(4.495), 1 / 3.0);
        Assert.Equal(Enumerable.Range(300, 300).Select(i => (double)i), kept);

        // Never fewer than the minimum, and fraction 1 keeps everything.
        Assert.Equal(VolatilityBand.MinCalibrationSamples, VolatilityBand.NearestInVolatility(past, 1, 0.01).Length);
        Assert.Equal(900, VolatilityBand.NearestInVolatility(past, 1, 1).Length);
    }

    [Fact]
    public void Vol_conditioning_off_or_at_fraction_one_is_exactly_v1()
    {
        var bars = Walk(20);
        var at = bars[^1].Timestamp;
        var v1 = new BaselineModel().Forecast(bars, at, TimeSpan.FromHours(4))!.Value;
        var all = new BaselineModel(BaselineSettings.Default with { VolConditioningFraction = 1 }).Forecast(bars, at, TimeSpan.FromHours(4))!.Value;
        var third = new BaselineModel(BaselineSettings.Default with { VolConditioningFraction = 1 / 3.0 }).Forecast(bars, at, TimeSpan.FromHours(4))!.Value;

        Assert.Equal(v1.Range, all.Range);
        Assert.NotEqual(v1.Range, third.Range);
        Assert.Equal(v1.Centre, third.Centre);
    }

    [Fact]
    public void Vol_conditioning_can_be_limited_to_some_horizons()
    {
        var bars = Walk(20);
        var at = bars[^1].Timestamp;
        var v1 = new BaselineModel();
        var candidate = new BaselineModel(BaselineSettings.Default with { VolConditioningFraction = 1 / 3.0, VolConditioningHorizonsHours = [1, 4] });

        Assert.Equal(1 / 3.0, candidate.Settings.VolConditioningFor(TimeSpan.FromHours(1)));
        Assert.Equal(0, candidate.Settings.VolConditioningFor(TimeSpan.FromHours(24)));
        Assert.NotEqual(v1.Forecast(bars, at, TimeSpan.FromHours(1))!.Value.Range, candidate.Forecast(bars, at, TimeSpan.FromHours(1))!.Value.Range);
        Assert.Equal(v1.Forecast(bars, at, TimeSpan.FromHours(24))!.Value.Range, candidate.Forecast(bars, at, TimeSpan.FromHours(24))!.Value.Range);
    }

    [Fact]
    public void Vol_conditioned_forecasts_dont_see_later_prices()
    {
        var bars = Walk(20);
        var slot = bars[^300].Timestamp;
        var model = new BaselineModel(BaselineSettings.Default with { VolConditioningFraction = 1 / 3.0 });
        var horizon = TimeSpan.FromHours(1);
        var history = model.Settings.HistoryFor(horizon);

        var original = WalkForwardRunner.Run(bars, [slot], horizon, model.Forecast, history).Single();
        var tampered = bars.Select(b => b.Timestamp > slot ? b with { Price = b.Price * 10 } : b).ToArray();
        var again = WalkForwardRunner.Run(tampered, [slot], horizon, model.Forecast, history).Single();

        Assert.Equal(original.Low90, again.Low90);
        Assert.Equal(original.High50, again.High50);
    }

    [Fact]
    public void Vol_conditioned_ranges_stay_calibrated_on_a_random_walk()
    {
        var bars = Walk(45, seed: 11);
        var model = new BaselineModel(BaselineSettings.Default with { VolConditioningFraction = 1 / 3.0 });
        var horizon = TimeSpan.FromHours(1);
        var period = new Period(Start.AddDays(10), Start.AddDays(45));
        var records = WalkForwardRunner.Run(bars, WalkForward.Slots(period, horizon, TimeSpan.FromMinutes(15)), horizon, model.Forecast, model.Settings.HistoryFor(horizon));

        var report = Evaluation.Evaluate(records, 60, intervalMinutes: 15);

        Assert.True(report.Range90!.IsCalibrated, $"90%: {report.Range90.HitRatePct:F1} ± {report.Range90.Margin95:F1}");
        Assert.True(report.Range50!.IsCalibrated, $"50%: {report.Range50.HitRatePct:F1} ± {report.Range50.Margin95:F1}");
    }

    [Fact]
    public void On_a_random_walk_the_calibrated_ranges_hold_about_as_often_as_they_say()
    {
        var bars = Walk(45, seed: 11);
        var model = new BaselineModel();
        var horizon = TimeSpan.FromHours(1);
        var period = new Period(Start.AddDays(10), Start.AddDays(45));
        var records = WalkForwardRunner.Run(bars, WalkForward.Slots(period, horizon, TimeSpan.FromMinutes(15)), horizon, model.Forecast, model.Settings.HistoryFor(horizon));

        var report = Evaluation.Evaluate(records, 60, intervalMinutes: 15);

        Assert.True(report.Samples > 3000);
        Assert.True(report.Range90!.IsCalibrated, $"90%: {report.Range90.HitRatePct:F1} ± {report.Range90.Margin95:F1}");
        Assert.True(report.Range50!.IsCalibrated, $"50%: {report.Range50.HitRatePct:F1} ± {report.Range50.Margin95:F1}");
    }
}
