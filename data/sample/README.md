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

### Misses by side and volatility

`btcf-eval score <file> --by-volatility` adds the `byVolatilityBucket` breakdown from live-stats: for each horizon and for calm,
normal and active conditions at forecast time, how often outcomes fell below and above the 90% range, and by how much. It
downloads the Binance bars it needs (30 days + 24 hours before the first forecast) into `data/cache`. For this week:

| Horizon | Conditions | n | n_eff | Below: misses | Held below | Avg miss | Above: misses | Held above | Avg miss |
|---|---|---|---|---|---|---|---|---|---|
| 1h | calm | 372 | 31.0 | 36 | 90.3 (±7.7) | −0.52% | 19 | 94.9 (±7.7) | +0.18% |
| | normal | 132 | 11.0 | 18 | 86.4 (±12.9) | −0.33% | 0 | 100.0 (±12.9) | – |
| | active | 1,512 | 126.0 | 61 | 96.0 (±3.8) | −0.63% | 65 | 95.7 (±3.8) | +0.46% |
| 4h | calm | 372 | 7.8 | 41 | 89.0 (±15.3) | −0.77% | 0 | 100.0 (±15.3) | – |
| | normal | 132 | 2.8 | 33 | 75.0 (±25.8) | −0.54% | 0 | 100.0 (±25.8) | – |
| | active | 1,512 | 31.5 | 75 | 95.0 (±7.6) | −1.67% | 76 | 95.0 (±7.6) | +0.83% |
| 24h | calm | 372 | 1.3 | 3 | 99.2 (±37.6) | −0.09% | 0 | 100.0 (±37.6) | – |
| | normal | 132 | 1.0 | 0 | 100.0 (±42.7) | – | 40 | 69.7 (±42.7) | +0.80% |
| | active | 1,512 | 5.2 | 284 | 81.2 (±18.6) | −1.75% | 131 | 91.3 (±18.6) | +1.00% |

Each side of a 90% range should hold about 95%. Conditions: the last 24 hours' realised variance ÷ its median over the
previous 30 days, calm ≤ 0.738 < normal ≤ 1.126 < active (terciles fixed on January–May 2026). Most of this week was active.
Split three ways and by side, a week says very little: none of these cells is outside its margin.

## `expected-results.json`

The scores BtcForecaster's own code computed for exactly these forecasts (coverage, margins, widths, interval scores, misses, daily breakdown). `ScoringTests.Scoring_the_sample_reproduces_the_sites_own_results` checks this library gets the same numbers to within 1e-9.

## Prices

The only prices here are each forecast's reference price and the price it was scored against, as published by BtcForecaster's API. No raw market data (candles or price history) is included; `btcf-eval download` fetches that from Binance's public endpoint into a local cache when you need it.
