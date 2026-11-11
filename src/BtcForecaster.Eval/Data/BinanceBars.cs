using System.Globalization;
using System.Text.Json;
using BtcForecaster.Eval.Baseline;

namespace BtcForecaster.Eval.Data;

/// <summary>
/// Public BTC/USDT 5-minute bars from Binance's public market-data endpoint
/// (<c>https://data-api.binance.vision/api/v3/klines</c>, no key needed), cached locally as one CSV per month under
/// <c>data/cache/</c>, which git ignores. Raw candles are never committed to this repository.
/// <para>
/// Please respect Binance's terms of use and API limits: this downloader asks for 1,000 bars (about 3.5 days) per request, waits
/// between requests, backs off when told to (HTTP 429/418, Retry-After), and only downloads months that aren't cached yet.
/// Use the data for your own analysis; check Binance's terms before redistributing it.
/// </para>
/// </summary>
public sealed class BinanceBars(string cacheDir, HttpClient? http = null)
{
    public const string BaseUrl = "https://data-api.binance.vision";
    public const string Symbol = "BTCUSDT";
    private static readonly TimeSpan Bar = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(300);

    private readonly HttpClient _http = http ?? CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("btcforecaster-eval/1.0");
        return client;
    }

    /// <summary>Bars with close times in [from, to), downloading only the months missing from the cache. Oldest first.</summary>
    public async Task<List<PriceSample>> LoadAsync(DateTime from, DateTime to, Action<string>? log = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cacheDir);
        var result = new List<PriceSample>();
        for (var month = new DateTime(from.Year, from.Month, 1, 0, 0, 0, DateTimeKind.Utc); month < to; month = month.AddMonths(1))
        {
            var path = Path.Combine(cacheDir, $"{Symbol}-5m-{month:yyyy-MM}.csv");
            var monthEnd = month.AddMonths(1);
            var complete = monthEnd <= DateTime.UtcNow - TimeSpan.FromHours(1);
            List<PriceSample> bars;
            if (File.Exists(path) && complete)
                bars = ReadCsv(path);
            else
            {
                log?.Invoke($"Downloading {Symbol} 5m bars for {month:yyyy-MM} from {BaseUrl} …");
                bars = await DownloadAsync(month, complete ? monthEnd : DateTime.UtcNow, ct);
                if (complete) WriteCsv(path, bars); // a month still in progress is fetched again next time
            }
            result.AddRange(bars.Where(b => b.Timestamp >= from && b.Timestamp < to));
        }
        return result;
    }

    /// <summary>Bars whose close falls in [from, to): each stamped at its close time (open time + 5 minutes) with its close price.</summary>
    public async Task<List<PriceSample>> DownloadAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var bars = new List<PriceSample>();
        var start = Ms(from - Bar);
        var end = Ms(to - Bar) - 1;
        while (start <= end)
        {
            var url = $"/api/v3/klines?symbol={Symbol}&interval=5m&startTime={start}&endTime={end}&limit=1000";
            using var response = await _http.GetAsync(url, ct);
            if ((int)response.StatusCode is 429 or 418)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                await Task.Delay(wait, ct);
                continue;
            }
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            var page = 0;
            foreach (var k in doc.RootElement.EnumerateArray())
            {
                page++;
                var openMs = k[0].GetInt64();
                var close = double.Parse(k[4].GetString()!, CultureInfo.InvariantCulture);
                bars.Add(new PriceSample(DateTimeOffset.FromUnixTimeMilliseconds(openMs).UtcDateTime + Bar, close));
                start = openMs + 1;
            }
            if (page < 1000) break;
            await Task.Delay(Pause, ct);
        }
        return bars.Where(b => b.Timestamp >= from && b.Timestamp < to).DistinctBy(b => b.Timestamp).OrderBy(b => b.Timestamp).ToList();
    }

    public static List<PriceSample> ReadCsv(string path) =>
        File.ReadLines(path).Skip(1).Where(l => l.Length > 0).Select(l => l.Split(','))
            .Select(p => new PriceSample(DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(p[0], CultureInfo.InvariantCulture)).UtcDateTime,
                double.Parse(p[1], CultureInfo.InvariantCulture)))
            .ToList();

    public static void WriteCsv(string path, IEnumerable<PriceSample> bars) =>
        File.WriteAllLines(path, bars.Select(b => $"{Ms(b.Timestamp)},{b.Price.ToString("R", CultureInfo.InvariantCulture)}").Prepend("closeTimeMs,close"));

    private static long Ms(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
