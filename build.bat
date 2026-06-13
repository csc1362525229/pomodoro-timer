@echo off
cd /d "%~dp0"
C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /target:winexe /win32manifest:app.manifest /out:Pomodoro.exe /r:Microsoft.Web.WebView2.Core.dll /r:Microsoft.Web.WebView2.WinForms.dll Main.cs
if %ERRORLEVEL% EQU 0 (
    echo [OK] Pomodoro.exe 编译成功
) else (
    echo [FAIL] 编译失败，错误码: %ERRORLEVEL%
)
pause
