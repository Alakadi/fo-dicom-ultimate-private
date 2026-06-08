using System.Text.Json;

namespace DicomPrintAdminGui.LicenseCore
{
    /// <summary>
    /// يُخزّن المفاتيح الصادرة في ملف JSON محلي (لدى المسؤول/Reseller).
    /// ملف: %AppData%\DCMPrint\issued_keys.json
    /// </summary>
    public class LicenseStore
    {
        private readonly string _filePath;
        private List<IssuedLicense> _cache;

        public LicenseStore(string? customPath = null)
        {
            _filePath = customPath
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DCMPrint", "issued_keys.json");

            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            _cache = Load();
        }

        /// <summary>يُضيف ترخيصاً جديداً.</summary>
        public void Add(IssuedLicense license)
        {
            _cache.Add(license);
            Save();
        }

        /// <summary>يُعيد قائمة جميع التراخيص.</summary>
        public IReadOnlyList<IssuedLicense> GetAll() => _cache.AsReadOnly();

        /// <summary>يُعيد التراخيص النشطة فقط.</summary>
        public IReadOnlyList<IssuedLicense> GetActive()
            => _cache.Where(l => !l.Payload.IsExpired() && !l.Revoked).ToList();

        /// <summary>يُلغي ترخيصاً بمعرّفه.</summary>
        public bool Revoke(string licenseId)
        {
            var lic = _cache.FirstOrDefault(l => l.Payload.Id == licenseId);
            if (lic == null) return false;
            lic.Revoked     = true;
            lic.RevokedAt   = DateTime.UtcNow;
            Save();
            return true;
        }

        /// <summary>يُمدّد تاريخ انتهاء ترخيص.</summary>
        public bool Extend(string licenseId, DateTime newExpiry)
        {
            var lic = _cache.FirstOrDefault(l => l.Payload.Id == licenseId);
            if (lic == null) return false;
            lic.Payload.ExpiresAt = new DateTimeOffset(newExpiry).ToUnixTimeSeconds();
            Save();
            return true;
        }

        /// <summary>إحصائيات سريعة.</summary>
        public LicenseStats GetStats()
        {
            return new LicenseStats
            {
                Total    = _cache.Count,
                Active   = _cache.Count(l => !l.Payload.IsExpired() && !l.Revoked),
                Expired  = _cache.Count(l => l.Payload.IsExpired()),
                Revoked  = _cache.Count(l => l.Revoked),
                TotalOps = _cache.Sum(l => l.Payload.MaxOps),
            };
        }

        private List<IssuedLicense> Load()
        {
            if (!File.Exists(_filePath)) return new();
            try
            {
                string json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<List<IssuedLicense>>(json) ?? new();
            }
            catch { return new(); }
        }

        private void Save()
        {
            string json = JsonSerializer.Serialize(_cache,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
    }

    public class IssuedLicense
    {
        public string         LicenseKey { get; set; } = "";
        public LicensePayload Payload    { get; set; } = new();
        public DateTime       IssuedDate { get; set; } = DateTime.UtcNow;
        public bool           Revoked    { get; set; } = false;
        public DateTime?      RevokedAt  { get; set; }
    }

    public class LicenseStats
    {
        public int  Total    { get; set; }
        public int  Active   { get; set; }
        public int  Expired  { get; set; }
        public int  Revoked  { get; set; }
        public int  TotalOps { get; set; }
    }
}
