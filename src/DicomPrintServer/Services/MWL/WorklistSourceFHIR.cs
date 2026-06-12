using System.Text.Json;
using DicomPrintServer.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DicomPrintServer.Services.MWL
{
    public class WorklistSourceFHIR : IWorklistSource
    {
        private readonly ILogger<WorklistSourceFHIR> _logger;
        private readonly HisRisConfig _config;
        private readonly HttpClient _http;

        public string SourceName => "FHIR";

        public WorklistSourceFHIR(ILogger<WorklistSourceFHIR> logger, IOptions<PrintServerConfig> options)
        {
            _logger = logger;
            _config = options.Value.HisRis;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(_config.FhirTimeoutSec) };

            if (!string.IsNullOrEmpty(_config.FhirBearerToken))
                _http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.FhirBearerToken);
        }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async Task<IReadOnlyList<WorklistItem>> FindAsync(
            MWLQueryCriteria criteria, int maxResults, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_config.FhirBaseUrl))
            {
                _logger.LogWarning("FHIR: Base URL not configured");
                return Array.Empty<WorklistItem>();
            }

            string baseUrl = _config.FhirBaseUrl.TrimEnd('/');
            var query = new List<string>();

            if (!string.IsNullOrEmpty(criteria.PatientID))
                query.Add($"identifier={Uri.EscapeDataString(criteria.PatientID)}");
            if (!string.IsNullOrEmpty(criteria.PatientName))
                query.Add($"name={Uri.EscapeDataString(criteria.PatientName)}");
            if (!string.IsNullOrEmpty(criteria.PatientSex))
                query.Add($"gender={Uri.EscapeDataString(criteria.PatientSex.ToLower())}");
            if (!string.IsNullOrEmpty(criteria.ScheduledProcedureStepStartDate))
                query.Add($"date=ge{criteria.ScheduledProcedureStepStartDate}");

            string url = $"{baseUrl}/ServiceRequest?_count={maxResults}" +
                         (query.Count > 0 ? "&" + string.Join("&", query) : "");

            _logger.LogDebug("FHIR query: {Url}", url);

            var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("FHIR returned {Status}", resp.StatusCode);
                return Array.Empty<WorklistItem>();
            }

            string json = await resp.Content.ReadAsStringAsync(ct);
            return ParseFHIRBundle(json);
        }

        private static List<WorklistItem> ParseFHIRBundle(string json)
        {
            var items = new List<WorklistItem>();
            var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("entry", out var entries))
                return items;

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("resource", out var resource))
                    continue;

                var item = new WorklistItem
                {
                    ScheduledProcedureStepID = resource.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    ScheduledProcedureStepDescription = resource.TryGetProperty("code", out var code)
                        ? code.ToString() : "",
                    ScheduledProcedureStepStatus = "SCHEDULED"
                };

                if (resource.TryGetProperty("subject", out var subject) &&
                    subject.TryGetProperty("reference", out var patientRef))
                {
                    item.PatientID = patientRef.GetString() ?? "";
                }

                items.Add(item);
            }

            return items;
        }

        public Task<bool> UpsertAsync(WorklistItem item, CancellationToken ct = default)
        {
            _logger.LogWarning("FHIR: Upsert not supported");
            return Task.FromResult(false);
        }

        public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
        {
            _logger.LogWarning("FHIR: Delete not supported");
            return Task.FromResult(false);
        }

        public Task<WorklistItem?> GetByIdAsync(string id, CancellationToken ct = default)
        {
            _logger.LogWarning("FHIR: GetById not supported");
            return Task.FromResult<WorklistItem?>(null);
        }

        public void Dispose() => _http.Dispose();
    }
}