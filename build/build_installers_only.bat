@echo off
setlocal

echo ================================================
echo   Build Installers Only (Inno Setup)
echo   Run build_all.bat first if EXE not built yet
echo ================================================

set "ROOT=%~dp0.."
set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"

if not exist "%ISCC%" (
    echo ERROR: Inno Setup not found at: %ISCC%
    echo.
    echo Download FREE from: https://jrsoftware.org/isinfo.php
    pause
    exit /b 1
)

echo Building: DCMP_Server_Trial_Setup.exe ...
"%ISCC%" "%ROOT%\build\setup_server_trial.iss"
if %errorlevel% neq 0 goto :err

echo Building: DCMP_Server_Full_Setup.exe ...
"%ISCC%" "%ROOT%\build\setup_server_full.iss"
if %errorlevel% neq 0 goto :err

echo Building: DCMP_Admin_Trial_Setup.exe ...
"%ISCC%" "%ROOT%\build\setup_admin_trial.iss"
if %errorlevel% neq 0 goto :err

echo Building: DCMP_Admin_Full_Setup.exe ...
"%ISCC%" "%ROOT%\build\setup_admin_full.iss"
if %errorlevel% neq 0 goto :err

echo.
echo All installers ready in: build\output\installers\
pause
exit /b 0

:err
echo BUILD FAILED
pause
exit /b 1
