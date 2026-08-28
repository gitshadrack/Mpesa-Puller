@echo off
setlocal

net session >nul 2>&1
if not "%errorlevel%"=="0" (
    echo Requesting Administrator permission...
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%ComSpec%' -ArgumentList '/c ""%~f0""' -Verb RunAs"
    exit /b 0
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-MPesa.ps1"
if not "%errorlevel%"=="0" (
    echo.
    echo Installation failed. Copy the complete MPesa folder, including the publish folder, before trying again.
    pause
    exit /b 1
)

echo.
echo Installation completed successfully.
pause
