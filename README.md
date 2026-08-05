# CreamyKeys

System-wide keyboard typing sounds for Windows, built around the Opera GX "Creamy Keyboard" mod samples.

It lives in the system tray next to the clock (check behind the `^` arrow if you don't see the keycap icon). Click the icon to mute or unmute. Right-click it (two-finger tap on a touchpad) to open the menu with volume, start with Windows, and exit.

**Install:** [download CreamyKeys.zip here](https://github.com/mofe-stack/CreamyKeys/releases/latest/download/CreamyKeys.zip), unzip it anywhere, run `CreamyKeys.exe`. Nothing else to set up. Windows may warn because the exe is unsigned and it listens for keypresses to play the sounds — nothing is stored or sent anywhere. Or build it yourself with the compiler that ships with Windows:

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /win32icon:icon.ico /out:CreamyKeys.exe CreamyKeys.cs
```

**Your own sounds:** the samples in `sounds\` come from the Opera GX Creamy Keyboard mod. Replace them with any short wavs using the same six names, then exit and reopen the app.
