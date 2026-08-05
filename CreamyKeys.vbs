' Launches CreamyKeys with no console window at all.
' Edit -Volume below (1-100) to change the default startup volume.
Dim sh, dir
Set sh = CreateObject("WScript.Shell")
dir = Left(WScript.ScriptFullName, InStrRev(WScript.ScriptFullName, "\") - 1)
sh.Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -File """ & dir & "\CreamyKeys.ps1"" -Volume 60", 0, False
