# Sample data

## `forecasts-v2-har-20261103-20261110.csv`

One full week of BtcForecaster's live forecasts from the current model (`v2-har`): every forecast made from 3 November 2026 00:00 to 10 November 2026 00:00 UTC, every 5 minutes, at all three horizons. That's 6,048 forecasts (2,016 per horizon), all with known outcomes. It was exported from the public API (`https://btcforecaster.com/api/v1/forecasts`) with `scripts/export-sample.ps1`, and uses the same columns:

| Column | Meaning |
|---|---|
| `forecastMadeUtc`, `targetUtc` | When the forecast was made, and the moment its ranges refer to (UTC) |
| `horizon`, `horizonMinutes` | `1h`/60, `4h`/240 or `24h`/1440 |
| `modelRelease` | The model version that made it |
| `referencePrice` | BTC/USDT price when the forecast was made; the ranges are centred on it |
| `low50`, `high50`, `low90`, `high90` | The 50% and 90% ranges |
| `priceSource` | The price feed the forecast was made from (`binance`) |
| `status` | `scored`, `pending` (not known yet) or `excluded` (outcome from another feed; never scored) |
| `actualPrice` | The price it was scored against |
| `inside50`, `inside90`, `miss90` | Whether it landed inside each range, and on which side it missed the 90% range |

**How much a week tells you.** Forecasts overlap, so a week of 2,016 forecasts is worth about 168 independent outcomes at 1 hour, 42 at 4 hours and only 7 at 24 hours. The margins reflect that:

| Horizon | 90% range held | 50% range held |
|---|---|---|
| 1h | 90.1% ± 4.5 | 53.1% ± 7.6 |
| 4h | 88.8% ± 9.1 | 58.8% ± 15.1 |
| 24h | 77.3% ± 22.2 | 49.0% ± 37.0 |

All six are within their margins (calibrated). The 24-hour figure looks low, but a week holds only about seven separate 24-hour outcomes, so it says little either way. This is one week, not BtcForecaster's track record: for the full live record, see the [track record page](https://btcforecaster.com/track-record) or `/api/v1/live-stats`.

To score a different week, export it with `scripts/export-sample.ps1` and run `btcf-eval score` on the file.

## `expected-results.json`

The scores BtcForecaster's own code computed for exactly these forecasts (coverage, margins, widths, interval scores, misses, daily breakdown). `ScoringTests.Scoring_the_sample_reproduces_the_sites_own_results` checks this library gets the same numbers to within 1e-9.

## Prices

The only prices here are each forecast's reference price and the price it was scored against, as published by BtcForecaster's API. No raw market data (candles or price history) is included; `btcf-eval download` fetches that from Binance's public endpoint into a local cache when you need it.
