# CreamyKeys

System-wide keyboard typing sounds for Windows, built around the Opera GX "Creamy Keyboard" mod samples. Runs as a tray app: left-click mutes, right-click for volume and start-with-Windows.

Build it with the compiler that ships with Windows:

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /win32icon:icon.ico /out:CreamyKeys.exe CreamyKeys.cs
```

Sounds aren't included. Drop `letter_1.wav`, `letter_2.wav`, `letter_3.wav`, `space.wav`, `enter.wav`, `backspace.wav` into `sounds\` — if you have the mod installed in Opera GX, copy them from the extension's `keyboard` folder.
