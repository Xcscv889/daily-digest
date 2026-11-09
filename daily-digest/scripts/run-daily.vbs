' 无控制台启动 run-daily.ps1：计划任务用 wscript 调用，不会有 pwsh 控制台窗口被关掉
Option Explicit
Dim sh, fso, here, ps1
Set sh = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
here = fso.GetParentFolderName(WScript.ScriptFullName)
ps1 = here & "\run-daily.ps1"
If Not fso.FileExists(ps1) Then WScript.Quit 1
sh.Run "pwsh.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File """ & ps1 & """", 0, False
