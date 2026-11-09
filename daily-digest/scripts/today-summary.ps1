# 用一次 claude 调用，把今天的会话压成一句话，供小窗显示
$ErrorActionPreference = "SilentlyContinue"
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = Join-Path $env:USERPROFILE ".daily-digest"
$log = Join-Path $root "memo.log"
$out = Join-Path $root "today-summary.txt"
$today = (Get-Date).ToString("yyyy-MM-dd")

function Log($m) {
    try { Add-Content -Path $log -Value ("{0} today-summary: {1}" -f (Get-Date -Format "s"), $m) -Encoding utf8 } catch { }
}

$collect = "D:\ClaudePlugins\daily-digest\scripts\collect.py"
if (-not (Test-Path $collect)) { Log "collect.py 不存在"; exit 0 }

& python -X utf8 $collect --date $today *> $null
$json = Join-Path $root ("work\day-{0}.json" -f $today)
if (-not (Test-Path $json)) { Log "没有今天的 day.json"; exit 0 }

$prompt = @"
下面是用户今天（$today）在 Claude Code 里的工作摘要 JSON。请概括他今天做的**几件事**。

要求：
- 3 到 4 条，每条一行，不超过 16 个字
- 每条说一个模块或一件事（例如"渠道后台链接管理收口""日报插件小窗迭代"）
- 不要编号、不要符号、不要文件名、不要解释
- 只输出这些行，别的什么都不要

JSON：
"@

$trimmed = & python -X utf8 -c @"
import json,sys
d=json.load(open(r'$json',encoding='utf-8'))
s=d.get('sessions',[])
parts=[]
for x in s:
    if x.get('redacted') and not x.get('prompts'): continue
    parts.append({'项目':x.get('project_label'),'提问':x.get('prompts',[])[:12],'文件':list(x.get('files',{}).keys())[:8],'结论':x.get('excerpt',[])[:4]})
print(json.dumps(parts,ensure_ascii=False)[:6000])
"@

if (-not $trimmed) { Log "没有可提炼的内容"; exit 0 }

# 走 opus：settings.json 把 sonnet 映射成 deepseek-v4.1-flash[1m]，代理偶尔不认这个模型名
$answer = & claude -p ($prompt + $trimmed) --model opus 2>&1 | Out-String
$lines = @($answer -split "`n" |
    ForEach-Object { ($_ -replace '^\s*[-•\d.、]+\s*', '').Trim() } |
    Where-Object { $_.Length -ge 2 -and $_.Length -le 40 } |
    Select-Object -First 4)
if ($lines.Count -eq 0) { Log ("提炼失败: " + $answer); exit 0 }

$body = ($lines -join "`n")
Set-Content -Path $out -Value ("# {0} {1}`n{2}" -f $today, (Get-Date -Format "HH:mm"), $body) -Encoding utf8
Log ("ok: " + ($lines -join " / "))
