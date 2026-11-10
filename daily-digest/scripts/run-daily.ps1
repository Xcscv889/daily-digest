# Scheduled entry: catch up every missing day -> collect -> analyze -> archive -> cleanup
param([string]$Date = "")

[Console]::OutputEncoding = [Text.Encoding]::UTF8
$OutputEncoding = [Text.Encoding]::UTF8
$root = Join-Path $env:USERPROFILE ".daily-digest"
$log = Join-Path $root "run.log"
$markFile = Join-Path $root "state\last-run.json"
$maxBackfill = 14   # 最多补两周，防止首次运行或长期关机后狂跑

function Log($m) {
    try { Add-Content -Path $log -Value ("{0} {1}" -f (Get-Date -Format "s"), $m) -Encoding utf8 } catch { }
}

if ((Test-Path $log) -and ((Get-Item $log).Length -gt 2MB)) { Move-Item $log "$log.old" -Force }

# ---- 决定要跑哪几天 ----
$yesterday = (Get-Date).AddDays(-1).Date
$days = @()
if ($Date) {
    $days = @([datetime]::ParseExact($Date, "yyyy-MM-dd", $null).Date)
    Log "----- run $Date (explicit) -----"
} else {
    $last = $null
    try {
        if (Test-Path $markFile) {
            $j = Get-Content $markFile -Raw | ConvertFrom-Json
            if ($j.last_run_date) { $last = [datetime]::ParseExact($j.last_run_date, "yyyy-MM-dd", $null).Date }
        }
    } catch { }
    if (-not $last) {
        $last = $yesterday.AddDays(-1)   # 没有标记时只跑昨天，不追溯历史
    }
    $from = $last.AddDays(1)
    if ($from -lt (Get-Date).AddDays(-$maxBackfill).Date) {
        Log ("backfill capped: {0} -> {1}" -f $from.ToString("yyyy-MM-dd"), (Get-Date).AddDays(-$maxBackfill).Date.ToString("yyyy-MM-dd"))
        $from = (Get-Date).AddDays(-$maxBackfill).Date
    }
    $d = $from
    while ($d -le $yesterday) { $days += $d; $d = $d.AddDays(1) }
    if ($days.Count -gt 0) {
        Log ("----- run catch-up {0} .. {1} ({2} day(s)) -----" -f $from.ToString("yyyy-MM-dd"), $yesterday.ToString("yyyy-MM-dd"), $days.Count)
    }
}

if ($days.Count -eq 0) { Log "nothing to do"; exit 0 }

$cfg = Get-Content (Join-Path $root "config.json") -Raw | ConvertFrom-Json
$todo = @()
$markTo = $null      # 只有连续跑通的日子才推进 last-run 标记
$failed = $false

foreach ($day in $days) {
    $ds = $day.ToString("yyyy-MM-dd")
    $report = Join-Path $root "days\$ds.md"
    $json = Join-Path $root ("work\day-{0}.json" -f $ds)
    $settled = $false

    try {
        $collectOut = & python (Join-Path $PSScriptRoot "collect.py") --date $ds 2>&1
        Log ("[" + $ds + "] " + ($collectOut -join " | "))
    } catch {
        Log ("[" + $ds + "] collect failed: " + $_.Exception.Message)
        $failed = $true
        continue
    }

    # 0 会话的天不调 claude：省一次调用，也不生成空洞的日报
    $sessions = -1
    try {
        if (Test-Path $json) {
            $j = Get-Content $json -Raw | ConvertFrom-Json
            $sessions = @($j.sessions).Count
        }
    } catch { }
    if ($sessions -eq 0 -and -not (Test-Path $report)) {
        Log ("[" + $ds + "] no sessions, skipped")
        $todo += $ds
        $settled = $true
    }
    elseif (Test-Path $report) {
        Log ("[" + $ds + "] report exists, skipped")
        $todo += $ds
        $settled = $true
    }
    else {
        # 无头会话的工作目录不是 ~/.daily-digest，必须显式放行数据目录和插件目录，
        # 否则读 day.json 会被「allowed working directories」拦掉，而且退出码仍是 0
        $output = & claude -p "/daily-digest $ds" --permission-mode acceptEdits `
            --add-dir $root --add-dir $PSScriptRoot --add-dir (Split-Path $PSScriptRoot) 2>&1
        Log ("[" + $ds + "] claude exit $LASTEXITCODE; " + (($output | Select-Object -Last 3) -join " | "))
        $todo += $ds
        if (Test-Path $report) {
            $settled = $true
        } else {
            Log ("[" + $ds + "] no report written, will retry on next run")
        }
    }

    if (-not $failed) {
        if ($settled) { $markTo = $day } else { $failed = $true }
    }
}

# ---- 归档（每份新生成的日报各传一次）----
foreach ($ds in $todo) {
    $report = Join-Path $root "days\$ds.md"
    if (-not (Test-Path $report)) { continue }
    try {
        switch ($cfg.archive.driver) {
            "local" {
                New-Item -ItemType Directory -Force -Path $cfg.archive.local_path | Out-Null
                Copy-Item $report $cfg.archive.local_path -Force
                Log ("[" + $ds + "] archived -> " + $cfg.archive.local_path)
            }
            "baidu" {
                $bdpan = Join-Path $env:USERPROFILE ".local/bin/bdpan.exe"
                if (-not (Test-Path $bdpan)) { throw "bdpan 未安装: $bdpan" }
                $remote = "ClaudeDigest/" + [IO.Path]::GetFileName($report)
                $sid = "{0}-{1}" -f [DateTimeOffset]::Now.ToUnixTimeSeconds(), (Get-Random -Maximum 999999)
                $out = & $bdpan upload $report $remote --agentname "claude-code" --session-input "daily digest archive" --session-id $sid --overwrite 2>&1
                Log ("[" + $ds + "] baidu upload exit=" + $LASTEXITCODE + " " + ($out -join " | "))
            }
            default { Log ("[" + $ds + "] unknown archive driver: " + $cfg.archive.driver) }
        }
    } catch {
        Log ("[" + $ds + "] archive failed: " + $_.Exception.Message)
    }
}

# ---- 清理 ----
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

# ---- 记标记：只推到连续跑通的那天，失败的天留到下次重试 ----
if (-not $Date) {
    if ($markTo) {
        try {
            New-Item -ItemType Directory -Force -Path (Split-Path $markFile) | Out-Null
            @{ last_run_date = $markTo.ToString("yyyy-MM-dd"); updated = (Get-Date -Format "s") } |
                ConvertTo-Json | Set-Content $markFile -Encoding utf8
        } catch { Log ("mark failed: " + $_.Exception.Message) }
    } else {
        Log "marker unchanged (no day settled)"
    }
}
