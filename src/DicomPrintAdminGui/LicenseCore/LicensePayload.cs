using System.Text.Json.Serialization;

namespace DicomPrintAdminGui.LicenseCore
{
    /// <summary>
    /// الحمولة الداخلية لمفتاح الترخيص (قبل التشفير).
    /// تُتشارك بين Admin GUI وخادم الطباعة.
    /// </summary>
    public class LicensePayload
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [JsonPropertyName("issued_to")]
        public string IssuedTo { get; set; } = "";

        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("issued_at")]
        public long IssuedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        [JsonPropertyName("expires_at")]
        public long? ExpiresAt { get; set; }

        [JsonPropertyName("max_ops")]
        public int MaxOps { get; set; } = 500;

        [JsonPropertyName("features")]
        public List<string> Features { get; set; } = new() { "PRINT", "JPG" };

        [JsonPropertyName("hw_lock")]
        public bool HwLock { get; set; } = false;

        [JsonPropertyName("hw_id")]
        public string? HwId { get; set; }

        [JsonPropertyName("tier")]
        public string Tier { get; set; } = "BASIC";

        [JsonPropertyName("watermark")]
        public bool Watermark { get; set; } = false;

        // ── مساعدات ────────────────────────────────────────────

        public bool IsExpired()
        {
            if (ExpiresAt == null) return false;
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() > ExpiresAt.Value;
        }

        public DateTime? ExpiresAtDate => ExpiresAt.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds(ExpiresAt.Value).LocalDateTime
            : null;

        public DateTime IssuedAtDate =>
            DateTimeOffset.FromUnixTimeSeconds(IssuedAt).LocalDateTime;

        public string TierDisplay => Tier switch
        {
            "BASIC"      => "أساسي (Basic)",
            "PRO"        => "احترافي (Pro)",
            "ENTERPRISE" => "مؤسسي (Enterprise)",
            _ => Tier
        };
    }
}
