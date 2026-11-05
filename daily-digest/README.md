# daily-digest

把 Claude Code 的本地会话记录整理成每日工作日报，第二天早上提醒你 —— Claude Code 插件 + 桌面备忘小窗。

## 它做什么

- **每天 08:00** 自动分析前一天（04:00 日界）的会话：做了什么、时间花在哪、提问质量如何
- **次日提醒**：新开会话时，把昨天没答完的问题注入上下文
- **桌面小窗**：常驻屏幕边缘，显示今天要做的、今天该问自己的、昨天的一行摘要
- **归档**：日报可上传到百度网盘（或本地 / rclone / Notion / 钉盘，按配置切换）

## 安装

```bash
claude plugin marketplace add <owner>/<repo>
claude plugin install daily-digest@local
```

数据目录固定在 `~/.daily-digest/`，插件本身不含任何数据。

## 使用

```
/daily-digest              # 分析昨天
/daily-digest 2026-11-04   # 指定日期
/daily-digest week         # 最近一周
```

## 组成

| 路径 | 作用 |
|---|---|
| `skills/daily-digest/SKILL.md` | 分析流程、报告格式、证据规则 |
| `hooks/hooks.json` | `SessionStart` 注入昨日追问 |
| `scripts/collect.py` | 只读抽取会话记录 → `day.json` |
| `scripts/live.py` | 今日实时统计（可选） |
| `scripts/session-start.ps1` | hook 入口，输出 UTF-8 JSON |
| `scripts/run-daily.ps1` | 每日任务：抽取 → 分析 → 归档 → 清理 |
| `scripts/run-weekly.ps1` | 周报任务 |
| `scripts/native/DailyMemoWpf.exe` | 桌面小窗（C# WPF 编译版，约 80 MB） |
| `scripts/native/MemoWpf.cs` | 小窗源码 |
| `scripts/memo.ps1` | 小窗旧版（PowerShell，约 270 MB，保留备查） |

## 数据目录

```
~/.daily-digest/
├── config.json      配置：日界、排除项目、归档驱动、天气坐标
├── tasks.md         待办（小窗读写）
├── days/*.md        每天的日报
├── state/next.md    次日追问
├── current.md       小窗读的摘要视图
└── work/            中间产物，3 天后自动清理
```

## 配置

```json
{
  "day_boundary": "04:00",
  "keep_local_days": 7,
  "exclude_mode": "content_anonymous",
  "exclude_projects": ["MyCompany-Repo"],
  "archive": { "driver": "local", "local_path": "D:/DailyDigest" },
  "weather": { "city": "南京", "latitude": 32.06, "longitude": 118.80 }
}
```

天气用免费接口 [Open-Meteo](https://open-meteo.com/)，`weather` 段填你自己城市的经纬度即可（城市名只是备注）。取不到时显示「天气不可用」，原因写进 `memo.log`。

`exclude_mode` 三档：

| 值 | 行为 |
|---|---|
| `full` | 整段跳过，日报里不出现 |
| `stats_only` | 只保留时间 / 报错数，不采集内容 |
| `content_anonymous` | 采集内容，但项目名显示为「已排除项目 · A/B/C」 |

归档驱动：`local` / `baidu`（百度网盘 CLI）/ `rclone` / `notion` / `dingtalk` / `shell`。

## 桌面小窗

- 编译版位于 `scripts/native/DailyMemoWpf.exe`，界面用 XAML 字符串在运行时解析，单文件无依赖
- 需要 .NET Framework 4.8（Windows 10/11 自带）
- 贴边吸附、3 秒自动隐藏、`Ctrl+Alt+N` 或**语音「芝麻开门」**唤出
- 不可缩放，误触最大化会自动还原
- 标题栏：今天的日期、居中的七段数码管时钟（可切换成 ASCII 小宠物）、实时天气（Open-Meteo）
- 待办：回车新增、双击勾选、悬停 × 删除
- 内容顺序：今天要做的 → 今天先问自己 → 昨天一行摘要

重新编译（系统自带 csc，无需 .NET SDK）：

```
csc -nologo -target:winexe -optimize+ ^
  -r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\PresentationFramework\v4.0_4.0.0.0__31bf3856ad364e35\PresentationFramework.dll" ^
  -r:"%WINDIR%\Microsoft.NET\assembly\GAC_64\PresentationCore\v4.0_4.0.0.0__31bf3856ad364e35\PresentationCore.dll" ^
  -r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll" ^
  -r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll" ^
  -out:DailyMemoWpf.exe MemoWpf.cs
```

### 关于仓库里的 exe

`scripts/native/DailyMemoWpf.exe` 是编译产物，直接提交进仓库是为了免去用户自己装编译器。它由同目录的 `MemoWpf.cs` 编译而来，没有任何依赖，也可以自己重新编译后替换。

SHA256（v4.6.0）：

```
A3DA5A6E53D57E87A4E8499D434A9D16D8036C3FD7E465DD504B6DC81FCF926C
```

不想运行现成二进制的话，按上面的 csc 命令编译，或者直接用 `scripts/memo.ps1`（PowerShell 版，功能相同但内存约 270 MB）。

## 依赖

- Windows + PowerShell 7 + Python 3.9+
- 无头分析需要 `claude` CLI 已登录

## 隐私

- 只读 `~/.claude/projects/**/*.jsonl`，不截屏、不读浏览器、不扫文件夹
- 日报默认只落本地；配置云盘驱动后，日报正文才会离开本机
- `exclude_projects` 里的项目可配置成完全不采集内容

## 排错

- 日志：`~/.daily-digest/memo.log`、`run.log`
- 小窗没出现：`Get-Process DailyMemoWpf`
- 天气不可用：检查 `config.json` 的坐标和网络（需要 TLS 1.2）
- 日报为空：确认当天在非排除项目里有会话

## License

MIT
