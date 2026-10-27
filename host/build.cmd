@echo off
cd /d %~dp0
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ /out:..\..\Italophone.exe /win32icon:italophone.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll Italophone.cs
