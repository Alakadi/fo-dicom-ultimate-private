@echo off
setlocal EnableDelayedExpansion

echo ================================================
echo   DICOM Print Server - Build All (6 outputs)
echo ================================================
echo.

set "ROOT=%~dp0.."
set "OUT=%ROOT%\build\output"
set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if exist "C:\Users\DELL\AppData\Local\Programs\Inno Setup 6\ISCC.exe" (
    set "ISCC=C:\Users\DELL\AppData\Local\Programs\Inno Setup 6\ISCC.exe"
)

REM --- Stop services and processes ---
echo Stopping services and processes...
sc stop "DicomPrintServer"      >nul 2>&1
sc stop "DicomPrintServerTrial" >nul 2>&1
sc stop "DICOM Print Server"    >nul 2>&1
timeout /t 3 /nobreak >nul
taskkill /f /t /im DicomPrintServer.exe   >nul 2>&1
taskkill /f /t /im DicomPrintAdminGui.exe >nul 2>&1
taskkill /f /t /im DicomPrintClientGui.exe >nul 2>&1
wmic process where "name='DicomPrintServer.exe'"   delete >nul 2>&1
wmic process where "name='DicomPrintAdminGui.exe'" delete >nul 2>&1
wmic process where "name='DicomPrintClientGui.exe'" delete >nul 2>&1
timeout /t 3 /nobreak >nul
echo Done stopping.

REM --- Clean output folder ---
if exist "%OUT%" rd /s /q "%OUT%"
mkdir "%OUT%\trial\server"
mkdir "%OUT%\trial\admin"
mkdir "%OUT%\trial\client"
mkdir "%OUT%\full\server"
mkdir "%OUT%\full\admin"
mkdir "%OUT%\full\client"
mkdir "%OUT%\installers"

echo [1/6] Building Server - TRIAL (8 hours)...
dotnet publish "%ROOT%\src\DicomPrintServer\DicomPrintServer.csproj" -c Release -r win-x64 --self-contained true -p:DefineConstants=TRIAL_BUILD -o "%OUT%\trial\server" --nologo -v minimal
if %errorlevel% neq 0 goto :error
echo     OK

echo [2/6] Building Server - FULL...
dotnet publish "%ROOT%\src\DicomPrintServer\DicomPrintServer.csproj" -c Release -r win-x64 --self-contained true -o "%OUT%\full\server" --nologo -v minimal
if %errorlevel% neq 0 goto :error
echo     OK

echo Restoring Client GUI packages...
dotnet restore "%ROOT%\src\DicomPrintClientGui\DicomPrintClientGui.csproj" --force --nologo
echo.

echo [3/6] Building Client GUI - TRIAL...
dotnet publish "%ROOT%\src\DicomPrintClientGui\DicomPrintClientGui.csproj" -c Release -r win-x64 --self-contained true -p:DefineConstants=TRIAL_BUILD -o "%OUT%\trial\client" --nologo -v minimal
if %errorlevel% neq 0 goto :error
echo     OK

echo [4/6] Building Client GUI - FULL...
dotnet publish "%ROOT%\src\DicomPrintClientGui\DicomPrintClientGui.csproj" -c Release -r win-x64 --self-contained true -o "%OUT%\full\client" --nologo -v minimal
if %errorlevel% neq 0 goto :error
echo     OK

echo [5/6] Building Admin GUI - FULL (for reseller only)...
dotnet publish "%ROOT%\src\DicomPrintAdminGui\DicomPrintAdminGui.csproj" -c Release -r win-x64 --self-contained true -o "%OUT%\full\admin" --nologo -v minimal
if %errorlevel% neq 0 goto :error
echo     OK

echo [6/6] Building Admin GUI - TRIAL (for reseller only)...
dotnet publish "%ROOT%\src\DicomPrintAdminGui\DicomPrintAdminGui.csproj" -c Release -r win-x64 --self-contained true -p:DefineConstants=TRIAL_BUILD -o "%OUT%\trial\admin" --nologo -v minimal
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

REM --- Unified installers (server + client in one setup) ---
echo Building: DicomPrintServer_Trial_Setup.exe (موحد - خدمة + لوحة تحكم) ...
"%ISCC%" "%ROOT%\build\setup_dicomprint_trial.iss"
if %errorlevel% neq 0 goto :error

echo Building: DicomPrintServer_Full_Setup.exe (موحد - خدمة + لوحة تحكم) ...
"%ISCC%" "%ROOT%\build\setup_dicomprint_full.iss"
if %errorlevel% neq 0 goto :error

REM --- Admin tool for reseller (separate, keep secret) ---
echo Building: DCMP_Admin_Full_Setup.exe (للموزع فقط - سري) ...
"%ISCC%" "%ROOT%\build\setup_admin_full.iss"
if %errorlevel% neq 0 goto :error

:done
echo.
echo ================================================
echo   DONE! Output: build\output\installers\
echo ================================================
echo.
echo   للعميل (مثبت واحد يشمل الخدمة + لوحة التحكم):
echo     DicomPrintServer_Trial_Setup.exe   - نسخة تجريبية 8 ساعات
echo     DicomPrintServer_Full_Setup.exe    - نسخة كاملة
echo.
echo   للموزع فقط (سري - لا ترسله للعميل):
echo     DCMP_Admin_Full_Setup.exe          - اداة توليد الرخص
echo.
pause
exit /b 0

:error
echo.
echo *** BUILD FAILED ***
pause
exit /b 1

