param(
    [string]$Path = (Join-Path $PSScriptRoot '..\samples\demo.log'),
    [int]$Seed = 300,          # lines written up front
    [int]$IntervalMs = 400     # then one new line this often, until Ctrl+C
)
# Writes a realistic-looking log for trying Logictail, then keeps appending to it so Follow tail has something to follow.
New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
$services = 'orders-api', 'billing', 'inventory', 'auth'
function Line([int]$i) {
    $ts = (Get-Date).AddSeconds($i - $Seed).ToString('yyyy-MM-dd HH:mm:ss.fff')
    $svc = $services[$i % $services.Count]
    switch ($i % 13) {
        4 { "$ts WARN  [$svc] Slow response from database: $(700 + $i % 400) ms (threshold 750 ms)" }
        9 { "$ts ERROR [$svc] Request $((48000 + $i)) failed: connection reset by peer" }
        11 { "$ts ERROR [$svc] Unhandled exception: System.TimeoutException: The operation has timed out.`n    at Logic.Orders.Sync() in Sync.cs:line 88" }
        default { "$ts INFO  [$svc] GET /api/orders/$((48000 + $i)) completed 200 in $(8 + $i % 60) ms" }
    }
}
Set-Content -Path $Path -Value (0..($Seed - 1) | ForEach-Object { Line $_ }) -Encoding utf8
Write-Host "Writing to $((Resolve-Path $Path).Path) - open it in Logictail. Ctrl+C to stop."
$i = $Seed
while ($true) {
    Add-Content -Path $Path -Value (Line $i) -Encoding utf8
    $i++
    Start-Sleep -Milliseconds $IntervalMs
}
