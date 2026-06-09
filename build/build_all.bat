@echo off
setlocal EnableDelayedExpansion

echo ================================================
echo   DICOM Print Server - Build Trial + Full
echo ================================================
echo.

set "ROOT=%~dp0.."
set "OUT=%ROOT%\build\output"
set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"

REM --- Stop any running instances that would lock the EXE ---
echo Stopping services and processes...
sc stop "DicomPrintServer"      >nul 2>&1
sc stop "DicomPrintServerTrial" >nul 2>&1
sc stop "DICOM Print Server"    >nul 2>&1
timeout /t 3 /nobreak >nul
taskkill /f /t /im DicomPrintServer.exe  >nul 2>&1
taskkill /f /t /im DicomPrintAdminGui.exe >nul 2>&1
wmic process where "name='DicomPrintServer.exe'"  delete >nul 2>&1
wmic process where "name='DicomPrintAdminGui.exe'" delete >nul 2>&1
timeout /t 3 /nobreak >nul
echo Done stopping.

REM --- Clean output folder ---
if exist "%OUT%" rd /s /q "%OUT%"
mkdir "%OUT%\trial\server"
mkdir "%OUT%\trial\admin"
mkdir "%OUT%\full\server"
mkdir "%OUT%\full\admin"
mkdir "%OUT%\installers"

echo [1/4] Building Server - TRIAL (8 hours)...
dotnet publish "%ROOT%\src\DicomPrintServer\DicomPrintServer.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DefineConstants=TRIAL_BUILD -o "%OUT%\trial\server" --nologo -v minimal
if %errorlevel% neq 0 goto :error
echo     OK

echo [2/4] Building Server - FULL...
dotnet publish "%ROOT%\src\DicomPrintServer\DicomPrintServer.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%OUT%\full\server" --nologo -v minimal
if %errorlevel% neq 0 goto :error
echo     OK

echo [3/4] Building Admin GUI - TRIAL (8 hours)...
dotnet publish "%ROOT%\src\DicomPrintAdminGui\DicomPrintAdminGui.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DefineConstants=TRIAL_BUILD -o "%OUT%\trial\admin" --nologo -v minimal
if %errorlevel% neq 0 goto :error
echo     OK

echo [4/4] Building Admin GUI - FULL...
dotnet publish "%ROOT%\src\DicomPrintAdminGui\DicomPrintAdminGui.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%OUT%\full\admin" --nologo -v minimal
if %errorlevel% neq 0 goto :error
echo     OK

echo.
echo ================================================
echo   Building Installers (Inno Setup)
echo ================================================
echo.

if not exist "%ISCC%" (
    echo WARNING: Inno Setup not found at: %ISCC%
    echo.
    echo Download Inno Setup FREE from: https://jrsoftware.org/isinfo.php
    echo Then run: build_installers_only.bat
    echo.
    echo EXE files are ready in: build\output\trial\  and  build\output\full\
    goto :done
)

echo Building: DCMP_Server_Trial_Setup.exe ...
"%ISCC%" "%ROOT%\build\setup_server_trial.iss"
if %errorlevel% neq 0 goto :error

echo Building: DCMP_Server_Full_Setup.exe ...
"%ISCC%" "%ROOT%\build\setup_server_full.iss"
if %errorlevel% neq 0 goto :error

echo Building: DCMP_Admin_Trial_Setup.exe ...
"%ISCC%" "%ROOT%\build\setup_admin_trial.iss"
if %errorlevel% neq 0 goto :error

echo Building: DCMP_Admin_Full_Setup.exe ...
"%ISCC%" "%ROOT%\build\setup_admin_full.iss"
if %errorlevel% neq 0 goto :error

:done
echo.
echo ================================================
echo   DONE! Output: build\output\installers\
echo ================================================
echo.
echo   TRIAL (send BEFORE payment - expires 8 hours):
echo     DCMP_Server_Trial_Setup.exe
echo     DCMP_Admin_Trial_Setup.exe
echo.
echo   FULL (send AFTER payment):
echo     DCMP_Server_Full_Setup.exe
echo     DCMP_Admin_Full_Setup.exe
echo.
pause
exit /b 0

:error
echo.
echo *** BUILD FAILED ***
pause
exit /b 1
