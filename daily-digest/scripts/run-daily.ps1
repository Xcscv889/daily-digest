# Scheduled entry: collect -> analyze -> archive -> cleanup
param([string]$Date = (Get-Date).AddDays(-1).ToString("yyyy-MM-dd"))

[Console]::OutputEncoding = [Text.Encoding]::UTF8
$OutputEncoding = [Text.Encoding]::UTF8
$root = Join-Path $env:USERPROFILE ".daily-digest"
$log = Join-Path $root "run.log"

function Log($m) {
    try { Add-Content -Path $log -Value ("{0} {1}" -f (Get-Date -Format "s"), $m) -Encoding utf8 } catch { }
}

if ((Test-Path $log) -and ((Get-Item $log).Length -gt 2MB)) { Move-Item $log "$log.old" -Force }
Log "----- run $Date -----"

try {
    $collectOut = & python (Join-Path $PSScriptRoot "collect.py") --date $Date 2>&1
    Log ($collectOut -join " | ")
} catch {
    Log ("collect failed: " + $_.Exception.Message)
    exit 1
}

$prompt = "/daily-digest $Date"
$output = & claude -p $prompt --permission-mode acceptEdits 2>&1
Log ("claude exit $LASTEXITCODE; " + (($output | Select-Object -Last 3) -join " | "))

$cfg = Get-Content (Join-Path $root "config.json") -Raw | ConvertFrom-Json
$report = Join-Path $root "days\$Date.md"

if (Test-Path $report) {
    try {
        switch ($cfg.archive.driver) {
            "local" {
                New-Item -ItemType Directory -Force -Path $cfg.archive.local_path | Out-Null
                Copy-Item $report $cfg.archive.local_path -Force
                Log "archived -> $($cfg.archive.local_path)"
            }
            "baidu" {
                $bdpan = Join-Path $env:USERPROFILE ".local/bin/bdpan.exe"
                if (-not (Test-Path $bdpan)) { throw "bdpan 未安装: $bdpan" }
                $remote = "ClaudeDigest/" + [IO.Path]::GetFileName($report)
                $sid = "{0}-{1}" -f [DateTimeOffset]::Now.ToUnixTimeSeconds(), (Get-Random -Maximum 999999)
                $out = & $bdpan upload $report $remote --agentname "claude-code" --session-input "daily digest archive" --session-id $sid --overwrite 2>&1
                Log ("baidu upload exit=" + $LASTEXITCODE + " " + ($out -join " | "))
            }
            default { Log ("unknown archive driver: " + $cfg.archive.driver) }
        }
    } catch {
        Log ("archive failed: " + $_.Exception.Message)
    }
}

try {
    Get-ChildItem (Join-Path $root "days") -Filter *.md |
        Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-[int]$cfg.keep_local_days) } |
        Remove-Item -Force
    Get-ChildItem (Join-Path $root "work") -Filter "day-*.json" |
        Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-3) } |
        Remove-Item -Force
    Log "cleanup done"
} catch {
    Log ("cleanup failed: " + $_.Exception.Message)
}
