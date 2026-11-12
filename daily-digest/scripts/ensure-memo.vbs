Set sh = CreateObject("WScript.Shell")
rc = sh.Run("""C:\Users\37964\AppData\Local\Microsoft\WindowsApps\pwsh.exe"" -NoProfile -WindowStyle Hidden -File ""D:\ClaudePlugins\daily-digest\scripts\ensure-memo.ps1""", 0, True)
WScript.Quit rc
