# Study: does outside data improve the forecast? (Rejected)

> **Why this is here.** This is the write-up of a real BtcForecaster study, included as an example of the method in this repository: a decision rule fixed in advance, every choice made on a tuning period, one run on a holdout period, and an honest "no". It was run with BtcForecaster's current model (v2, HAR-RV-J), whose code isn't in this repository, so it can't be re-run from here; the scoring, the Diebold-Mariano test, the split and the decision rule it used are all in `src/BtcForecaster.Eval`. Lightly edited: internal file paths and code names removed.

Study run 2 November 2026. **Result: no candidate passed the decision rule, so no model was added.** v2 stays as it is.

The data window was pinned, so the split was exactly the one v2 had been chosen on: tuning 31 December 2025 – 30 May 2026, holdout 30 May – 27 October 2026. (Later studies move the split to the first of a month, here 1 June; see `WalkForward.Halves`.)

## Data

| Source | Endpoint | Coverage (23 Sep 2025 → 28 Oct 2026) | Problems |
|---|---|---|---|
| DVOL (BTC implied volatility) | Deribit `public/get_volatility_index_data` | 1-minute for the last ~6 months, hourly closes before that (carried forward, stamped at the hour's end) | none; 3 forecast slots in Jun–Oct had stale DVOL (> 30 min) and fell back to v2 |
| Funding rate | Binance `GET /fapi/v1/fundingRate`, BTCUSDT | 1,200 settlements, every 8 h, no gaps | none. Max 1,000 rows per request, ascending; shares a 500 requests / 5 min / IP limit |
| Open interest | Binance public data archive `data.binance.vision/data/futures/um/daily/metrics/BTCUSDT/` (`sum_open_interest`, BTC) | 115,192 snapshots, every 5 min | 0.7 h missing in total (15 Oct 2026, 13:40–14:50 UTC) |

- **Open interest:** Binance's `openInterestHist` API rejects anything older than about 30 days, but Binance's own daily archive has the full history at 5-minute resolution, so open interest was tested on the full period at normal confidence, not as a low-confidence side test.
- **No look-ahead:** funding is stamped at its settlement time. Each open-interest snapshot counts as public 5 minutes after its creation time. DVOL is stamped at the close of its candle. Lookups return the latest value at or before the forecast time, never later. A test replaced every value published after the forecast time with absurd numbers and checked that the features at that time didn't change.
- **Staleness:** funding older than 8.5 h, open interest older than 30 min (including the 1h/4h/24h-ago snapshots) and DVOL older than 30 min count as missing. The candidate then makes no forecast for that slot and the study uses v2's forecast there, as a live shadow model would.

## Candidates

All are v2's exact pipeline (90-day daily refit, time-of-day terms, scheduled-release effects, 7/14-day empirical calibration) plus extra regressors.

- **A1:** log σ_h, with σ_h = (DVOL / 100) × √(h / 8760), the implied move over the horizon.
- **A2:** A1 plus log(IV / RV), with RV the realised variance of the last 30 days.
- **B:** |funding| and its change since the previous settlement (basis points), and the open-interest log change over 1h, 4h and 24h.
- **C1 / C2:** A1 + B and A2 + B.
- **D (lopsided edges): not run.** It was optional and only meant to check whether funding or open interest explain the slight downside lean in misses. B left the below/above miss split essentially where v2 has it (Jun–Oct 1h: 743/745 vs 745/741; 24h: 860/782 vs 845/795), so there was nothing to explain.

Worth knowing: in a log-linear regression, A1's term is an affine transform of v2's existing optional "log DVOL variance" term, and log(IV/RV) splits into log IV minus log RV. So A1 is effectively the "DVOL as a regressor" variant already reported, and A2 adds only a 30-day realised-volatility term.

**Selection on Jan–May only:** A1 and A2 were equivalent within noise (1h −0.25% vs −0.33%, 4h −0.12% vs −0.06%, 24h +0.51% vs +0.31%), so A1 went forward as the simpler one, and C followed it (C1 = A1 + B). A2 and C2 were not run on Jun–Oct.

## Results: combined interval score, change vs v2 (negative = better)

| Candidate | Jan–May 1h | 4h | 24h | **Jun–Oct 1h** | **4h** | **24h** |
|---|---|---|---|---|---|---|
| v2 score | 3.146 | 6.443 | 16.560 | 2.562 | 5.216 | 14.696 |
| A1: + DVOL | −0.25% | −0.12% | +0.51% | **−0.22%** (p 0.008) | **−0.31%** (p 0.21) | **+0.93%** (p 0.38) |
| A2: + DVOL + IV/RV | −0.33% | −0.06% | +0.31% | – | – | – |
| B: + funding/OI | +0.11% | +0.05% | +0.05% | **+0.02%** (p 0.74) | **+0.12%** (p 0.19) | **−0.01%** (p 0.95) |
| C1: A1 + B | −0.20% | −0.08% | +0.65% | **−0.21%** (p 0.08) | **−0.30%** (p 0.29) | **+0.61%** (p 0.57) |
| C2: A2 + B | −0.20% | +0.16% | +0.68% | – | – | – |

p = Diebold-Mariano test on the per-forecast score differences, Newey-West variance with lag = horizon in 5-minute steps (12 / 48 / 288). This is conservative with forecasts every 15 minutes.

Coverage stayed calibrated for every candidate and horizon. Jun–Oct 90% hit rates were 89.4–89.8% at 1h and 4h and 88.6–89.0% at 24h, all within margin95. Widths moved by at most 3%.

## Decision

The rule was fixed before the Jun–Oct run: at least 2% better with p < 0.05 on one horizon; no other horizon worse by more than 1%; coverage within margin95 everywhere; the gain spread across months.

| Candidate | Rule 1 (≥ 2%, p < 0.05) | Rule 2 | Rule 3 | Verdict |
|---|---|---|---|---|
| A1 | ✘ best −0.31% | ✔ | ✔ | fails |
| B | ✘ best −0.01% | ✔ | ✔ | fails |
| C1 | ✘ best −0.30% | ✔ | ✔ | fails |

**No model added.** The live period (from 28 October) wasn't replayed: with every candidate an order of magnitude short of the threshold on five months of holdout, four days couldn't change the decision. (`ComparisonTests.The_outside_data_study_fails_the_decision_rule` runs A1's holdout numbers through `DecisionRule`.)

## In plain English

Outside data didn't help in any way that matters.

- **Implied volatility (DVOL):** a small but statistically real improvement at 1 hour, about 0.2%, which is a tenth of the bar set. It's slightly worse at 24 hours.
- **Funding rates and open interest:** no measurable effect at any horizon (within ±0.12%, nowhere near significant) once recent price volatility is known.

Recent realised volatility, which v2 already measures over 15 minutes, an hour, a day and a week, carries almost everything these sources know about the next few hours. Not adding them also keeps the forecast independent of Deribit and Binance futures data.
