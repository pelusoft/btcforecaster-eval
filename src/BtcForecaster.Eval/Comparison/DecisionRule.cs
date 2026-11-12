namespace BtcForecaster.Eval.Comparison;

/// <summary>A candidate against the current model at one horizon, on the holdout.</summary>
/// <param name="ChangePct">Combined interval score change vs the baseline, % (negative = better).</param>
/// <param name="PValue">Diebold-Mariano p-value of the per-forecast score differences.</param>
/// <param name="Calibrated90">The candidate's 90% hit rate is within its margin95 of 90.</param>
/// <param name="Calibrated50">Its 50% hit rate is within its margin95 of 50.</param>
/// <param name="MonthGains">Total score reduction per calendar month (positive = the candidate did better that month).</param>
public sealed record HorizonComparison(int HorizonMinutes, double ChangePct, double PValue, bool Calibrated90, bool Calibrated50, IReadOnlyList<double> MonthGains);

public sealed record Verdict(bool Passes, bool Rule1, bool Rule2, bool Rule3, bool Rule4, IReadOnlyList<int> WinningHorizons);

/// <summary>
/// The pre-registered rule BtcForecaster's studies use to accept a new model, fixed before the holdout is run:
/// <list type="number">
/// <item>the combined interval score is at least <c>MinGainPct</c> (2%) better with p &lt; 0.05 on at least one horizon;</item>
/// <item>no other horizon is worse by more than <c>MaxLossPct</c> (1%);</item>
/// <item>90% and 50% coverage stay within margin95 on every horizon;</item>
/// <item>on each winning horizon the gain is broad: better in at least 3 months, and no single month supplies more than half of it.</item>
/// </list>
/// </summary>
public sealed record DecisionRule(double MinGainPct = 2, double Alpha = 0.05, double MaxLossPct = 1, int MinMonthsBetter = 3, double MaxMonthShare = 0.5)
{
    public Verdict Evaluate(IReadOnlyList<HorizonComparison> horizons)
    {
        var wins = horizons.Where(h => h.ChangePct <= -MinGainPct && h.PValue < Alpha).ToList();
        var rule1 = wins.Count > 0;
        var rule2 = horizons.Where(h => !wins.Contains(h)).All(h => h.ChangePct <= MaxLossPct);
        var rule3 = horizons.All(h => h.Calibrated90 && h.Calibrated50);
        var rule4 = rule1 && wins.All(w => Broad(w.MonthGains));
        return new Verdict(rule1 && rule2 && rule3 && rule4, rule1, rule2, rule3, rule4, wins.Select(w => w.HorizonMinutes).ToList());
    }

    private bool Broad(IReadOnlyList<double> gains) =>
        gains.Count(g => g > 0) >= MinMonthsBetter && gains.Sum() > 0 && gains.Max() <= MaxMonthShare * gains.Sum();
}
