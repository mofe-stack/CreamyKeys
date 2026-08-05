@echo off
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /win32icon:icon.ico ^
 /resource:sounds\letter_1.wav,sounds.letter_1.wav ^
 /resource:sounds\letter_2.wav,sounds.letter_2.wav ^
 /resource:sounds\letter_3.wav,sounds.letter_3.wav ^
 /resource:sounds\space.wav,sounds.space.wav ^
 /resource:sounds\enter.wav,sounds.enter.wav ^
 /resource:sounds\backspace.wav,sounds.backspace.wav ^
 /resource:icon.ico,icon.ico ^
 /resource:icon_muted.ico,icon_muted.ico ^
 /out:CreamyKeys.exe CreamyKeys.cs
