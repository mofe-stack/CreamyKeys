# CreamyKeys

System-wide keyboard typing sounds for Windows, built around the Opera GX "Creamy Keyboard" mod samples. Here's 30 seconds of what it sounds like:








https://github.com/user-attachments/assets/62961d59-9416-4dd9-8bd7-06914ffbaef3







The keycap icon sits in the bottom-right corner of the taskbar, with the volume and wifi icons and it pins itself there a few seconds after the first launch (if Windows keeps it behind the `^` arrow, drag it out once). Click the icon to mute or unmute. Right-click it (two-finger tap on a touchpad) to open the menu with volume, start with Windows, and exit.

**Install:** [Download CreamyKeys.exe here](https://github.com/mofe-stack/CreamyKeys/releases/latest/download/CreamyKeys.exe) and double-click it. That's the whole install, the sounds are inside the exe. If you open it straight from the browser or run it from Downloads, it moves itself to a permanent home (`%LOCALAPPDATA%\CreamyKeys`) and runs from there, so "Start with Windows" keeps working even after your Downloads folder gets cleaned out. Windows may warn because it's unsigned and it listens for keypresses to play the sounds. Nothing is stored or sent anywhere. Or build it yourself by running `build.cmd` (uses the compiler that ships with Windows).

**Your own sounds:** on first run the app unpacks its samples into a `sounds` folder next to the exe. Replace them with any short wavs using the same six names, then exit and reopen the app.

MIT license. The sound samples are Opera's, from their public [GX mod template](https://github.com/opera-gaming/gxmods).
