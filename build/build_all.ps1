# ================================================================
# DICOM Print Server — سكريبت PowerShell للبناء الكامل
# شغّل بصلاحيات Administrator:
#   powershell -ExecutionPolicy Bypass -File build\build_all.ps1
# ================================================================

$ErrorActionPreference = "Stop"
$Root   = Split-Path $PSScriptRoot -Parent
$Out    = "$Root\build\output"
$ISCC   = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"

function Write-Step($n, $msg) {
    Write-Host ""
    Write-Host "[$n] $msg" -ForegroundColor Cyan
}

function Invoke-Publish($proj, $outDir, [string[]]$extra = @()) {
    $args = @(
        "publish", $proj,
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-o", $outDir,
        "--nologo", "-v", "minimal"
    ) + $extra
    dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $proj" }
}

# ── تنظيف ──────────────────────────────────────────────────────
Write-Host "================================================================" -ForegroundColor Yellow
Write-Host "   DICOM Print Server — بناء النسختين (Trial + Full)"            -ForegroundColor Yellow
Write-Host "================================================================" -ForegroundColor Yellow

if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
$null = New-Item -ItemType Directory "$Out\trial\server", "$Out\trial\admin",
                                     "$Out\full\server",  "$Out\full\admin",
                                     "$Out\installers" -Force

# ── بناء الملفات ───────────────────────────────────────────────

Write-Step "1/4" "خادم الطباعة — نسخة تجريبية (8 ساعات)"
Invoke-Publish "$Root\src\DicomPrintServer\DicomPrintServer.csproj" `
               "$Out\trial\server" `
               @("-p:DefineConstants=TRIAL_BUILD")

Write-Step "2/4" "خادم الطباعة — نسخة كاملة"
Invoke-Publish "$Root\src\DicomPrintServer\DicomPrintServer.csproj" `
               "$Out\full\server"

Write-Step "3/4" "أداة الإدارة — نسخة تجريبية (8 ساعات)"
Invoke-Publish "$Root\src\DicomPrintAdminGui\DicomPrintAdminGui.csproj" `
               "$Out\trial\admin" `
               @("-p:DefineConstants=TRIAL_BUILD")

Write-Step "4/4" "أداة الإدارة — نسخة كاملة"
Invoke-Publish "$Root\src\DicomPrintAdminGui\DicomPrintAdminGui.csproj" `
               "$Out\full\admin"

Write-Host ""
Write-Host "✅ تم بناء جميع الملفات بنجاح!" -ForegroundColor Green

# ── بناء المثبّتات ─────────────────────────────────────────────
Write-Host ""
Write-Host "================================================================" -ForegroundColor Yellow
Write-Host "   بناء ملفات المثبّت (Inno Setup)"                              -ForegroundColor Yellow
Write-Host "================================================================" -ForegroundColor Yellow

if (-not (Test-Path $ISCC)) {
    Write-Host ""
    Write-Host "⚠️  Inno Setup غير موجود في: $ISCC" -ForegroundColor Yellow
    Write-Host "   حمّله من: https://jrsoftware.org/isinfo.php" -ForegroundColor Yellow
    Write-Host "   ثم شغّل: .\build\build_installers_only.bat" -ForegroundColor Yellow
}
else {
    $scripts = @(
        "setup_server_trial.iss",
        "setup_server_full.iss",
        "setup_admin_trial.iss",
        "setup_admin_full.iss"
    )
    foreach ($iss in $scripts) {
        Write-Host "   بناء: $iss" -ForegroundColor Cyan
        & $ISCC "$Root\build\$iss"
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed for $iss" }
    }
    Write-Host ""
    Write-Host "✅ جميع المثبّتات جاهزة!" -ForegroundColor Green
}

# ── ملخص نهائي ─────────────────────────────────────────────────
Write-Host ""
Write-Host "================================================================" -ForegroundColor Yellow
Write-Host "   الملفات النهائية في: build\output\installers\"                 -ForegroundColor Yellow
Write-Host "================================================================" -ForegroundColor Yellow
Write-Host ""
Write-Host "   🔴 تجريبية (ترسلها قبل الأتعاب — تنتهي بعد 8 ساعات):" -ForegroundColor Red
Write-Host "      DCMP_Server_Trial_Setup.exe"
Write-Host "      DCMP_Admin_Trial_Setup.exe"
Write-Host ""
Write-Host "   🟢 كاملة (ترسلها بعد استلام الأتعاب):"  -ForegroundColor Green
Write-Host "      DCMP_Server_Full_Setup.exe"
Write-Host "      DCMP_Admin_Full_Setup.exe"
Write-Host ""
