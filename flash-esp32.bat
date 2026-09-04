@echo off
setlocal
rem ESP32 烧录工具启动器：调用 flash-esp32.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0flash-esp32.ps1" %*
endlocal
