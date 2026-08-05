# CreamyKeys

System-wide keyboard typing sounds for Windows, built around the Opera GX "Creamy Keyboard" mod samples.

It lives in the system tray next to the clock (check behind the `^` arrow if you don't see the keycap icon). Click the icon to mute or unmute. Right-click it — two-finger tap on a touchpad — to open the menu with volume, start with Windows, and exit.

Build it with the compiler that ships with Windows:

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /win32icon:icon.ico /out:CreamyKeys.exe CreamyKeys.cs
```

The samples in `sounds\` come from the Opera GX Creamy Keyboard mod. Swap in any short wavs with the same names to change the sound.
