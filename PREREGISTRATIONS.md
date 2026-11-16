# Pre-registrations

Decision rules for BtcForecaster's models, written before the live data they are judged on exists. One entry per test, newest
first. Each rule is kept as registered; anything added later is a dated status line under it. The same text is on the
[methodology page](https://btcforecaster.com/how-it-works#preregistered) and in the app's README.

Live forecasts and outcomes for both models are public: `GET https://btcforecaster.com/api/v1/forecasts?model=v2-har` and
`?model=v3-har-cal90` (see [/developers](https://btcforecaster.com/developers)), so anyone can check a result.

## 2. Second v3 shadow test (registered 16 November 2026)

**New pre-registered test, registered 16 November 2026.** The first shadow test (above) ends on 23 November. It is likely to fail for a reason I didn't account for when I wrote it: two weeks of 24-hour forecasts is only about 14 independent results, too few to show a 2% difference at p < 0.05. Since then, a test on 2024 and 2025, two years never used to build or choose any model, found v3 3.7% better than v2 at 24 hours (p < 0.001), and better in both years separately. [Report](https://btcforecaster.com/how-it-works/out-of-sample-2024-2025)

v3 keeps running as a shadow model. It becomes the headline model on **7 December 2026** unless, on live forecasts made from **17 November 2026 00:00 UTC to 7 December 2026 00:00 UTC**, its 24-hour combined interval score is significantly worse than v2's (p < 0.05, Diebold-Mariano with Newey-West errors, lag 288), or its 90% or 50% coverage falls outside margin95 at any horizon. Live forecasts before 17 November are not used, because they have already been seen. The live window is a safety check; the main evidence is the two-year test. Whatever the result, it will be reported here.

("The first shadow test (above)" refers to the methodology page, where it comes first; here it is entry 1, below.)

## 1. First v3 shadow test (registered 9 November 2026)

Registered on 9 November 2026 on the methodology page and in the app's README. It was copied into this file on 16 November 2026,
so this repository's history dates it only from then.

**What changes:** the 24-hour range learns its width from the last 90 days of outcomes instead of the last 14; everything else, including the 1-, 4- and 12-hour ranges, is exactly v2.

**Why a live test:** on June–October 2026 this scored about 6% better at 24 hours and held 90% of the time, but most of that gain came from a single fortnight in August, so it failed our rule that a gain mustn't depend on one month. Rather than loosen the rule after the fact, the decision is made on new, live data, with the rule fixed now.

**The rule, fixed before the test starts** (start: 2026/11/09). After at least 14 days of live forecasts, the candidate becomes the main model only if all of these hold:

- its 24-hour interval score is at least 2% better than v2's, and the difference is statistically significant (p < 0.05);
- it is not more than 1% worse at 1 or 4 hours;
- its hit rate is within the margin of error of its target at every horizon;
- it passes the independence test at 24 hours (its misses don't bunch together or depend on the range's width) where v2 fails it.

If it fails, it is retired and listed with the rejected ideas. Its live record is on the [track record](https://btcforecaster.com/track-record) page and in [/api/live-stats](https://btcforecaster.com/api/live-stats) as `v3-har-cal90`.

*Status, 16 November 2026: this test ends on 23 November 2026 at 21:20 UTC. Its result will be reported here as written.*
