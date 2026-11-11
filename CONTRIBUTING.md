# Contributing

Thanks for taking a look. Bug reports, questions about the method and pull requests are all welcome.

- **Found a scoring bug, or think a formula is wrong?** Open an issue with a small example: the forecasts, what you expected and what you got. The scoring has to match what the live site publishes, so changes to it are checked against `data/sample/expected-results.json`.
- **Pull requests:** keep them focused, add or update a test, and run `dotnet test` before opening one. The CI runs the same on every push and pull request.
- **Tests run offline.** Don't add tests that need the network; use the sample data or synthetic prices.
- **No market data in the repository.** Don't commit downloaded candles (`data/cache/` is ignored for this reason).
- **New model ideas** are welcome as issues. To count as better, a model has to pass the decision rule in the README on a holdout period it was never tuned on.

By contributing you agree that your contribution is released under the repository's [MIT licence](LICENSE), and to follow the [code of conduct](CODE_OF_CONDUCT.md).

Nothing in this repository is financial advice.
