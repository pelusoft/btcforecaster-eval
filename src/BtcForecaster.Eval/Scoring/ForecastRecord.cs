using System.Globalization;

namespace BtcForecaster.Eval.Scoring;

/// <summary>
/// One forecast and, once known, its outcome: the shape of a row from BtcForecaster's <c>/api/v1/forecasts</c> (and of the CSV
/// files in <c>data/sample</c>). Prices are BTC/USDT; times are UTC.
/// </summary>
/// <param name="ForecastMadeUtc">When the forecast was made (a 5-minute slot).</param>
/// <param name="TargetUtc">The moment the ranges refer to.</param>
/// <param name="ReferencePrice">The price when the forecast was made; the ranges are centred on it.</param>
/// <param name="Status">"scored", "pending" or "excluded" (outcome from a different price feed; never scored).</param>
/// <param name="ActualPrice">The price the forecast was scored against (null unless scored).</param>
public sealed record ForecastRecord(
    DateTime ForecastMadeUtc, DateTime TargetUtc, int HorizonMinutes, string ModelRelease, double ReferencePrice,
    double? Low50, double? High50, double? Low90, double? High90, string Status, double? ActualPrice)
{
    public bool IsScored => Status == "scored" && ActualPrice is not null;
}

/// <summary>Reads and writes the forecasts CSV format (a header row, then one forecast per line; no quoted fields).</summary>
public static class ForecastCsv
{
    public const string Header =
        "forecastMadeUtc,targetUtc,horizon,horizonMinutes,modelRelease,referencePrice,low50,high50,low90,high90,priceSource,status,actualPrice,inside50,inside90,miss90";

    public static List<ForecastRecord> Read(string path) => Parse(File.ReadLines(path));

    public static List<ForecastRecord> Parse(IEnumerable<string> lines)
    {
        using var e = lines.GetEnumerator();
        if (!e.MoveNext()) return [];
        var columns = e.Current.Split(',').Select((name, i) => (name, i)).ToDictionary(c => c.name, c => c.i);
        int Col(string name) => columns.TryGetValue(name, out var i) ? i : throw new FormatException($"Missing column '{name}'.");
        var (made, target, horizon, release, reference, l50, h50, l90, h90, status, actual) =
            (Col("forecastMadeUtc"), Col("targetUtc"), Col("horizonMinutes"), Col("modelRelease"), Col("referencePrice"),
             Col("low50"), Col("high50"), Col("low90"), Col("high90"), Col("status"), Col("actualPrice"));

        var result = new List<ForecastRecord>();
        while (e.MoveNext())
        {
            if (string.IsNullOrWhiteSpace(e.Current)) continue;
            var f = e.Current.Split(',');
            result.Add(new ForecastRecord(
                Time(f[made]), Time(f[target]), int.Parse(f[horizon], CultureInfo.InvariantCulture), f[release],
                double.Parse(f[reference], CultureInfo.InvariantCulture),
                Number(f[l50]), Number(f[h50]), Number(f[l90]), Number(f[h90]), f[status], Number(f[actual])));
        }
        return result;
    }

    private static DateTime Time(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static double? Number(string s) => s.Length == 0 ? null : double.Parse(s, CultureInfo.InvariantCulture);
}
