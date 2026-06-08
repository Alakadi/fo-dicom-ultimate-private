# خطة تطوير DICOM Print Server — الوثيقة الشاملة

**تاريخ الإنشاء:** يونيو 2026  
**المشروع:** خادم طباعة DICOM مبني على مكتبة `fo-dicom` (.NET / C#)  
**الإصدار:** 1.0

---

## فهرس المحتويات

1. [مبادئ عامة وآلية العمل](#1-مبادئ-عامة)
2. [هيكل النسختين: التجريبية والكاملة](#2-هيكل-النسختين)
3. [نظام الترخيص والمفاتيح — التصميم الكامل](#3-نظام-الترخيص)
4. [منهجية الأمان — طبقات الحماية](#4-منهجية-الأمان)
5. [مهام التطوير التفصيلية](#5-مهام-التطوير)
6. [جدول الأولويات والجهد](#6-جدول-الأولويات)

---

## 1. مبادئ عامة وآلية العمل

### 1.1 قاعدة "الفحص قبل البناء"
**قبل تنفيذ أي مهمة** يجب المطور أن:
1. يراجع الكود الموجود في المسارات المذكورة في كل مهمة
2. يحدد ما هو مكتمل، ما يحتاج ربط، وما يحتاج بناء من الصفر
3. يوثّق نتيجة الفحص في تعليق في أعلى ملف المهمة

### 1.2 دورة الإصدار بعد كل مهمة
بعد إكمال **كل مهمة** أو مجموعة مترابطة من المهام:

```
[ تطوير المهمة ] → [ اختبار داخلي ] → [ بناء النسختين ] → [ توزيع للعميل للفحص ]
                                              ↓
                                    نسخة تجريبية (Canary Build)
                                    + نسخة كاملة (Full Build)
```

---

## 2. هيكل النسختين

### 2.1 النسخة التجريبية (Canary / Trial Build)

**الهدف:** يمنحها المطور للعميل (المسؤول/Reseller) بعد كل تحديث للتحقق من الميزات الجديدة.

#### آلية الموقت الصامت (Silent Self-Destruct Timer)

- **لا تُظهر أي رسالة** للمستخدم عن وجود موقت أو تاريخ انتهاء
- البرنامج يعمل بشكل طبيعي تمامًا طوال فترة الصلاحية
- عند انتهاء الصلاحية: **الكود يُفجّر نفسه** — يحذف ملفات runtime الأساسية ثم يتوقف نهائيًا
- لا توجد طريقة لإعادة تشغيله إلا بتثبيت نسخة أحدث أو نسخة كاملة بمفتاح ترخيص

#### تفاصيل آلية التدمير الذاتي

```
عند التشغيل:
  1. يقرأ "وقت أول تشغيل" من سجل مشفّر (لا يظهر في Registry العادي)
  2. يحسب الفرق الزمني
  3. إذا تجاوز الحد (مثلاً 14 يوماً):
     - يمسح ملفات التكوين والمفاتيح الداخلية
     - يتلف ملف التنفيذ بكتابة بيانات عشوائية على بايتات محددة
     - يتوقف فوراً بدون رسالة
  4. إذا لم يتجاوز: يعمل بشكل طبيعي

ضد تغيير ساعة النظام:
  - يحفظ "طابع زمني مشفر" في موقعين مختلفين (Registry + ملف مخفي)
  - يستخدم "عداد تشغيل" بالإضافة للتاريخ (كل تشغيل = +1)
  - إذا رجع التاريخ للوراء → يعتبر تلاعبًا → تدمير فوري
  - إذا أمكن: يراجع وقت خادم NTP عبر الإنترنت لمقارنة الوقت
```

#### محتوى النسخة التجريبية
- جميع الميزات المضافة حتى لحظة الإصدار مفعّلة
- علامة مائية "نسخة تجريبية" على جميع المخرجات (JPG / PDF / مطبوعات)
- عداد عمليات محدود (مثلاً 50 طباعة كحد أقصى للجلسة كلها)
- **لا يوجد** نظام إدارة مفاتيح (admin panel) — هذا للنسخة الكاملة فقط

### 2.2 النسخة الكاملة (Full / Production Build)

- بدون علامة مائية
- جميع الميزات مفعّلة حسب المفتاح
- نظام ترخيص كامل (يوصف في القسم 3)
- العميل (المسؤول) هو من يتحكم بالمفاتيح والصلاحيات
- المطور **لا يتدخل** في إنشاء المفاتيح بعد تسليم النظام

---

## 3. نظام الترخيص — التصميم الكامل

### 3.1 هيكل الأدوار

```
المطور (أنت)
    │
    └── يبني ويسلّم: برنامج الإدارة (Admin Tool) + خادم الطباعة
    
العميل / المسؤول (Reseller / Admin)
    │
    ├── يمتلك: Admin Tool (برنامج منفصل مقفول بمفتاح رئيسي)
    ├── ينشئ: مفاتيح ترخيص للمستخدمين
    └── يحدد: صلاحيات كل مفتاح
    
المستخدم النهائي (End User)
    └── يحصل على: مفتاح ترخيص من العميل/المسؤول
        └── يُدخله في البرنامج للتفعيل
```

### 3.2 تصميم المفتاح (License Key Structure)

#### تركيب المفتاح المشفّر

```
المفتاح = Base32Encode( RSA_Sign( AES_Encrypt( JSON_Payload ) ) )

JSON_Payload يحتوي:
{
  "id":          "UUID فريد لهذا المفتاح",
  "issued_to":   "اسم العميل / المؤسسة",
  "issued_at":   "تاريخ الإنشاء (Unix timestamp)",
  "expires_at":  "تاريخ انتهاء الصلاحية (أو null للأبد)",
  "max_ops":     "عدد العمليات المسموح بها (مثلاً 1000 طباعة)",
  "features":    ["PRINT","PDF","JPG","MULTI_PORT","WHATSAPP"],
  "hw_lock":     false,           // هل مرتبط بجهاز معين؟
  "hw_id":       "بصمة الجهاز أو null",
  "tier":        "BASIC|PRO|ENTERPRISE",
  "watermark":   false            // هل يُضاف watermark؟
}
```

#### شكل المفتاح النهائي للمستخدم
```
DCMP-XXXX-XXXX-XXXX-XXXX-XXXX
(حروف وأرقام فقط، 30 خانة، مقسّمة بشرطات للقراءة السهلة)
```

### 3.3 برنامج الإدارة (Admin License Tool)

برنامج مستقل (WinForms أو WPF) يُسلَّم للعميل (المسؤول).

#### واجهة برنامج الإدارة — الشاشات

**شاشة 1: Dashboard**
```
┌─────────────────────────────────────────────────┐
│  DICOM Print Server — لوحة إدارة التراخيص       │
│  مرحباً: اسم العميل/المسؤول                      │
├─────────────────────────────────────────────────┤
│  📊 إحصائيات سريعة                              │
│  ├─ مفاتيح نشطة: 12                             │
│  ├─ مفاتيح منتهية: 3                            │
│  ├─ إجمالي العمليات الممنوحة: 5000              │
│  └─ العمليات المستهلكة: 2341                    │
├─────────────────────────────────────────────────┤
│  [ إنشاء مفتاح جديد ]  [ عرض المفاتيح ]        │
│  [ تصدير التقرير ]     [ الإعدادات ]            │
└─────────────────────────────────────────────────┘
```

**شاشة 2: إنشاء مفتاح جديد**
```
┌─────────────────────────────────────────────────┐
│  إنشاء مفتاح ترخيص جديد                         │
├─────────────────────────────────────────────────┤
│  اسم العميل/المؤسسة: [________________]          │
│  البريد الإلكتروني:  [________________]          │
├─────────────────────────────────────────────────┤
│  نوع الترخيص:                                   │
│    ○ أساسي (Basic)   ○ احترافي (Pro)             │
│    ○ مؤسسي (Enterprise)                          │
├─────────────────────────────────────────────────┤
│  عدد العمليات المسموحة:                          │
│    ○ 100 طباعة   ○ 500 طباعة   ○ 1000 طباعة    │
│    ○ مخصص: [____]  ○ غير محدود (Enterprise)    │
├─────────────────────────────────────────────────┤
│  تاريخ الانتهاء:                                 │
│    ○ لا ينتهي  ○ 1 سنة  ○ 6 أشهر  ○ [تاريخ]   │
├─────────────────────────────────────────────────┤
│  الميزات المفعّلة:                               │
│    ☑ الطباعة على Windows                        │
│    ☑ حفظ JPG                                    │
│    ☑ توليد PDF                                  │
│    ☑ منافذ متعددة                               │
│    ☐ تكامل WhatsApp                             │
│    ☐ تقارير متقدمة                              │
├─────────────────────────────────────────────────┤
│  ربط بجهاز محدد (Hardware Lock):                │
│    ○ لا  ○ نعم (أدخل بصمة الجهاز: [_______])   │
├─────────────────────────────────────────────────┤
│         [ إنشاء المفتاح ]  [ إلغاء ]            │
└─────────────────────────────────────────────────┘
```

**شاشة 3: عرض المفتاح المنشأ**
```
┌─────────────────────────────────────────────────┐
│  ✅ تم إنشاء المفتاح بنجاح                      │
├─────────────────────────────────────────────────┤
│  المفتاح:                                        │
│  DCMP-A3F2-9K1M-LP87-XQ23-TR56                 │
│                           [ نسخ ] [ إرسال ]     │
├─────────────────────────────────────────────────┤
│  تفاصيل المفتاح:                                 │
│  ├─ العميل: اسم العميل                          │
│  ├─ الصلاحية: 500 طباعة                         │
│  ├─ الانتهاء: 2027/06/01                        │
│  └─ الميزات: طباعة، JPG، PDF                   │
│                                                  │
│  إرسال عبر: [ بريد إلكتروني ] [ واتساب ] [ نسخ]│
└─────────────────────────────────────────────────┘
```

**شاشة 4: إدارة المفاتيح**
```
┌────┬──────────────┬──────┬────────┬──────────┬─────────┐
│ #  │ العميل       │ Ops  │ مستهلك │ الانتهاء │ الحالة  │
├────┼──────────────┼──────┼────────┼──────────┼─────────┤
│ 1  │ مستشفى الأمل│ 500  │ 342    │ 2027/01  │ 🟢 نشط  │
│ 2  │ مركز الصحة  │ 100  │ 100    │ 2026/03  │ 🔴 منته │
│ 3  │ عيادة النور  │ 1000 │ 12     │ لا ينتهي│ 🟢 نشط  │
└────┴──────────────┴──────┴────────┴──────────┴─────────┘
                   [ تعطيل ] [ تمديد ] [ حذف ]
```

### 3.4 آلية التفعيل في البرنامج الرئيسي

```
المستخدم يشغّل البرنامج أول مرة
    ↓
يظهر مربع "أدخل مفتاح الترخيص"
    ↓
البرنامج يتحقق من المفتاح:
  1. يفك تشفير المفتاح بالمفتاح العام RSA
  2. يتحقق من التوقيع الرقمي
  3. يستخرج الـ JSON Payload
  4. يحسب بصمة الجهاز الحالي
  5. يقارن مع hw_id (إن وُجد)
  6. يحفظ حالة الترخيص مشفّرة محلياً
    ↓
عند كل تشغيل لاحق:
  - يقرأ الترخيص المحفوظ ويتحقق من صحته
  - يقارن عداد العمليات بالحد الأقصى
  - كل عملية طباعة → عداد +1 → حفظ مشفّر
    ↓
عند نفاد العمليات:
  - يعرض رسالة "انتهت رصيد العمليات، تواصل مع مزوّد الخدمة"
  - يوقف الطباعة فقط (لا يُغلق البرنامج)
```

### 3.5 تخزين حالة الترخيص محلياً

```
الموقع: HKLM\Software\[مشفّر]\[مشفّر]
التشفير: AES-256-GCM
المحتوى المحفوظ:
  - مفتاح الترخيص (مشفّر)
  - عداد العمليات الحالي
  - تاريخ آخر استخدام
  - بصمة الجهاز وقت التفعيل

ملف احتياطي: %AppData%\[مشفّر]\[UUID].dat
(نفس المحتوى — يُستخدم للمقارنة لاكتشاف التلاعب)
```

---

## 4. منهجية الأمان — طبقات الحماية

> **ملاحظة صادقة:** لا يوجد حماية 100% مطلقة ضد الهندسة العكسية عندما يكون الملف في يد المستخدم. الهدف هو رفع التكلفة والوقت اللازمين للاختراق إلى مستوى يجعله غير مجدٍ اقتصادياً.

### الطبقة 1: تشويش الكود (Code Obfuscation)
- **الأداة:** ConfuserEx (مفتوح المصدر) أو Eazfuscator
- **ما يفعله:**
  - إعادة تسمية جميع الدوال والمتغيرات والكلاسات بأسماء عشوائية (`a`, `b`, `_0x3f`)
  - تشفير النصوص (Strings) المضمّنة في الكود
  - تعقيد تدفق التنفيذ (Control Flow Obfuscation)
  - حذف بيانات debugging (PDB symbols)
- **يُطبَّق:** تلقائياً في خطوة Post-Build في MSBuild

### الطبقة 2: مكافحة التصحيح (Anti-Debugging)
```csharp
// يُضاف في أماكن حساسة متعددة (ليس في مكان واحد)
private static void CheckDebugger()
{
    if (Debugger.IsAttached || Debugger.IsLogging())
        TriggerSilentDestruct();
    
    // Windows API check
    if (IsDebuggerPresent())
        TriggerSilentDestruct();
}
```

### الطبقة 3: فحص سلامة الملفات (Integrity Check)
```csharp
// عند التشغيل: يحسب SHA-256 لملف EXE نفسه
// يقارنه بقيمة محفوظة ومشفّرة داخل الكود
// أي تعديل على الـ EXE → يُكتشف → تدمير ذاتي
private static void VerifyExecutableIntegrity()
{
    var hash = ComputeFileHash(Assembly.GetExecutingAssembly().Location);
    var expected = GetEmbeddedHash(); // مشفّر داخل الـ DLL
    if (!hash.SequenceEqual(expected))
        TriggerSilentDestruct();
}
```

### الطبقة 4: التشفير الأساسي للترخيص
```
توليد المفاتيح:
  المطور يملك: RSA Private Key (2048-bit) — محفوظ بأمان لديه فقط
  البرنامج يحتوي: RSA Public Key (مُدمج في الكود المشوّش)

إنشاء مفتاح ترخيص:
  payload = JSON(البيانات)
  encrypted = AES-256(payload, secret_key)
  signed = RSA_Sign(encrypted, private_key)
  license_key = Base32(signed + encrypted)

التحقق في البرنامج:
  data = Base32_Decode(license_key)
  verified = RSA_Verify(data, public_key)  ← إذا فشل → مرفوض
  payload = AES_Decrypt(data, secret_key)
  json = Parse(payload)
```

### الطبقة 5: بصمة الجهاز (Hardware Fingerprint)
```csharp
public static string GetHardwareId()
{
    var components = new[]
    {
        GetCpuId(),           // معرّف المعالج
        GetMotherboardId(),   // معرّف اللوحة الأم
        GetDiskId(),          // معرّف القرص الصلب
        GetMacAddress()       // أول MAC address ثابت
    };
    
    // يجمعها ثم يشفّرها → نص فريد لكل جهاز
    return SHA256(string.Join("|", components));
}
```

### الطبقة 6: التدمير الذاتي (Self-Destruct)
```csharp
private static void TriggerSilentDestruct()
{
    try
    {
        // 1. مسح ملفات التكوين
        DeleteConfigFiles();
        
        // 2. تلف سجل الترخيص في Registry
        CorruptRegistryEntry();
        
        // 3. كتابة بيانات عشوائية في بداية EXE (يجعله لا يعمل)
        // ملاحظة: يتطلب إعادة تشغيل للتأثير الكامل
        ScheduleFileCorruption(); // يجدول عملية منفصلة
        
        // 4. إغلاق فوري بدون رسالة
        Environment.Exit(0);
    }
    catch { Environment.Exit(0); }
}
```

### الطبقة 7: التوقيع الرقمي للملف التنفيذي
- التوقيع بـ Authenticode Certificate (شهادة Code Signing)
- يمنع التعديل دون كسر التوقيع
- Windows يحذر المستخدم إذا تم تعديل الملف

### ملخص مستوى الأمان المتحقق

| الهجوم | مستوى الصعوبة بعد التطبيق |
|--------|--------------------------|
| نسخ EXE على جهاز آخر | ❌ مستحيل بدون مفتاح جديد (hw_lock) |
| تعديل الـ EXE يدوياً | ❌ يُكتشف → تدمير ذاتي |
| تصحيح البرنامج (Debugger) | ❌ يُكتشف → تدمير ذاتي |
| إنشاء مفتاح مزيّف | ❌ مستحيل بدون RSA Private Key |
| تجاوز عداد العمليات | ⚠️ صعب (مخزّن في موقعين مشفّرين) |
| الهندسة العكسية للكود | ⚠️ صعب جداً بعد Obfuscation |
| تغيير تاريخ النظام | ❌ يُكتشف → تدمير ذاتي |

---

## 5. مهام التطوير التفصيلية

---

### 🔷 [M1] البنية التحتية — Multi-Port Server

**فحص ما هو موجود أولاً:**
```
المطور يفحص:
  ✅ samples/Core/Print SCP/PrintService.cs      → SCP أساسي على منفذ واحد
  ✅ samples/Core/Print SCP/Program.cs           → نقطة الدخول
  ✅ library/FO-DICOM.Core/Network/DicomServer.cs → DicomServerFactory موجود
  ❌ لا يوجد: إدارة منافذ متعددة
  ❌ لا يوجد: ملف إعدادات appsettings.json
  → القرار: نُعيد هيكلة Program.cs ونُضيف MultiPortManager
```

**ما يجب بناؤه:**
- `DicomPrintServer` — مشروع جديد (Worker Service) يستبدل مشروع الـ Sample
- `MultiPortManager` — يدير قائمة ديناميكية من `IDicomServer`
- `appsettings.json` — إعدادات المنافذ، الطابعات، AET
- دعم إضافة/إزالة منفذ أثناء التشغيل

**هيكل appsettings.json:**
```json
{
  "PrintServer": {
    "Listeners": [
      {
        "Port": 8000,
        "AET": "PRINTER_A",
        "WindowsPrinterName": "HP LaserJet 1020",
        "SaveJpg": true,
        "JpgQuality": 95,
        "SavePdf": true,
        "OutputFolder": "C:\\PrintOutput\\A"
      },
      {
        "Port": 8001,
        "AET": "PRINTER_B",
        "WindowsPrinterName": "Canon iR3045",
        "SaveJpg": false,
        "SavePdf": true,
        "OutputFolder": "C:\\PrintOutput\\B"
      }
    ],
    "CenterLogo": "C:\\Config\\logo.png",
    "CenterName": "مركز الأشعة الطبية"
  }
}
```

**الاختبار:** الاتصال من جهازين مختلفين على منفذين مختلفين في نفس الوقت.

---

### 🔷 [M2] معالجة الصور

#### [M2-A] حفظ الصور بصيغة JPG

**فحص ما هو موجود أولاً:**
```
المطور يفحص:
  ✅ library/Platform/FO-DICOM.Imaging.Desktop/Printing/ImageBoxExtensions.cs
     → Print() يرسم على Graphics — موجود ومكتمل
  ✅ library/FO-DICOM.Core/Imaging/DicomImage.cs → RenderImage() موجود
  ✅ library/Platform/FO-DICOM.Imaging.ImageSharp/ → ImageSharp موجود
  ❌ لا يوجد: حفظ كـ JPG
  → القرار: نستخدم FilmBox.Print() الموجود + Bitmap.Save() لحفظ JPG
```

**ما يجب بناؤه:**
```csharp
public class JpgExporter
{
    // يأخذ FilmBox → يرسمها على Bitmap → يحفظ JPG
    public string ExportFilmBox(FilmBox filmBox, string outputFolder, int quality);
    
    // يحفظ كل ImageBox كملف منفصل
    public IEnumerable<string> ExportImageBoxes(FilmBox filmBox, string outputFolder);
}
```

#### [M2-B] تعديل الجاما والكونتراست

**فحص ما هو موجود أولاً:**
```
المطور يفحص:
  ✅ library/FO-DICOM.Core/Imaging/Render/ImageGraphic.cs → Pipeline تعديل الصورة
  ✅ library/FO-DICOM.Core/Imaging/ → DicomImage.RenderImage() 
  ❌ لا يوجد: Gamma/Contrast controls مكشوفة للمستخدم
  → القرار: نضيف ImageProcessor يطبّق التعديلات على Bitmap قبل الحفظ/الطباعة
```

**ما يجب بناؤه:**
```csharp
public class ImageProcessor
{
    public Bitmap ApplyGamma(Bitmap source, double gamma);
    public Bitmap ApplyContrast(Bitmap source, double contrast);
    public Bitmap ApplyBoth(Bitmap source, double gamma, double contrast);
}

// في appsettings.json لكل منفذ:
"ImageProcessing": {
    "Gamma": 1.0,
    "Contrast": 1.0
}
```

#### [M2-C] إضافة Header و Footer

**فحص ما هو موجود أولاً:**
```
المطور يفحص:
  ✅ DICOM Dataset يحتوي بيانات المريض: PatientName, StudyDate, Modality
  ✅ ImageBoxExtensions.Print() يرسم على Graphics
  ❌ لا يوجد: رسم نص/شعار فوق/تحت الصورة
  → القرار: نضيف ImageAnnotator يضيف header/footer بعد الرسم الأساسي
```

**ما يجب بناؤه:**
```csharp
public class ImageAnnotator
{
    // يضيف header (نص + شعار) وfooter على Bitmap
    public Bitmap AddAnnotations(
        Bitmap source,
        DicomDataset dicomData,
        AnnotationConfig config);
}

// AnnotationConfig في appsettings.json:
"Annotations": {
    "Header": {
        "Enabled": true,
        "LogoPath": "C:\\Config\\logo.png",
        "Text": "{CenterName}",
        "Height": 60
    },
    "Footer": {
        "Enabled": true,
        "Text": "{PatientName} | {StudyDate} | {Modality}",
        "Height": 40
    }
}
```

#### [M2-D] وضع المعايرة (Calibration Mode)

**فحص ما هو موجود أولاً:**
```
المطور يفحص:
  ✅ ImageProcessor (من M2-B)
  ✅ System.Drawing.Printing لطباعة ورقة كاملة
  ❌ لا يوجد: شبكة مقارنة (Calibration Grid)
  → القرار: نبني CalibrationPrinter يُنشئ صورة مركّبة من نسخ متعددة
```

**ما يجب بناؤه:**
```csharp
public class CalibrationPrinter
{
    // ينشئ صورة 3×3 (أو NxM) من نفس الصورة بقيم جاما/كونتراست مختلفة
    // يضيف تسمية لكل خلية: "γ=0.8 C=1.2"
    public Bitmap CreateCalibrationGrid(
        Bitmap source,
        CalibrationConfig config);
}

"CalibrationMode": {
    "Enabled": false,
    "Columns": 3,
    "Rows": 3,
    "GammaRange": [0.7, 0.8, 0.9, 1.0, 1.1, 1.2, 1.3, 1.4, 1.5],
    "ContrastRange": [0.8, 1.0, 1.2]
}
```

---

### 🔷 [M3] توليد PDF

**فحص ما هو موجود أولاً:**
```
المطور يفحص:
  ✅ JpgExporter (من M2-A) → صور JPG جاهزة
  ✅ ImageAnnotator (من M2-C) → Header/Footer جاهز
  ✅ DICOM Dataset → PatientName, StudyID, StudyDate متاحة
  ❌ لا يوجد: مكتبة PDF
  ❌ لا يوجد: منطق تجميع الحالات
  → القرار: نضيف QuestPDF (مفتوح المصدر) ونبني PdfBuilder
```

**ما يجب بناؤه:**

**أ) إضافة مكتبة PDF:**
```xml
<PackageReference Include="QuestPDF" Version="2024.*" />
```

**ب) PdfBuilder:**
```csharp
public class PdfBuilder
{
    // يُنشئ PDF لحالة مريض كاملة
    public string BuildPatientPdf(
        string patientName,
        string studyId,
        string centerName,
        string logoPath,
        IEnumerable<Bitmap> images,
        string outputFolder);
}
```

**ج) تنسيق PDF:**
```
الصفحة 1 (Cover Page):
┌─────────────────────────┐
│  [شعار المركز]          │
│  اسم المركز             │
│                         │
│  اسم المريض: ___        │
│  رقم الحالة: ___        │
│  تاريخ الفحص: ___       │
│  نوع الفحص: ___         │
└─────────────────────────┘

الصفحات 2، 3، ... (صور الطباعة):
كل صفحة تحتوي على صورة FilmBox كاملة
مع header/footer مختصر
```

**د) اسم ملف PDF:**
```
{PatientName}_{StudyDate}_{StudyID}.pdf
مثال: AhmedMohamed_20260601_ST12345.pdf
```

**هـ) منطق التجميع:**
```csharp
// يجمّع FilmBoxes بنفس Patient ID في PDF واحد
// إذا وصل FilmBox جديد لنفس المريض خلال X دقيقة → يُضاف لنفس PDF
// بعد X دقيقة من آخر صورة → يُغلق PDF ويُرسَل
public class PdfSessionManager
{
    private Dictionary<string, PdfSession> _activeSessions;
    public void AddFilmBox(string patientId, FilmBox filmBox);
    public void FlushSession(string patientId); // يُغلق ويحفظ PDF
}
```

---

### 🔷 [M4] الطباعة على أي طابعة Windows

**فحص ما هو موجود أولاً:**
```
المطور يفحص:
  ✅ samples/Core/Print SCP/PrintJob.cs → DoPrint() موجود لكن hardcoded
     PrinterName = "Microsoft XPS Document Writer"
  ✅ System.Drawing.Printing.PrinterSettings → دعم اختيار الطابعة موجود
  ❌ لا يوجد: قراءة اسم الطابعة من الإعدادات
  → القرار: تعديل بسيط في DoPrint() لقراءة اسم الطابعة من appsettings
```

**التعديل المطلوب:**
```csharp
// في PrintJob.DoPrint():
var printerSettings = new PrinterSettings
{
    // بدلاً من القيمة الثابتة:
    PrinterName = _config.WindowsPrinterName, // من appsettings
    PrintToFile = false
};
```

**إضافة: PrinterDiscovery:**
```csharp
public class PrinterDiscovery
{
    // يُعيد قائمة الطابعات المثبتة على Windows
    public IEnumerable<string> GetInstalledPrinters()
        => PrinterSettings.InstalledPrinters.Cast<string>();
}
```

---

### 🔷 [M5] المراقبة والتقارير

**فحص ما هو موجود أولاً:**
```
المطور يفحص:
  ✅ PrintJob.cs → يحتوي Status و SOPInstanceUID
  ✅ PrintService.cs → يستقبل جميع الطلبات
  ❌ لا يوجد: قاعدة بيانات، عدادات، تقارير
  → القرار: نضيف SQLite + PrintCounterService
```

**ما يجب بناؤه:**

**أ) قاعدة بيانات SQLite:**
```sql
CREATE TABLE PrintOperations (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Timestamp   TEXT NOT NULL,
    PatientId   TEXT,
    PatientName TEXT,
    PrinterName TEXT,
    Port        INTEGER,
    Status      TEXT,
    FilmBoxes   INTEGER,
    OutputJpg   TEXT,
    OutputPdf   TEXT
);

CREATE TABLE DailyCounters (
    Date        TEXT,
    PrinterName TEXT,
    PrintCount  INTEGER,
    PdfCount    INTEGER,
    PRIMARY KEY (Date, PrinterName)
);
```

**ب) PrintCounterService:**
```csharp
public class PrintCounterService
{
    void RecordPrintOperation(PrintOperation op);
    PrintReport GetReport(DateTime from, DateTime to, string printerName);
    int GetTodayCount(string printerName);
    bool IsLimitExceeded(string printerName); // يفحص الحد اليومي
}
```

**ج) واجهة التقارير (web بسيطة):**
- ASP.NET Core Minimal API على منفذ إداري (مثلاً 9000)
- صفحة HTML بسيطة تعرض الإحصائيات اليومية والأسبوعية
- تصدير CSV

---

### 🔷 [M6] نظام الترخيص

**فحص ما هو موجود أولاً:**
```
المطور يفحص:
  ✅ docs/secure_trial_distribution_and_licensing_plan.md → خطة موجودة
  ❌ لا يوجد: أي كود ترخيص
  → القرار: بناء كامل من الصفر وفق التصميم في القسم 3
```

**مكونات نظام الترخيص (4 مشاريع منفصلة):**

```
DicomPrintServer.Licensing/          ← مكتبة مشتركة
DicomPrintServer.LicenseGenerator/  ← أداة المطور (سرية)
DicomPrintServer.AdminTool/         ← برنامج المسؤول/العميل
DicomPrintServer/                   ← البرنامج الرئيسي
```

**ترتيب التنفيذ:**
1. `LicenseManager` — التحقق من المفاتيح (في المكتبة المشتركة)
2. `HardwareIdProvider` — بصمة الجهاز
3. `LicenseStorage` — تخزين مشفّر في Registry
4. `LicenseGenerator` — أداة المطور لإنشاء المفاتيح
5. `AdminTool` — واجهة المسؤول/العميل
6. `AntiTamper` — طبقات الحماية
7. `SilentTimer` — موقت النسخة التجريبية

---

### 🔷 [M7] تكامل WhatsApp

**فحص ما هو موجود أولاً:**
```
المطور يفحص:
  ✅ PdfBuilder (من M3) → PDF جاهز
  ✅ JpgExporter (من M2-A) → JPG جاهز
  ❌ لا يوجد: أي تكامل WhatsApp
  → القرار: نستخدم WhatsApp Business API (أو Twilio)
```

**ما يجب بناؤه:**
```csharp
public class WhatsAppSender
{
    // يرسل رسالة مع مرفق PDF أو رابط
    Task SendCaseAsync(
        string phoneNumber,
        string patientName,
        string pdfPath,
        WhatsAppConfig config);
}

"WhatsApp": {
    "Enabled": false,
    "Provider": "Twilio",        // أو "360dialog" أو "Meta"
    "AccountSid": "...",
    "AuthToken": "...",
    "FromNumber": "+96655...",
    "MessageTemplate": "مرحباً {PatientName}، نتائج فحصك متاحة للتحميل"
}
```

---

## 6. جدول الأولويات والجهد التقديري

| # | المهمة | الأولوية | الجهد | يعتمد على | نسخة تسليم |
|---|---------|----------|-------|-----------|------------|
| M1 | Multi-Port Infrastructure | 🔴 حرجة | 4 أيام | — | v0.1 |
| M2-A | JPG Export | 🔴 حرجة | 2 يوم | M1 | v0.2 |
| M4 | Windows Printer Selection | 🔴 حرجة | 1 يوم | M1 | v0.2 |
| M2-B | Gamma/Contrast | 🟠 عالية | 2 يوم | M2-A | v0.3 |
| M2-C | Header/Footer | 🟠 عالية | 2 يوم | M2-A | v0.3 |
| M3 | PDF Generation | 🔴 حرجة | 4 أيام | M2-A, M2-C | v0.4 |
| M5 | Monitoring/Reports | 🟠 عالية | 3 أيام | M1 | v0.5 |
| M2-D | Calibration Mode | 🟡 متوسطة | 2 يوم | M2-B | v0.6 |
| M6-L | License Manager | 🔴 حرجة | 3 أيام | M5 | v0.7 |
| M6-G | License Generator (Dev Tool) | 🔴 حرجة | 2 يوم | M6-L | v0.7 |
| M6-A | Admin Tool (Reseller UI) | 🔴 حرجة | 4 أيام | M6-G | v0.8 |
| M6-S | Security Layers (AntiTamper) | 🔴 حرجة | 3 أيام | M6-L | v0.8 |
| M6-T | Silent Trial Timer | 🟠 عالية | 2 يوم | M6-S | v0.8 |
| M7 | WhatsApp Integration | 🟡 متوسطة | 3 أيام | M3 | v0.9 |
| **الإجمالي** | | | **~37 يوم** | | **v1.0** |

---

## 7. آلية إصدار نسخ التسليم

بعد اكتمال كل مجموعة مهام:

```
المطور يُصدر نسختين:

📦 نسخة v0.X-Trial (للفحص)
   ├── مدة: 14 يوم من التثبيت (صامتة)
   ├── عمليات: 50 طباعة كحد أقصى
   ├── watermark: "نسخة تجريبية"
   └── تدمير ذاتي بعد انتهاء المدة أو العمليات

📦 نسخة v0.X-Full (للإنتاج)
   ├── تتطلب مفتاح ترخيص صالح
   ├── بدون watermark
   └── جميع الميزات حسب المفتاح
```

---

## ملاحظات التنفيذ

1. **Platform:** Windows فقط (نظرًا لاعتماد الطباعة على `System.Drawing.Printing`)
2. **Framework:** .NET 8.0 Windows (التوافق مع النسخة الموجودة في مشروع الـ Sample)
3. **مشروع رئيسي:** `DicomPrintServer.sln` يشمل جميع المشاريع الفرعية
4. **إعادة الاستخدام:** الكود الموجود في `samples/Core/Print SCP/` هو نقطة البداية — لا نكتب من الصفر
5. **الأمان:** RSA Private Key يبقى عند المطور فقط — لا يُدمج في أي EXE يُوزَّع
