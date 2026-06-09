# دليل بناء المثبّتات — DICOM Print Server

## الناتج النهائي

بعد تشغيل سكريبت البناء ستحصل على **4 ملفات مثبّت**:

| الملف | الوصف |
|-------|-------|
| `DCMP_Server_Trial_Setup.exe` | خادم الطباعة — تجريبية (8 ساعات، تدمير ذاتي) |
| `DCMP_Admin_Trial_Setup.exe`  | أداة الإدارة — تجريبية (8 ساعات، تدمير ذاتي) |
| `DCMP_Server_Full_Setup.exe`  | خادم الطباعة — كاملة (ترسلها بعد الأتعاب)   |
| `DCMP_Admin_Full_Setup.exe`   | أداة الإدارة — كاملة (ترسلها بعد الأتعاب)   |

---

## المتطلبات (على جهاز Windows)

1. **.NET 8 SDK** — من [dotnet.microsoft.com](https://dotnet.microsoft.com/download)
2. **Inno Setup 6** — مجاني من [jrsoftware.org/isinfo.php](https://jrsoftware.org/isinfo.php)

---

## خطوات البناء

### الطريقة السريعة (كل شيء في خطوة واحدة)
```
انقر نقراً مزدوجاً على: build\build_all.bat
```

### إذا أردت البناء فقط بدون المثبّتات أولاً
```batch
cd build
build_all.bat
```

### إذا كانت الملفات المبنية موجودة وتريد فقط إنشاء المثبّتات
```
انقر نقراً مزدوجاً على: build\build_installers_only.bat
```

---

## هيكل الإخراج

```
build\output\
├── trial\
│   ├── server\          ← ملفات EXE التجريبية للخادم
│   └── admin\           ← ملفات EXE التجريبية للأداة
├── full\
│   ├── server\          ← ملفات EXE الكاملة للخادم
│   └── admin\           ← ملفات EXE الكاملة للأداة
└── installers\          ← ← ← الملفات النهائية هنا
    ├── DCMP_Server_Trial_Setup.exe
    ├── DCMP_Admin_Trial_Setup.exe
    ├── DCMP_Server_Full_Setup.exe
    └── DCMP_Admin_Full_Setup.exe
```

---

## كيفية التعامل مع النسختين

### النسخة التجريبية (Trial)
- ترسلها للعميل **قبل** استلام الأتعاب
- **لا تخبره** بأن فيها موقت
- البرنامج يعمل بشكل طبيعي تماماً لمدة **8 ساعات**
- بعد 8 ساعات: يغلق نفسه صامتاً ويتلف ملف EXE
- لا طريقة لإعادة تشغيله بعد انتهاء المدة
- يكشف تغيير تاريخ النظام ويتلف نفسه فوراً

### النسخة الكاملة (Full)
- ترسلها **بعد** استلام الأتعاب
- تحتاج ملف `license.key` للعمل
- أنشئ ملف الترخيص عبر: `DicomPrintAdminTool` بالـ Private Key الخاص بك
- ضع ملف `license.key` في نفس مجلد `DicomPrintServer.exe`

---

## ملاحظات مهمة

> **الخصوصية:** الـ Private Key (`private_key.pem`) يجب أن يبقى عندك فقط. لا ترسله لأحد.

> **البناء:** يتم بناء نسخة Trial باستخدام `#define TRIAL_BUILD` — تفعّل `AdminTrialGuard` في أداة الإدارة و`TrialManager` في الخادم.

> **الأمان:** كل نسخة تجريبية مرتبطة بالجهاز الذي تعمل عليه (Machine ID). لا تعمل على جهاز آخر بعد انتهاء المدة.
