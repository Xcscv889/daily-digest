# SessionStart hook: inject yesterday's follow-up questions into a new session.
# Prints hook JSON on stdout; prints nothing when there is nothing to say.

[Console]::OutputEncoding = New-Object Text.UTF8Encoding $false
$ErrorActionPreference = "SilentlyContinue"
$root = Join-Path $env:USERPROFILE ".daily-digest"
$next = Join-Path $root "state\next.md"
$current = Join-Path $root "current.md"
$log = Join-Path $root "memo.log"

function Log($m) {
    try { Add-Content -Path $log -Value ("{0} hook: {1}" -f (Get-Date -Format "s"), $m) -Encoding utf8 } catch { }
}

if (-not (Test-Path $next)) { exit 0 }

$lines = @(Get-Content $next -Encoding utf8)

# questions: numbered lines, skip the metadata sub-bullets
$questions = @()
foreach ($line in $lines) {
    if ($line -match '^\s*\d+\.\s+(.+)$') { $questions += $Matches[1].Trim() }
}
if ($questions.Count -eq 0) { exit 0 }

$title = ($lines | Where-Object { $_ -match '^#\s+(.+)$' } | Select-Object -First 1)
$title = if ($title) { $title -replace '^#\s+', '' } else { "上一次的追问" }

# open items from the memo view
$open = @()
if (Test-Path $current) {
    foreach ($line in (Get-Content $current -Encoding utf8)) {
        if ($line -match '^- \[ \]\s+(.+)$') { $open += $Matches[1].Trim() }
    }
}

$sb = New-Object Text.StringBuilder
[void]$sb.Append("【$title】`n").Append("上次没答完的问题：`n")

foreach ($q in $questions | Select-Object -First 3) { [void]$sb.Append("- " + $q + "`n") }
if ($open.Count -gt 0) {
    [void]$sb.Append("还挂着的待办：`n")
    foreach ($o in $open | Select-Object -First 3) { [void]$sb.Append("- " + $o + "`n") }
}
[void]$sb.Append("用户如果问的是别的事，忽略上面内容，不要主动提起。要重新生成日报时说 /daily-digest。")

$payload = @{
    hookSpecificOutput = @{
        hookEventName     = "SessionStart"
        additionalContext = $sb.ToString().Trim()
    }
} | ConvertTo-Json -Depth 5 -Compress

Log ("injected {0} questions" -f $questions.Count)
[Console]::Out.Write($payload)
