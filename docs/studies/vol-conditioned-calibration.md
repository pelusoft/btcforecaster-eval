# Study: volatility-conditioned calibration (pre-registration)

> **Status: holdout not run yet.** This note fixes every choice before the June–October holdout is scored, so the result can't
> be steered by it. The holdout result will be added below without changing anything above it. Unlike the
> [outside data study](outside-data-rejected.md), this one is built on the v1 baseline and can be re-run from this repository.

Written 12 November 2026.

## Question

Should a forecast be calibrated differently on calm and volatile days?

A hard switch between a "calm" and a "wild" model was considered and not pursued: v1 and v2 already scale their ranges with
recent volatility (on v2's live record the average 1h 90% width is 0.67% in the calmest third of forecasts and 1.46% in the
wildest), and a switch adds thresholds to tune. What a scale can't fix is the **shape** of the ranges. Both models set their
edges from the empirical distribution of past standardised moves, pooled over 7–14 days. If that distribution differs between
calm and volatile spells, pooled edges are too wide in one and too narrow in the other.

## Why it was tested

v2's live record, 28 October – 12 November 2026 (12,558 scored forecasts), split into thirds by the 6-hour realised volatility
known at forecast time:

| Horizon | 90% held: calm / normal / wild | 50% held: calm / normal / wild |
|---|---|---|
| 1h | 90.7 / 92.2 / 89.7 (±5.4) | 55.0 / 53.8 / 46.6 (±9.0) |
| 4h | 90.6 / 89.8 / 87.3 (±10.8) | 61.8 / 58.3 / 45.7 (±18.0) |

The 90% range holds evenly. The 50% range leans the same way at both horizons (too wide when calm, too narrow when wild),
but within the margins. Two weeks, thirds cut on the same data, several cells looked at: a hint, not evidence.

## Candidate

v1 exactly as shipped, except that its calibration uses only the **third of past outcomes whose volatility at the time was
closest to the current volatility** (nearest on a log scale of the per-bar variance), never fewer than 288. It applies at
**1h and 4h only**; 24h is unchanged. Code: `VolatilityBand.NearestInVolatility`, settings
`VolConditioningFraction = 0.33` (the value the tuning runs used) and `VolConditioningHorizonsHours = [1, 4]`.

## Choices made on January–May only

Three fractions were run on the tuning period, split into January–March and April–May. Change in combined interval score
vs v1 (negative = better):

| Fraction | 1h Jan–Mar | 1h Apr–May | 4h Jan–Mar | 4h Apr–May | 24h Jan–Mar | 24h Apr–May |
|---|---|---|---|---|---|---|
| 0.25 | −3.14% (p < 0.001) | −3.45% (p < 0.001) | −1.26% (p 0.24) | −1.80% (p 0.02) | +6.25% (p 0.20) | −1.05% (p 0.60) |
| 0.33 | −3.02% (p < 0.001) | −3.39% (p < 0.001) | −1.61% (p 0.10) | −1.76% (p 0.01) | +3.50% (p 0.37) | −1.02% (p 0.56) |
| 0.50 | −2.81% (p < 0.001) | −3.05% (p < 0.001) | −1.77% (p 0.02) | −1.45% (p 0.005) | +1.62% (p 0.61) | −1.89% (p 0.19) |

The criteria for these choices were set before the runs:

- **Horizons: 1h and 4h.** "If 24h gets worse on January–May, limit the candidate to 1h and 4h." It was worse in
  January–March at every fraction, by more than the 1% the decision rule allows, and the 90% range fell outside its margin
  there at 0.25 and 0.33. A 14-day window holds only about 14 independent 24-hour outcomes; keeping a third leaves about 5.
- **Fraction: 1/3.** "If the fractions are within noise of each other, keep 1/3." At 1h and 4h they are.

## Holdout: what will be run, and what counts

```bash
dotnet run --project tools/BtcForecaster.Eval.Cli -- compare --from 2026-01-01 --to 2026-11-01 \
    --vol-conditioning 0.33 --vol-conditioning-horizons 1,4
```

Tuning 1 January – 31 May, holdout 1 June – 31 October 2026, forecasts every 15 minutes, scored once. The decision rule is
the standard one (`DecisionRule` defaults): at least 2% better with p < 0.05 on one horizon; no horizon worse by more than 1%;
coverage within margin95 everywhere; the gain better in at least 3 of the 5 months with no month supplying more than half.

**What a pass would mean:** only that the idea is worth porting to v2. The comparison is against v1, and part of a gain over
v1 may come from something v2's HAR model already handles. **What a fail means:** it joins the rejected ideas in the README.

## Holdout result

*Not run yet.*
