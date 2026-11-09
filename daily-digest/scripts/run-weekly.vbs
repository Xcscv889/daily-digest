' 无控制台启动 run-weekly.ps1
Option Explicit
Dim sh, fso, here, ps1
Set sh = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
here = fso.GetParentFolderName(WScript.ScriptFullName)
ps1 = here & "\run-weekly.ps1"
If Not fso.FileExists(ps1) Then WScript.Quit 1
sh.Run "pwsh.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File """ & ps1 & """", 0, False
