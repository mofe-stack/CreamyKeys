# CreamyKeys

System-wide keyboard typing sounds for Windows, built around the Opera GX "Creamy Keyboard" mod samples. Runs as a tray app: left-click mutes, right-click for volume and start-with-Windows.

Build it with the compiler that ships with Windows:

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /win32icon:icon.ico /out:CreamyKeys.exe CreamyKeys.cs
```

The samples in `sounds\` come from the Opera GX Creamy Keyboard mod. Swap in any short wavs with the same names to change the sound.
