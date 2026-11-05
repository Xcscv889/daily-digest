Set sh = CreateObject("WScript.Shell")
home = sh.ExpandEnvironmentStrings("%USERPROFILE%")
sh.Run "pwsh.exe -NoProfile -WindowStyle Hidden -File """ & home & "\.daily-digest\bin\memo.ps1""", 0, False
