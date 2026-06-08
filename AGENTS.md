# DICOM Print Server — Project Plan (AI-Readable)

> **للذكاء الاصطناعي:** اقرأ هذا الملف أولاً قبل أي مهمة. يحتوي على حالة المشروع الحالية، ما تم إنجازه، وما تبقّى.

---

## حالة المشروع الحالية

| المهمة | الحالة | الملفات الرئيسية |
|--------|--------|-----------------|
| M1 — Multi-Port Infrastructure | ✅ مكتملة + مبنية | `Services/MultiPortManager.cs` |
| M2-A — JPG Export | ✅ مكتملة + مبنية | `Services/JpgExporter.cs` |
| M2-B — Gamma/Contrast Adjustment | ✅ مكتملة + مبنية | `Services/ImageProcessor.cs` |
| M2-C — Header/Footer Annotations | ✅ مكتملة + مبنية | `Services/AnnotationRenderer.cs` |
| M2-D — Calibration Mode | ✅ مكتملة + مبنية | `Services/CalibrationService.cs` |
| M3 — PDF Generation | ✅ مكتملة + مبنية | `Services/PdfExporter.cs` |
| M4 — Windows Printer Selection | ✅ مكتملة + مبنية | `Services/PrintJob.cs` |
| M5 — Monitoring/Reports | ✅ مكتملة + مبنية | `Services/PrintMonitor.cs` |
| M6-L — License Manager | ✅ مكتملة + مبنية | `Services/LicenseManager.cs` |
| M6-G — License Generator Tool | ✅ مكتملة + مبنية | `src/DicomPrintAdminTool/Program.cs` |
| M6-A — Admin License Tool | ✅ مكتملة + مبنية | `src/DicomPrintAdminTool/Program.cs` |
| M6-S — Security/AntiTamper | ✅ مكتملة + مبنية | `Services/SecurityGuard.cs` |
| M6-T — Silent Trial Timer | ✅ مكتملة + مبنية | `Services/TrialManager.cs` |
| M7 — WhatsApp Integration | ✅ مكتملة + مبنية | `Services/WhatsAppNotifier.cs` |

**جميع المهام مكتملة ومبنية بنجاح — 0 أخطاء / 0 تحذيرات**

---

## هيكل المشروع النهائي

```
/
├── AGENTS.md                              ← هذا الملف
├── docs/
│   └── DICOM_PRINT_SERVER_PLAN.md
├── library/                               ← fo-dicom (لا تعدّل)
├── samples/Core/Print SCP/               ← مرجع فقط (لا تعدّل)
└── src/
    ├── DicomPrintServer/                  ← الخادم الرئيسي
    │   ├── DicomPrintServer.csproj        ← net8.0-windows / win-x64
    │   ├── Program.cs                     ← DI setup + DicomSetupBuilder
    │   ├── appsettings.json               ← كل الإعدادات
    │   ├── Configuration/
    │   │   └── PrintServerConfig.cs       ✅ PrintServerConfig, ListenerConfig, etc.
    │   ├── Services/
    │   │   ├── MultiPortManager.cs        ✅ M1 — IDicomServerFactory injection
    │   │   ├── PrintConfigProvider.cs     ✅ AET → Config mapping
    │   │   ├── PrintService.cs            ✅ M1 — N-CREATE/SET/ACTION/GET/DELETE
    │   │   ├── PrintJob.cs               ✅ M1+M4 — thread-safe + Monitor
    │   │   ├── Printer.cs                 ✅
    │   │   ├── JpgExporter.cs             ✅ M2-A+B+C+D pipeline
    │   │   ├── ImageProcessor.cs          ✅ M2-B — Gamma/Contrast/WL/Invert
    │   │   ├── AnnotationRenderer.cs      ✅ M2-C — Header/Footer/Watermark
    │   │   ├── CalibrationService.cs      ✅ M2-D — TG18/GreyRamp/SMPTE/etc.
    │   │   ├── PdfExporter.cs             ✅ M3 — PdfSharpCore
    │   │   ├── PrintMonitor.cs            ✅ M5 — thread-safe counters
    │   │   ├── LicenseManager.cs          ✅ M6-L — RSA-2048 verify
    │   │   ├── SecurityGuard.cs           ✅ M6-S — anti-tamper
    │   │   ├── TrialManager.cs            ✅ M6-T — DPAPI/AES-GCM + Registry
    │   │   └── WhatsAppNotifier.cs        ✅ M7 — CallMeBot/Twilio/Meta
    │   └── Workers/
    │       └── PrintServerWorker.cs       ✅ License check + hourly reports
    └── DicomPrintAdminTool/               ← أداة الموزع
        ├── DicomPrintAdminTool.csproj     ← net8.0-windows
        └── Program.cs                     ✅ M6-A/G — keygen/issue/verify/info
```

---

## قواعد يجب على الذكاء الاصطناعي اتباعها

1. **لا تعدّل** مجلد `library/` أو مجلد `samples/`
2. **كل كود جديد** يذهب إلى `src/DicomPrintServer/`
3. **بعد كل تعديل:** ابنِ للتحقق `dotnet build --no-restore`
4. **ملاحظة بناء:** استخدم دائماً `--no-restore` إذا لم تضف حزمة جديدة
5. **DicomSetupBuilder.UseServiceProvider(host.Services)** تُستدعى بعد `host.Build()` — ضروري لـ IDicomServerFactory

---

## ملاحظات تقنية مهمة (اقرأها قبل أي تعديل)

- **`Rect<T>` في NuGet 5.2.5 ≠ المكتبة المحلية** → استخدم `BoxRect` المحلية في `JpgExporter`
- **`IDicomServer.IsListening`** (وليس IsDisposed) — الخاصية الصحيحة
- **`FontFamily` هيكل struct** → لا تستخدم `??` عليه، ابحث بـ `foreach`
- **`ProtectedData`** تحتاج NuGet: `System.Security.Cryptography.ProtectedData 8.0.0`
- **`PdfSharpCore`** الإصدار `1.3.65` — يعمل cross-platform
- **`System.Drawing.Common 8.0.10`** مطلوب لـ `PrintDocument` في Windows
- **`appsettings.json`** لا تضف `<Content>` يدوياً في .csproj — SDK يضيفها تلقائياً

---

## المكتبات المستخدمة

| المكتبة | الإصدار | الغرض |
|---------|---------|-------|
| fo-dicom | 5.2.5 | DICOM protocol |
| fo-dicom.Imaging.ImageSharp | 5.2.5 | DicomImage rendering |
| SixLabors.ImageSharp | 3.1.11 | Image processing |
| SixLabors.ImageSharp.Drawing | 2.1.4 | Text/annotations |
| PdfSharpCore | 1.3.65 | PDF generation |
| System.Drawing.Common | 8.0.10 | Windows printing |
| System.Security.Cryptography.ProtectedData | 8.0.0 | DPAPI encryption |
| Microsoft.Extensions.Hosting.WindowsServices | 8.0.1 | Windows Service |
| fo-dicom.Codecs | 5.16.4 | Extra DICOM codecs |

---

## ما يجب فعله لإطلاق إنتاجي

1. **توليد مفاتيح RSA حقيقية:**
   ```
   DicomPrintAdminTool.exe keygen
   ```
   ثم ضع محتوى `public_key.pem` في `LicenseManager.PublicKeyPem`

2. **ربط WhatsApp:** عيّن بيانات API في `appsettings.json → WhatsApp`

3. **بناء الإصدار النهائي:**
   ```
   dotnet publish -c Release -r win-x64 --self-contained
   ```

4. **تثبيت كـ Windows Service:**
   ```
   sc create DicomPrintServer binPath= "C:\DicomPrintServer.exe"
   sc start DicomPrintServer
   ```

---

*آخر تحديث: جميع المهام مكتملة ومبنية — 0 أخطاء / 0 تحذيرات*
