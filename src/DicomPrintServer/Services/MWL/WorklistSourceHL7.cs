using System.Net.Sockets;
using System.Text;
using DicomPrintServer.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DicomPrintServer.Services.MWL
{
    public class WorklistSourceHL7 : IWorklistSource
    {
        private readonly ILogger<WorklistSourceHL7> _logger;
        private readonly HisRisConfig _config;

        public string SourceName => "HL7v2";

        public WorklistSourceHL7(ILogger<WorklistSourceHL7> logger, IOptions<PrintServerConfig> options)
        {
            _logger = logger;
            _config = options.Value.HisRis;
        }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async Task<IReadOnlyList<WorklistItem>> FindAsync(
            MWLQueryCriteria criteria, int maxResults, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_config.Hl7Host))
            {
                _logger.LogWarning("HL7: Host not configured");
                return Array.Empty<WorklistItem>();
            }

            string msgId = Guid.NewGuid().ToString("N")[..20].ToUpper();
            string queryId = Guid.NewGuid().ToString("N")[..20].ToUpper();
            string now = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            string app = _config.Hl7SendingApp;
            string fac = _config.Hl7SendingFac;

            var qpdFields = new List<string>();
            if (!string.IsNullOrEmpty(criteria.PatientID))
                qpdFields.Add($"@PID.3.1^{criteria.PatientID}");
            if (!string.IsNullOrEmpty(criteria.PatientName))
                qpdFields.Add($"@PID.5^{criteria.PatientName}");
            if (!string.IsNullOrEmpty(criteria.ScheduledStationAET))
                qpdFields.Add($"@PV1.18^{criteria.ScheduledStationAET}");

            string qpd = string.Join("\\", qpdFields);

            string hl7Message =
                $"MSH|^~\\&|{app}|{fac}|MWL|MWL|{now}||QBP^Q21^QBP_Q21|{msgId}|P|2.5\r" +
                $"QPD|IHE Scheduled Worklist Query|{queryId}|{qpd}\r" +
                $"RCP|I|{maxResults}^RD\r";

            _logger.LogDebug("HL7 MWL query → {Host}:{Port}", _config.Hl7Host, _config.Hl7Port);

            string response = await SendMllpAsync(hl7Message, ct);
            if (string.IsNullOrEmpty(response))
                return Array.Empty<WorklistItem>();

            return ParseHl7Response(response);
        }

        private async Task<string> SendMllpAsync(string message, CancellationToken ct)
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_config.Hl7TimeoutSec));

            await client.ConnectAsync(_config.Hl7Host, _config.Hl7Port, cts.Token);
            using var stream = client.GetStream();

            byte[] msgBytes = Encoding.UTF8.GetBytes(message);
            byte[] framed = new byte[msgBytes.Length + 3];
            framed[0] = 0x0B;
            msgBytes.CopyTo(framed, 1);
            framed[^2] = 0x1C;
            framed[^1] = 0x0D;

            await stream.WriteAsync(framed, cts.Token);
            await stream.FlushAsync(cts.Token);

            var buffer = new byte[65536];
            int read = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
            if (read < 3) return "";

            return Encoding.UTF8.GetString(buffer, 1, read - 3);
        }

        private static List<WorklistItem> ParseHl7Response(string response)
        {
            var items = new List<WorklistItem>();
            var segments = response.Split('\r', StringSplitOptions.RemoveEmptyEntries);

            foreach (var seg in segments)
            {
                if (!seg.StartsWith("DFI")) continue;

                var f = seg.Split('|');
                if (f.Length < 10) continue;

                items.Add(new WorklistItem
                {
                    ScheduledProcedureStepID = f.Length > 3 ? f[3].Split('^')[0] : "",
                    PatientName = f.Length > 5 ? f[5].Replace('^', ' ') : "",
                    PatientID = f.Length > 3 ? f[3].Split('^')[0] : "",
                    PatientSex = f.Length > 8 ? f[8] : "",
                    ScheduledStationAET = f.Length > 15 ? f[15].Split('^')[0] : "",
                    ScheduledProcedureStepStartDate = f.Length > 11 ? f[11] : "",
                    ScheduledProcedureStepStatus = "SCHEDULED"
                });
            }

            return items;
        }

        public Task<bool> UpsertAsync(WorklistItem item, CancellationToken ct = default)
        {
            _logger.LogWarning("HL7: Upsert not supported");
            return Task.FromResult(false);
        }

        public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
        {
            _logger.LogWarning("HL7: Delete not supported");
            return Task.FromResult(false);
        }

        public Task<WorklistItem?> GetByIdAsync(string id, CancellationToken ct = default)
        {
            _logger.LogWarning("HL7: GetById not supported");
            return Task.FromResult<WorklistItem?>(null);
        }
    }
}