# 守护：窗口没在跑就拉起来（计划任务每 10 分钟调用一次）
$exe = "D:\ClaudePlugins\daily-digest\scripts\native\DailyMemoWpf.exe"
$log = Join-Path $env:USERPROFILE ".daily-digest\memo.log"

if (-not (Test-Path $exe)) { exit 0 }

$running = Get-Process DailyMemoWpf -ErrorAction SilentlyContinue
if ($running) { exit 0 }

Start-Process $exe
try {
    Add-Content -Path $log -Value ("{0} watchdog: restarted" -f (Get-Date -Format "s")) -Encoding utf8
} catch { }
