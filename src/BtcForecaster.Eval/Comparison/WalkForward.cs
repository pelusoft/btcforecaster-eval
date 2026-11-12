namespace BtcForecaster.Eval.Comparison;

/// <summary>A half-open period [Start, End), UTC.</summary>
public sealed record Period(DateTime Start, DateTime End)
{
    public bool Contains(DateTime t) => t >= Start && t < End;
    public override string ToString() => $"{Start:yyyy-MM-dd} to {End:yyyy-MM-dd}";
}

/// <summary>Tune on one period, then test once on a later one the choices never saw.</summary>
public sealed record WalkForwardSplit(Period Tune, Period Test);

/// <summary>
/// The walk-forward discipline BtcForecaster's studies follow: every setting is chosen on the tuning period only; the holdout is
/// scored once, with a decision rule fixed beforehand. Looking at the holdout and then changing a setting turns it into tuning data.
/// </summary>
public static class WalkForward
{
    /// <summary>
    /// Splits an evaluation period in two at its midpoint moved to the nearest first of a month, so no month is split between
    /// tuning and holdout (the rule since 9 Nov 2026; e.g. 2025-12-31 to 2026-10-28 splits on 2026-06-01).
    /// </summary>
    public static WalkForwardSplit Halves(DateTime start, DateTime end, bool rawMidpoint = false)
    {
        var mid = Midpoint(start, end, rawMidpoint);
        return new WalkForwardSplit(new Period(start, mid), new Period(mid, end));
    }

    /// <summary>The split point: the raw midpoint (whole days), or that moved to the nearest first of a month.</summary>
    public static DateTime Midpoint(DateTime start, DateTime end, bool rawMidpoint = false)
    {
        var raw = start.AddDays((int)((end - start).TotalDays / 2));
        if (rawMidpoint) return raw;
        var first = new DateTime(raw.Year, raw.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return raw.Day > 15 ? first.AddMonths(1) : first;
    }

    /// <summary>
    /// Rolling-origin folds: tune on <paramref name="tuneLength"/>, test on the next <paramref name="testLength"/>, then move
    /// forward by the test length. Each test period comes strictly after its tuning period and test periods never overlap.
    /// </summary>
    public static IEnumerable<WalkForwardSplit> Rolling(DateTime start, DateTime end, TimeSpan tuneLength, TimeSpan testLength)
    {
        for (var tuneStart = start; tuneStart + tuneLength + testLength <= end; tuneStart += testLength)
        {
            var mid = tuneStart + tuneLength;
            yield return new WalkForwardSplit(new Period(tuneStart, mid), new Period(mid, mid + testLength));
        }
    }

    /// <summary>
    /// Forecast slots every <paramref name="stride"/> in a period whose outcome (slot + horizon) is also inside it, so no outcome
    /// from the next period leaks into this one.
    /// </summary>
    public static IEnumerable<DateTime> Slots(Period period, TimeSpan horizon, TimeSpan stride)
    {
        for (var t = period.Start; t + horizon < period.End; t += stride) yield return t;
    }
}
