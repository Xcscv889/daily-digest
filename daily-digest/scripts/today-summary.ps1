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
下面是用户今天（$today）在 Claude Code 里的工作摘要 JSON。请用一句中文概括他今天主要在做什么，不超过 30 字，只输出这句话本身，不要引号、不要标点开头、不要解释。

要求：
- 说清"在做什么模块/功能"，不要罗列文件名
- 如果内容跨多个主题，挑最主要的一件
- 信息不足就输出"今天的工作比较零散"

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

$answer = & claude -p ($prompt + $trimmed) 2>&1 | Out-String
$answer = ($answer -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 1).Trim()
if (-not $answer -or $answer.Length -gt 60) { Log ("提炼失败: " + $answer); exit 0 }

Set-Content -Path $out -Value ("# {0} {1}`n{2}" -f $today, (Get-Date -Format "HH:mm"), $answer) -Encoding utf8
Log ("ok: " + $answer)
