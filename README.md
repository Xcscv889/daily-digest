# daily-digest

Claude Code 插件：把每天的会话记录整理成工作日报，第二天早上提醒你。

- **每日分析** —— 做了什么、时间花在哪、提问质量如何
- **次日追问** —— 新开会话时把昨天没答完的问题带进来
- **桌面小窗** —— 常驻屏幕边缘，显示今天待办和今天的三个追问
- **归档** —— 日报可上传到百度网盘或留在本地

## 安装

```bash
claude plugin marketplace add Xcscv889/daily-digest
claude plugin install daily-digest@local
```

## 使用

```
/daily-digest              # 分析昨天（04:00 日界）
/daily-digest 2026-11-04   # 指定日期
/daily-digest week         # 最近一周
```

## 仓库结构

```
.claude-plugin/marketplace.json   插件清单
daily-digest/                     插件本体
├── skills/daily-digest/SKILL.md  分析流程与报告格式
├── hooks/hooks.json              SessionStart 注入
└── scripts/                      采集、归档、桌面小窗
```

插件的详细说明、配置项、隐私边界和编译方法见 **[daily-digest/README.md](daily-digest/README.md)**。

## 要求

- Windows 10/11 + PowerShell 7 + Python 3.9+
- Claude Code CLI 已登录

## License

MIT
