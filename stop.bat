@echo off
rem Stops the relay hub (the games are closed normally from their own windows).
powershell -Command "Get-NetTCPConnection -LocalPort 7777 -State Listen -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.OwningProcess -Force }; Write-Host 'Hub stopped.'"
pause
