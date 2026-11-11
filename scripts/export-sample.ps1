<#
.SYNOPSIS
    Exports BtcForecaster live forecasts and their outcomes from the public API to a CSV that `btcf-eval score` reads.

.DESCRIPTION
    Pages through GET https://btcforecaster.com/api/v1/forecasts (at most 7 days per request; no key needed) and writes the
    same columns as data/sample. The API allows 60 requests per minute per IP; this script waits between pages.
    Terms: https://btcforecaster.com/developers#terms (free to use; attribution appreciated; not financial advice).

.EXAMPLE
    pwsh scripts/export-sample.ps1 -From 2026-11-04 -To 2026-11-11 -Out week.csv
    dotnet run --project tools/BtcForecaster.Eval.Cli -- score week.csv
#>
param(
    [Parameter(Mandatory)] [datetime] $From,
    [Parameter(Mandatory)] [datetime] $To,
    [string] $Out = "forecasts.csv",
    [string] $Model = "",
    [string] $BaseUrl = "https://btcforecaster.com"
)

$ErrorActionPreference = "Stop"
$from = [DateTime]::SpecifyKind($From, [DateTimeKind]::Utc)
$to = [DateTime]::SpecifyKind($To, [DateTimeKind]::Utc)
if (($to - $from).TotalDays -gt 7) { throw "At most 7 days per export (the API's limit per request)." }

function Iso([datetime] $t) { $t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture) }
function Num($v) { if ($null -eq $v) { "" } else { ([double]$v).ToString("R", [Globalization.CultureInfo]::InvariantCulture) } }
function Bool($v) { if ($null -eq $v) { "" } elseif ($v) { "true" } else { "false" } }

$url = "$BaseUrl/api/v1/forecasts?from=$(Iso $from)&to=$(Iso $to)&pageSize=1000" + ($(if ($Model) { "&model=$Model" } else { "" }))
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("forecastMadeUtc,targetUtc,horizon,horizonMinutes,modelRelease,referencePrice,low50,high50,low90,high90,priceSource,status,actualPrice,inside50,inside90,miss90")
while ($url) {
    Write-Host "GET $url"
    $page = Invoke-RestMethod -Uri $url -Headers @{ "User-Agent" = "btcforecaster-eval/1.0" }
    foreach ($f in $page.forecasts) {
        $o = $f.outcome
        $lines.Add((@(
            (Iso ([datetime]$f.forecastMadeUtc).ToUniversalTime()), (Iso ([datetime]$f.targetUtc).ToUniversalTime()), $f.horizon, $f.horizonMinutes,
            $f.modelRelease, (Num $f.referencePrice), (Num $f.range50.low), (Num $f.range50.high), (Num $f.range90.low), (Num $f.range90.high),
            $f.priceSource, $o.status, (Num $o.actualPrice), (Bool $o.inside50), (Bool $o.inside90), $o.miss90
        ) -join ","))
    }
    $url = if ($page.nextPage) { "$BaseUrl$($page.nextPage)" } else { $null }
    if ($url) { Start-Sleep -Seconds 2 }
}
Set-Content -Path $Out -Value $lines -Encoding utf8NoBOM
Write-Host "Wrote $($lines.Count - 1) forecasts to $Out (data: BtcForecaster)"
