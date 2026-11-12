# btcforecaster-eval

How [BtcForecaster](https://btcforecaster.com) scores its Bitcoin forecasts, as code you can run.

BtcForecaster is a free website that shows how far the Bitcoin price is likely to move in the next 1, 4 and 24 hours, as a 50% range and a 90% range, refreshed every 5 minutes. It doesn't try to say whether the price will go up or down. Every forecast is scored in public against what the price actually did, misses included.

**This repository exists so anyone can check exactly how the forecasts are scored.** It holds the scoring code the site uses, the statistical tools used to decide whether a new model is really better, and the simple baseline model as a worked example. A test checks that this code reproduces the site's own numbers on a sample of its forecasts.

| Folder | What's in it |
|---|---|
| `src/BtcForecaster.Eval/Scoring` | Hit rate (coverage) per range and horizon, effective sample size and `margin95`, interval score (combined, 90%, 50%), misses above and below, daily and rolling 7-day breakdowns, misses by volatility bucket and side |
| `src/BtcForecaster.Eval/Comparison` | Diebold-Mariano test with Newey-West (HAC) errors, walk-forward splits (tune on one period, test once on a later one), and the pre-registered decision rule |
| `src/BtcForecaster.Eval/Baseline` | The v1 baseline model (`v1-baseline`): calibrated volatility ranges around the current price |
| `src/BtcForecaster.Eval/Data` | A downloader for public Binance BTC/USDT 5-minute bars, cached locally |
| `tools/BtcForecaster.Eval.Cli` | `btcf-eval`: score a forecasts file, download bars, replay the baseline, compare two models |
| `data/sample` | Real BtcForecaster forecasts with their outcomes, and the site's own scores for them ([read the notes](data/sample/README.md)) |
| `docs/studies` | A full study of an idea that was tested and **rejected**, as an example of the method |

## Quick start

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download) (or later).

```bash
git clone https://github.com/pelusoft/btcforecaster-eval.git
cd btcforecaster-eval
dotnet test

# Score the sample forecasts, offline:
dotnet run --project tools/BtcForecaster.Eval.Cli -- score data/sample/forecasts-v2-har-20261103-20261110.csv
```

With an internet connection you can also replay the baseline on real prices and run a model comparison:

```bash
# Downloads public 5-minute bars from Binance into data/cache (not committed), then scores the v1 baseline:
dotnet run --project tools/BtcForecaster.Eval.Cli -- baseline --from 2026-10-01 --to 2026-10-15

# Tune on the first half, test once on the second: v1 with a 60-minute volatility half-life against v1 as shipped (15 minutes):
dotnet run --project tools/BtcForecaster.Eval.Cli -- compare --from 2026-07-01 --to 2026-10-01 --half-life 60
```

To score BtcForecaster's latest live forecasts yourself, export them from the public API with `scripts/export-sample.ps1` (PowerShell 7, any OS) and run `score` on the file.

## How the scoring works

Every forecast has a 50% range and a 90% range around the price when it was made. Once its target time has passed, it is scored against the price at that moment (the nearest stored price within ±3 minutes, from the same price feed as the forecast; forecasts whose outcome came from a different feed are excluded, never scored).

### Coverage (hit rate)

The share of outcomes that landed inside the range, edges included. A good 90% range holds about 90% of the time: much less means it's too narrow (overconfident), much more means it's too wide (not useful).

> Of 2,016 one-hour forecasts, 1,810 landed inside their 90% range: coverage 89.8%.

### Effective sample size and margin

Is 89.8% close enough to 90%? That depends on how much evidence there is, and forecasts made every 5 minutes overlap: twelve consecutive 1-hour forecasts share most of the same hour, so they aren't independent. Counting them as independent would make the evidence look twelve times stronger than it is. So the margin uses an **effective sample size** of n ÷ overlap, where overlap = horizon ÷ 5 minutes (12 at 1 hour, 48 at 4 hours, 288 at 24 hours):

```
n_eff    = n ÷ overlap
margin95 = 1.96 × √(p × (1 − p) ÷ n_eff) × 100      (p = 0.90 for the 90% range)
```

A range is **calibrated** when its coverage is within `margin95` of its target.

> 2,016 one-hour forecasts (a week) → n_eff = 168 → margin95 = ±4.5 points, so 89.8% is calibrated. The same week of 24-hour forecasts has n_eff = 7 → ±22 points: a week says very little about a 24-hour forecast.

### Interval score

Coverage alone can be gamed: absurdly wide ranges always hold. The **interval score** (Gneiting & Raftery, 2007) rewards narrow ranges but punishes misses in proportion to how far outside the price landed:

```
interval score = (upper − lower) + (2 ÷ α) × (lower − actual, if below) + (2 ÷ α) × (actual − upper, if above)
α = 1 − level   (0.10 for the 90% range, 0.50 for the 50% range)
```

BtcForecaster reports it as a percentage of the price, averaged over forecasts, for each range and for the two added together ("combined"). Lower is better.

> A 90% range from 99 to 101 (width 2): if the price ends at 100, the score is 2. If it ends at 102, one point above, the score is 2 + 20 × 1 = 22.

### Misses by side and volatility

A 90% range should leave about 5% of outcomes below it and 5% above, in quiet markets and busy ones alike. Live-stats'
`byVolatilityBucket` block (and `btcf-eval score --by-volatility`) splits the misses by side and by conditions when the
forecast was made: calm, normal or active, from the last 24 hours' realised variance ÷ its median over the trailing 30 days,
at thresholds fixed on January–May 2026. For each it gives the misses, the share held on that side (target 95%, with its
margin from the effective sample size) and the average miss size as a % of the price. An example is in
[the sample notes](data/sample/README.md#misses-by-side-and-volatility). It is reporting only: no forecast or score changes.

### Is a new model really better?

A new model must beat the current one on the **interval score** on a period it was never tuned on, by enough that it isn't luck:

- **Walk-forward:** every setting is chosen on a tuning period (e.g. January–May); the holdout (June–October) is scored **once**. The split falls on the first of a month, so no month is in both.
- **Diebold-Mariano test:** on the per-forecast score differences, with a Newey-West (HAC) variance whose lag is the horizon in 5-minute steps, because overlapping forecasts make the differences strongly correlated.
- **Decision rule, fixed before the holdout is run:** at least 2% better with p < 0.05 on one horizon; no other horizon worse by more than 1%; coverage within margin95 everywhere; and the gain spread across months (better in at least 3, no month supplying more than half of it).

## Ideas tested and rejected

Each was tested the same way and left out. The full study of one of them is in [`docs/studies/outside-data-rejected.md`](docs/studies/outside-data-rejected.md).

- **Predicting direction** (three models: SSA on log returns, gradient-boosted trees on price and volume features, and the same trees on order flow, funding and open interest): none beat assuming the price stays the same on unseen data. Direction accuracy was 47–49%, which is chance. Bitcoin is close to a random walk over hours, which is why BtcForecaster forecasts ranges, not direction.
- **Implied volatility as an input** (Deribit's DVOL, what options traders expect): a tiny gain at 1 hour, slightly worse at 24 hours. Not worth depending on another data source.
- **Recency-weighted calibration** (counting the last hours or days more when setting the edges): mixed results, none better than the current model, and one version let the 24-hour hit rate fall to 83%.
- **Shorter training (30 or 60 days):** similar at 1 and 4 hours, worse at 24 hours.
- **Live feedback correction** (widening after each miss and narrowing after each hit): it forced the hit rate to 90%, but by over-widening after unlucky streaks, and scored worse at every horizon.
- **Outside data** (implied volatility scaled to the horizon, futures funding rates, open interest): nothing came close to the 2% improvement fixed in advance. Recent price volatility already carries what these know about the next few hours.
- **Extra room for the 48 hours after a scheduled US release:** the Fed-only version looked good on January–May but was worse on unseen data (based on three Fed decisions); the version pooling all releases made almost no difference.
- **Lopsided ranges** (more room on one side, because recent misses fell below the range more often): mostly worse. One mild version helped at 24 hours but wasn't statistically convincing; it may be re-tested after more months of data.

## What's not here

- **The live site, its infrastructure and its data.** This repository is only the scoring and study tooling.
- **The current model (v2, HAR-RV-J).** The v1 baseline is included as a worked example; the studies of v2 are described in the reports but its code isn't published here.
- **Raw market data.** `btcf-eval download` fetches public Binance bars into a local cache that git ignores. Please respect [Binance's terms of use](https://www.binance.com/en/terms) and API limits: the downloader asks for 1,000 bars per request, waits between requests and backs off when asked to.

## Live data and API

- Live forecasts and track record: [btcforecaster.com](https://btcforecaster.com) and the [track record page](https://btcforecaster.com/track-record)
- Free public API, no key needed: [btcforecaster.com/developers](https://btcforecaster.com/developers) (base URL `https://btcforecaster.com/api/v1`)
- How the forecasts are made: [btcforecaster.com/how-it-works](https://btcforecaster.com/how-it-works)

## Not financial advice

Everything here, and on BtcForecaster, is for information and education only. It is not financial, investment or trading advice, and not a recommendation to buy or sell anything. Forecast ranges are statistical estimates that will sometimes be wrong: a 90% range is expected to miss about 1 time in 10, and sudden moves can land far outside it. Cryptocurrency prices are highly volatile and you can lose all the money you put in.

## Contributing and licence

Issues and pull requests are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md) and the [code of conduct](CODE_OF_CONDUCT.md). Released under the [MIT licence](LICENSE).
