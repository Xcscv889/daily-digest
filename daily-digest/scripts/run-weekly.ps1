# Weekly entry: aggregate the last 7 daily reports
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$OutputEncoding = [Text.Encoding]::UTF8
$root = Join-Path $env:USERPROFILE ".daily-digest"
$log = Join-Path $root "run.log"

function Log($m) {
    try { Add-Content -Path $log -Value ("{0} {1}" -f (Get-Date -Format "s"), $m) -Encoding utf8 } catch { }
}

Log "----- weekly -----"
try {
    $output = & claude -p "/daily-digest week" --permission-mode acceptEdits 2>&1
    Log ("claude exit $LASTEXITCODE; " + (($output | Select-Object -Last 3) -join " | "))
} catch {
    Log ("weekly failed: " + $_.Exception.Message)
}

$cfg = Get-Content (Join-Path $root "config.json") -Raw | ConvertFrom-Json
$week = Get-Date -Format "yyyy-\WW"
$file = Join-Path $root "weekly\$week.md"
if ((Test-Path $file) -and $cfg.archive.driver -eq "local") {
    try {
        $dir = Join-Path $cfg.archive.local_path "weekly"
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Copy-Item $file $dir -Force
        Log "weekly archived -> $dir"
    } catch {
        Log ("weekly archive failed: " + $_.Exception.Message)
    }
}
