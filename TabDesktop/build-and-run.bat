@echo off
cd /d D:\repos\tabdesktop
taskkill /IM TabDesktop.exe /F >nul 2>&1
call yarn build || (pause & exit /b 1)
start "" "D:\repos\tabdesktop\TabDesktop\bin\Debug\net10.0-windows\TabDesktop.exe"
