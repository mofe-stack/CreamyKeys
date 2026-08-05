# CreamyKeys

System-wide keyboard typing sounds for Windows, built around the Opera GX "Creamy Keyboard" mod samples.

It lives in the system tray next to the clock (check behind the `^` arrow if you don't see the keycap icon). Click the icon to mute or unmute. Right-click it (two-finger tap on a touchpad) to open the menu with volume, start with Windows, and exit.

**Install:** grab `CreamyKeys.zip` from [Releases](https://github.com/mofe-stack/CreamyKeys/releases), unzip it anywhere, run `CreamyKeys.exe`. Nothing else to set up. Or build it yourself with the compiler that ships with Windows:

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /win32icon:icon.ico /out:CreamyKeys.exe CreamyKeys.cs
```

**Your own sounds:** the samples in `sounds\` come from the Opera GX Creamy Keyboard mod. Replace them with any short wavs using the same six names, then exit and reopen the app.
