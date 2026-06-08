# سجل التغييرات — DICOM Print Server
**المشروع:** DICOM Print Server (fo-dicom · .NET 8 · C# · Windows Worker Service)
**تاريخ آخر تحديث:** 2025

---

## الإصدار 1.0.0 — الإصدار الأول الكامل

---

### M1 — الأساسية (DICOM Print SCP)

| المهمة | الملف | الوصف |
|--------|-------|-------|
| M1-A | `PrintService.cs` | معالج Print SCP كامل (N-CREATE / N-SET / N-ACTION / N-DELETE / N-GET / C-ECHO) |
| M1-B | `PrintJob.cs` | تنفيذ مهمة الطباعة: Windows GDI + `PrintDocument` |
| M1-C | `PrintServerWorker.cs` | Worker Service يُشغّل خادم DICOM |
| M1-D | `PrintConfigProvider.cs` | إعدادات ديناميكية لكل AET من `appsettings.json` |

---

### M2 — جودة الصورة والمعايرة

| المهمة | الملف | الوصف |
|--------|-------|-------|
| M2-A | `JpgExporter.cs` | تصدير صور JPG بتحكم Gamma/Contrast/Brightness عبر ImageSharp |
| M2-B | `PrintJob.cs` | تطبيق قيم Gamma/Contrast/Brightness عند الطباعة (LUT 256 entry) |
| M2-C | `CalibrationService.cs` | أنماط معايرة TG-18QC / GreyRamp / SMPTE / CheckerBoard / CrossHatch |
| M2-D | `CalibrationService.cs` → `CalibrationGridPrinter` | شبكة NxM من الصور بقيم Gamma×Contrast مختلفة لاختيار الإعداد الأمثل |

---

### M3 — المخرجات

| المهمة | الملف | الوصف |
|--------|-------|-------|
| M3-A | `JpgExporter.cs` | حفظ صور JPG بجودة قابلة للضبط |
| M3-B | `PdfExporter.cs` | تحويل الصور إلى PDF (صفحة واحدة أو متعددة) |
| M3-C | `PrintJob.cs` | دعم Grayscale وColor وReverse في الطباعة |
| M3-D | `WatermarkService.cs` | علامة مائية نصية/صورة قابلة للتكوين (موضع/دوران/شفافية) |
| M3-E | `PdfSessionManager.cs` | تجميع FilmBoxes لنفس المريض في PDF واحد مع timeout تلقائي |

---

### M4 — اكتشاف الطابعات

| المهمة | الملف | الوصف |
|--------|-------|-------|
| M4-A | `PrinterDiscovery.cs` | قائمة الطابعات المثبتة على Windows + الطابعة الافتراضية |
| M4-B | `PrinterDiscovery.cs` | فحص حالة كل طابعة (Online/Offline/Error) |
| M4-C | `AdminApiWorker.cs` `/api/printers` | endpoint يعرض الطابعات عبر REST |

---

### M5 — المراقبة والإحصاءات

| المهمة | الملف | الوصف |
|--------|-------|-------|
| M5-A | `PrintRepository.cs` | SQLite persistence (PrintOperations + DailyCounters) بـ WAL mode |
| M5-B | `PrintMonitor.cs` | إحصاءات في الذاكرة (thread-safe بـ ConcurrentDictionary + Interlocked) |
| M5-C | `AdminApiWorker.cs` | REST API كامل على المنفذ 9000 مع Basic Auth |

**Endpoints REST:**

| الـ endpoint | الوصف |
|------------|-------|
| `GET /` | لوحة تحكم HTML تفاعلية |
| `GET /api/stats` | إحصاءات JSON من الذاكرة |
| `GET /api/jobs` | آخر N مهمة طباعة (JSON) |
| `GET /api/jobs/csv` | تصدير CSV من الذاكرة |
| `GET /api/listeners` | قائمة AETs المُهيَّأة |
| `GET /api/printers` | الطابعات المثبتة |
| `GET /api/db/stats` | إجماليات SQLite |
| `GET /api/db/daily?days=30` | إحصاءات يومية |
| `GET /api/db/jobs/csv` | تصدير CSV من SQLite |

---

### M6 — الترخيص والتجربة

| المهمة | الملف | الوصف |
|--------|-------|-------|
| M6-A | `DicomPrintAdminGui/` | Admin GUI (WinForms) للـ Reseller لإنشاء وإدارة المفاتيح |
| M6-B | `LicenseManager.cs` | التحقق من الترخيص RSA-2048 عند الإقلاع |
| M6-C | `TrialManager.cs` | إدارة النسخة التجريبية (عداد مضاعف Registry+ملف، NTP، إيقاف صامت) |
| M6-D | `DicomPrintAdminTool/` | Admin CLI لتوليد مفاتيح RSA وإصدار التراخيص |
| M6-G | `LicenseGenerator.cs` | توليد مفاتيح بصيغة DCMP-XXXX-XXXX |

---

### M7 — الإشعارات

| المهمة | الملف | الوصف |
|--------|-------|-------|
| M7-A | `WhatsAppNotifier.cs` | إشعارات WhatsApp عبر CallMeBot API بعد كل طباعة ناجحة |

---

### M8 — متعدد المنافذ

| المهمة | الملف | الوصف |
|--------|-------|-------|
| M8-A | `MultiPortManager.cs` | تشغيل منافذ DICOM متعددة في نفس الوقت |
| M8-B | `PrintConfigProvider.cs` | تحديد إعدادات الطابعة بحسب CalledAE |

---

### Admin GUI — التفاصيل التقنية

**المشروع:** `src/DicomPrintAdminGui/DicomPrintAdminGui.csproj`
**الإطار:** .NET 8 / WinForms / SelfContained / win-x64

**الشاشات:**

| الشاشة | الملف | الوصف |
|--------|-------|-------|
| تسجيل الدخول | `UnlockForm.cs` | مفتاح رئيسي (DPAPI + Registry) — أول تشغيل: ضبط مفتاح |
| لوحة التحكم | `MainForm.cs` | بطاقات الإحصائيات + جدول آخر المفاتيح |
| إنشاء مفتاح | `CreateKeyForm.cs` | جميع الحقول (عميل/نوع/عمليات/انتهاء/ميزات/HwLock) |
| إدارة المفاتيح | `ManageKeysForm.cs` | DataGridView + إلغاء + تمديد + تصدير CSV |
| الإعدادات | `SettingsForm.cs` | تغيير المفتاح الرئيسي |

**نظام الترخيص:**

- توليد: RSA-2048 (SHA-256 PKCS1) — payload JSON موقّع
- الصيغة: `DCMP-XXXXX-XXXXX-XXXXX-...`
- المخزن: `%AppData%\DCMPrint\issued_keys.json`
- الحماية: DPAPI لمفتاح المسؤول في Registry

---

### التغييرات على Program.cs

```csharp
services.AddSingleton<PdfSessionManager>();
services.AddSingleton<PrintRepository>(sp => { var repo = new PrintRepository(...); repo.Initialize(); return repo; });
services.AddHostedService<AdminApiWorker>();
```

---

### التغييرات على appsettings.json (مثال)

```json
{
  "AdminApi": { "Enabled": true, "Port": 9000, "Username": "admin", "Password": "secret" },
  "WhatsApp": { "Enabled": true, "ApiKey": "...", "DefaultRecipientPhone": "+966..." }
}
```

---

### الاعتماديات المُضافة

| الحزمة | الإصدار | الغرض |
|--------|---------|-------|
| `Microsoft.Data.Sqlite` | 8.0.8 | SQLite persistence |
| `System.Security.Cryptography.ProtectedData` | 8.0.0 | DPAPI |
| `SixLabors.ImageSharp` | موجود | معالجة الصور |
| `SixLabors.ImageSharp.Drawing` | موجود | رسم النصوص والأشكال |

---

*تم إنشاء هذا الملف تلقائياً من وصف التنفيذ. يُعدَّل مع كل إصدار.*
