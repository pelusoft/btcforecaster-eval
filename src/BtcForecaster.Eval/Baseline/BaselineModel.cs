using BtcForecaster.Eval.Scoring;

namespace BtcForecaster.Eval.Baseline;

/// <summary>
/// Settings of the v1 baseline, as BtcForecaster ships them: 6-hour warm-up, a 15-minute volatility half-life at 1 hour scaled
/// with the horizon (60 minutes at 4 hours, capped at 6 hours at 24 hours), 7 days of calibration at 1 hour and 14 days for
/// longer horizons, and the time-of-day adjustment at 4 and 24 hours (it made no consistent difference at 1 hour).
/// <see cref="VolConditioningFraction"/> is a candidate, off in v1: see <see cref="VolatilityBand.NearestInVolatility"/>.
/// It applies at <see cref="VolConditioningHorizonsHours"/> only, or at every horizon when that is null.
/// </summary>
public sealed record BaselineSettings(
    int LookbackHours = 6, int HalfLifeMinutes = 15, int CalibrationDays = 7, int LongHorizonCalibrationDays = 14,
    int[]? SeasonalHorizonsHours = null, double VolConditioningFraction = 0, int[]? VolConditioningHorizonsHours = null)
{
    public static readonly BaselineSettings Default = new(SeasonalHorizonsHours: [4, 24]);

    public TimeSpan Lookback => TimeSpan.FromHours(LookbackHours);

    public TimeSpan HalfLifeFor(TimeSpan horizon)
    {
        if (HalfLifeMinutes <= 0) return TimeSpan.Zero;
        var scaled = TimeSpan.FromMinutes(HalfLifeMinutes) * Math.Max(1, horizon / TimeSpan.FromHours(1));
        return scaled < TimeSpan.FromHours(6) ? scaled : TimeSpan.FromHours(6);
    }

    public TimeSpan CalibrationWindowFor(TimeSpan horizon) =>
        CalibrationDays <= 0 ? TimeSpan.Zero
        : horizon > TimeSpan.FromHours(1) ? TimeSpan.FromDays(Math.Max(CalibrationDays, LongHorizonCalibrationDays))
        : TimeSpan.FromDays(CalibrationDays);

    public bool SeasonalFor(TimeSpan horizon) => (SeasonalHorizonsHours ?? []).Any(h => TimeSpan.FromHours(h) == horizon);

    public double VolConditioningFor(TimeSpan horizon) =>
        VolConditioningHorizonsHours is null || VolConditioningHorizonsHours.Any(h => TimeSpan.FromHours(h) == horizon)
            ? VolConditioningFraction
            : 0;

    public TimeSpan HistoryFor(TimeSpan horizon) => VolatilityBand.RequiredHistory(Lookback, horizon, CalibrationWindowFor(horizon));
}

/// <summary>
/// The v1 baseline (<c>v1-baseline</c>): the price at the horizon equals the price now, with calibrated volatility ranges around
/// it. It doesn't predict direction; BtcForecaster's direction models never beat this "no change" guess.
/// </summary>
public sealed class BaselineModel(BaselineSettings? settings = null)
{
    public const string Release = "v1-baseline";

    public BaselineSettings Settings { get; } = settings ?? BaselineSettings.Default;

    /// <summary>The forecast at <paramref name="createdAt"/>, from prices up to then only; null without enough history.</summary>
    /// <param name="history">Prices oldest first. Anything after <paramref name="createdAt"/> must not be passed (see <see cref="WalkForwardRunner"/>).</param>
    public (double Centre, PriceRange Range)? Forecast(IReadOnlyList<PriceSample> history, DateTime createdAt, TimeSpan horizon)
    {
        if (history.Count == 0 || history[^1].Timestamp > createdAt) return null;
        var centre = history[^1].Price;
        var distribution = VolatilityBand.Distribution(history, createdAt, horizon, Settings.Lookback,
            Settings.CalibrationWindowFor(horizon), Settings.HalfLifeFor(horizon), Settings.SeasonalFor(horizon),
            Settings.VolConditioningFor(horizon));
        return distribution is null ? null : (centre, distribution.RangeAround(centre));
    }
}

/// <summary>
/// Replays a model over historical bars, the way BtcForecaster's studies do: at each slot the model sees only prices at or
/// before the slot, and the outcome is the price at slot + horizon. Produces <see cref="ForecastRecord"/>s, so the results
/// are scored by exactly the same code as the live forecasts.
/// </summary>
public static class WalkForwardRunner
{
    public static List<ForecastRecord> Run(
        IReadOnlyList<PriceSample> bars, IEnumerable<DateTime> slots, TimeSpan horizon,
        Func<IReadOnlyList<PriceSample>, DateTime, TimeSpan, (double Centre, PriceRange Range)?> model, TimeSpan historyNeeded,
        string release = BaselineModel.Release)
    {
        var array = bars as PriceSample[] ?? bars.ToArray();
        var times = array.Select(b => b.Timestamp).ToArray();
        var result = new List<ForecastRecord>();
        foreach (var slot in slots)
        {
            var end = UpperBound(times, slot); // bars[..end] are at or before the slot
            var start = LowerBound(times, slot - historyNeeded - VolatilityBand.BarSize * 2);
            if (end - start < 2) continue;
            var history = new ArraySegment<PriceSample>(array, start, end - start);
            if (model(history, slot, horizon) is not { } f) continue;

            var target = slot + horizon;
            var outcome = OutcomeAt(array, times, target);
            result.Add(new ForecastRecord(slot, target, (int)horizon.TotalMinutes, release, f.Centre,
                f.Range.Lower50, f.Range.Upper50, f.Range.Lower90, f.Range.Upper90,
                outcome is null ? "pending" : "scored", outcome));
        }
        return result;
    }

    /// <summary>The bar exactly at the target time (5-minute closes are stamped on the grid), or null if it's missing.</summary>
    private static double? OutcomeAt(IReadOnlyList<PriceSample> bars, DateTime[] times, DateTime target)
    {
        var i = Array.BinarySearch(times, target);
        return i >= 0 ? bars[i].Price : null;
    }

    private static int UpperBound(DateTime[] times, DateTime t)
    {
        int lo = 0, hi = times.Length;
        while (lo < hi) { var mid = (lo + hi) / 2; if (times[mid] <= t) lo = mid + 1; else hi = mid; }
        return lo;
    }

    private static int LowerBound(DateTime[] times, DateTime t)
    {
        int lo = 0, hi = times.Length;
        while (lo < hi) { var mid = (lo + hi) / 2; if (times[mid] < t) lo = mid + 1; else hi = mid; }
        return lo;
    }
}
