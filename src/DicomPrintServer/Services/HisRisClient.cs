using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DicomPrintServer.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DicomPrintServer.Services
{
    /// <summary>
    /// تكامل HIS/RIS — يجلب بيانات المريض (الاسم، رقم الهاتف) من نظام الاستقبال.
    ///
    /// يدعم أربعة مصادر بالأولوية:
    ///   1. FHIR R4 REST API   — GET /Patient?identifier={id}
    ///   2. HL7 v2.5 MLLP      — QBP^Q22 (Patient Demographics Query)
    ///   3. CSV Lookup         — ملف محلي PatientID,Name,Phone
    ///   4. DICOM Tag          — يُقرأ من FilmSession مباشرةً (خارج هذا الكلاس)
    ///
    /// التكوين في appsettings.json → PrintServer.HisRis
    /// </summary>
    public class HisRisClient : IDisposable
    {
        private readonly HisRisConfig _config;
        private readonly ILogger<HisRisClient> _logger;
        private readonly HttpClient _http;
        private readonly ConcurrentDictionary<string, PatientInfo> _csvCache = new();
        private bool _csvLoaded;

        public HisRisClient(
            IOptions<PrintServerConfig> options,
            ILogger<HisRisClient> logger)
        {
            _config = options.Value.HisRis;
            _logger = logger;

            _http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(Math.Max(5, _config.FhirTimeoutSec))
            };

            if (!string.IsNullOrEmpty(_config.FhirBearerToken))
                _http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", _config.FhirBearerToken);

            if (_config.Provider == "CSV")
                LoadCsvSafe();
        }

        // ══════════════════════════════════════════════════════════════════════
        // واجهة عامة
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// يجلب بيانات المريض — يُعيد null إذا كان التكامل معطّلاً أو فشل الاستعلام.
        /// </summary>
        public async Task<PatientInfo?> GetPatientInfoAsync(
            string patientId,
            string patientName,
            CancellationToken ct = default)
        {
            if (!_config.Enabled)
            {
                _logger.LogDebug("HisRis disabled — skipping patient lookup");
                return null;
            }

            if (string.IsNullOrWhiteSpace(patientId) && string.IsNullOrWhiteSpace(patientName))
            {
                _logger.LogDebug("HisRis: no patientId or name — skipping");
                return null;
            }

            try
            {
                PatientInfo? result = _config.Provider switch
                {
                    "FHIR"  => await QueryFhirAsync(patientId, patientName, ct),
                    "HL7v2" => await QueryHl7v2Async(patientId, ct),
                    "CSV"   => QueryCsv(patientId),
                    _       => null
                };

                if (result != null)
                    _logger.LogInformation(
                        "HisRis [{Provider}]: found patient {Name} / phone={Phone}",
                        _config.Provider, result.PatientName, result.Phone ?? "(none)");
                else
                    _logger.LogDebug("HisRis [{Provider}]: no result for id={Id}", _config.Provider, patientId);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HisRis query failed for patientId={Id}", patientId);
                return null;
            }
        }

        /// <summary>يُعيد رقم الهاتف فقط (null إذا لم يُعثَر عليه).</summary>
        public async Task<string?> GetPatientPhoneAsync(
            string patientId,
            string patientName,
            CancellationToken ct = default)
        {
            var info = await GetPatientInfoAsync(patientId, patientName, ct);
            return string.IsNullOrWhiteSpace(info?.Phone) ? null : info!.Phone;
        }

        // ══════════════════════════════════════════════════════════════════════
        // FHIR R4
        // ══════════════════════════════════════════════════════════════════════

        private async Task<PatientInfo?> QueryFhirAsync(
            string patientId, string patientName, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_config.FhirBaseUrl))
            {
                _logger.LogWarning("FHIR base URL not configured");
                return null;
            }

            string baseUrl = _config.FhirBaseUrl.TrimEnd('/');

            // ابحث بالـ identifier أولاً، ثم بالاسم
            string searchUrl = !string.IsNullOrEmpty(patientId)
                ? $"{baseUrl}/Patient?identifier={Uri.EscapeDataString(patientId)}"
                : $"{baseUrl}/Patient?name={Uri.EscapeDataString(patientName)}&_count=1";

            _logger.LogDebug("FHIR query: {Url}", searchUrl);

            var resp = await _http.GetAsync(searchUrl, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("FHIR returned {Status} for {Url}", resp.StatusCode, searchUrl);
                return null;
            }

            string json = await resp.Content.ReadAsStringAsync(ct);
            var doc     = JsonDocument.Parse(json);
            var root    = doc.RootElement;

            // Bundle → entry[0].resource
            if (root.TryGetProperty("entry", out var entries) && entries.GetArrayLength() > 0)
            {
                var first = entries[0];
                if (first.TryGetProperty("resource", out var resource))
                    return ParseFhirPatient(resource);
            }

            return null;
        }

        private static PatientInfo ParseFhirPatient(JsonElement resource)
        {
            var info = new PatientInfo();

            if (resource.TryGetProperty("id", out var id))
                info.PatientId = id.GetString() ?? "";

            // الاسم
            if (resource.TryGetProperty("name", out var names) && names.GetArrayLength() > 0)
            {
                var nameEl = names[0];
                string family = nameEl.TryGetProperty("family", out var f) ? f.GetString() ?? "" : "";
                string given  = nameEl.TryGetProperty("given",  out var g) && g.GetArrayLength() > 0
                    ? g[0].GetString() ?? "" : "";
                info.PatientName = $"{given} {family}".Trim();
            }

            // الهاتف
            if (resource.TryGetProperty("telecom", out var telecoms))
            {
                foreach (var t in telecoms.EnumerateArray())
                {
                    bool isPhone = t.TryGetProperty("system", out var sys) && sys.GetString() == "phone";
                    if (isPhone && t.TryGetProperty("value", out var val))
                    {
                        info.Phone = val.GetString();
                        break;
                    }
                }
            }

            // تاريخ الميلاد
            if (resource.TryGetProperty("birthDate", out var dob))
                info.DateOfBirth = dob.GetString();

            return info;
        }

        // ══════════════════════════════════════════════════════════════════════
        // HL7 v2.5 MLLP — QBP^Q22 (Patient Demographics Query)
        // ══════════════════════════════════════════════════════════════════════

        private async Task<PatientInfo?> QueryHl7v2Async(string patientId, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_config.Hl7Host))
            {
                _logger.LogWarning("HL7v2: host not configured");
                return null;
            }

            string msgId    = Guid.NewGuid().ToString("N")[..20].ToUpper();
            string queryId  = Guid.NewGuid().ToString("N")[..20].ToUpper();
            string now      = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            string app      = _config.Hl7SendingApp;
            string fac      = _config.Hl7SendingFac;

            // بناء رسالة QBP^Q22 لاستعلام بيانات المريض
            string hl7Message =
                $"MSH|^~\\&|{app}|{fac}|HIS|HIS|{now}||QBP^Q22^QBP_Q21|{msgId}|P|2.5\r" +
                $"QPD|IHE PDQ Query|{queryId}|@PID.3.1^{patientId}\r" +
                $"RCP|I|1^RD\r";

            _logger.LogDebug("HL7v2 query → {Host}:{Port} for patientId={Id}",
                _config.Hl7Host, _config.Hl7Port, patientId);

            string response = await SendMllpMessageAsync(hl7Message, ct);
            if (string.IsNullOrEmpty(response))
                return null;

            return ParseHl7Response(response);
        }

        private async Task<string> SendMllpMessageAsync(string message, CancellationToken ct)
        {
            using var client = new TcpClient();
            using var cts    = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_config.Hl7TimeoutSec));

            await client.ConnectAsync(_config.Hl7Host, _config.Hl7Port, cts.Token);
            using var stream = client.GetStream();

            // MLLP framing: 0x0B + message bytes + 0x1C + 0x0D
            byte[] msgBytes = Encoding.UTF8.GetBytes(message);
            byte[] framed   = new byte[msgBytes.Length + 3];
            framed[0] = 0x0B;
            msgBytes.CopyTo(framed, 1);
            framed[^2] = 0x1C;
            framed[^1] = 0x0D;

            await stream.WriteAsync(framed, cts.Token);
            await stream.FlushAsync(cts.Token);

            // قراءة الرد
            var buffer = new byte[65536];
            int read   = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
            if (read < 3) return "";

            // إزالة MLLP framing من الرد
            string rawResponse = Encoding.UTF8.GetString(buffer, 1, read - 3);
            return rawResponse;
        }

        private static PatientInfo? ParseHl7Response(string response)
        {
            var segments = response.Split('\r', StringSplitOptions.RemoveEmptyEntries);

            // تحقق من ACK ناجح
            bool hasAck = segments.Any(s =>
            {
                if (!s.StartsWith("MSA")) return false;
                var f = s.Split('|');
                return f.Length > 1 && f[1] == "AA";
            });

            if (!hasAck) return null;

            foreach (var seg in segments)
            {
                if (!seg.StartsWith("PID")) continue;

                var f    = seg.Split('|');
                var info = new PatientInfo();

                // PID-3: Patient ID
                if (f.Length > 3)
                    info.PatientId = f[3].Split('^')[0].Trim();

                // PID-5: Patient Name — Last^First^Middle
                if (f.Length > 5 && !string.IsNullOrEmpty(f[5]))
                {
                    var parts = f[5].Split('^');
                    string last  = parts.Length > 0 ? parts[0].Trim() : "";
                    string first = parts.Length > 1 ? parts[1].Trim() : "";
                    info.PatientName = $"{first} {last}".Trim();
                }

                // PID-7: Date of Birth
                if (f.Length > 7) info.DateOfBirth = f[7].Trim();

                // PID-13: Phone Home
                if (f.Length > 13 && !string.IsNullOrEmpty(f[13]))
                    info.Phone = f[13].Split('^')[0].Trim();

                // PID-14: Phone Business (fallback)
                if (string.IsNullOrEmpty(info.Phone) && f.Length > 14 && !string.IsNullOrEmpty(f[14]))
                    info.Phone = f[14].Split('^')[0].Trim();

                return info;
            }

            return null;
        }

        // ══════════════════════════════════════════════════════════════════════
        // CSV Lookup
        // ══════════════════════════════════════════════════════════════════════

        private void LoadCsvSafe()
        {
            try
            {
                if (string.IsNullOrEmpty(_config.CsvFilePath) || !File.Exists(_config.CsvFilePath))
                {
                    _logger.LogWarning("HisRis CSV file not found: {Path}", _config.CsvFilePath);
                    return;
                }

                int count = 0;
                foreach (var line in File.ReadLines(_config.CsvFilePath, Encoding.UTF8).Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split(',');
                    if (parts.Length < 2) continue;

                    string pid   = parts[0].Trim();
                    string name  = parts.Length > 1 ? parts[1].Trim() : "";
                    string phone = parts.Length > 2 ? parts[2].Trim() : "";

                    if (!string.IsNullOrEmpty(pid))
                    {
                        _csvCache[pid] = new PatientInfo
                        {
                            PatientId   = pid,
                            PatientName = name,
                            Phone       = string.IsNullOrEmpty(phone) ? null : phone
                        };
                        count++;
                    }
                }

                _csvLoaded = true;
                _logger.LogInformation("HisRis CSV loaded: {Count} patients from {Path}",
                    count, _config.CsvFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load HisRis CSV from {Path}", _config.CsvFilePath);
            }
        }

        private PatientInfo? QueryCsv(string patientId)
        {
            if (!_csvLoaded) LoadCsvSafe();
            return _csvCache.TryGetValue(patientId, out var info) ? info : null;
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // DTOs
    // ══════════════════════════════════════════════════════════════════════════

    public class PatientInfo
    {
        public string  PatientId   { get; set; } = "";
        public string  PatientName { get; set; } = "";
        public string? Phone       { get; set; }
        public string? DateOfBirth { get; set; }
        public string? Email       { get; set; }
    }
}
