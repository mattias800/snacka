@echo off
rem Starts the Snacka server from this folder. Everything it stores (database, uploads, keys)
rem goes in .\data; back up that folder to back up the server.
cd /d "%~dp0"
if "%SNACKA_DATA%"=="" set "SNACKA_DATA=%~dp0data"
if "%SNACKA_URLS%"=="" set "SNACKA_URLS=http://0.0.0.0:5117"
Snacka.Server.exe --urls "%SNACKA_URLS%" %*
pause
