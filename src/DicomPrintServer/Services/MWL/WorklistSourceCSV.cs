using DicomPrintServer.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DicomPrintServer.Services.MWL
{
    public class WorklistSourceCSV : IWorklistSource
    {
        private readonly ILogger<WorklistSourceCSV> _logger;
        private readonly HisRisConfig _config;
        private List<WorklistItem> _items = new();
        private bool _loaded;

        public string SourceName => "CSV";

        public WorklistSourceCSV(ILogger<WorklistSourceCSV> logger, IOptions<PrintServerConfig> options)
        {
            _logger = logger;
            _config = options.Value.HisRis;
        }

        public Task InitializeAsync(CancellationToken ct = default)
        {
            LoadCsv();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorklistItem>> FindAsync(
            MWLQueryCriteria criteria, int maxResults, CancellationToken ct = default)
        {
            if (!_loaded) LoadCsv();

            var query = _items.AsEnumerable();

            if (!string.IsNullOrEmpty(criteria.PatientID))
                query = query.Where(i => i.PatientID == criteria.PatientID);

            if (!string.IsNullOrEmpty(criteria.PatientName))
            {
                if (criteria.PatientName.Contains('*'))
                {
                    string pattern = criteria.PatientName.Replace("*", "");
                    query = query.Where(i => i.PatientName.Contains(pattern, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    query = query.Where(i => i.PatientName.Contains(criteria.PatientName, StringComparison.OrdinalIgnoreCase));
                }
            }

            if (!string.IsNullOrEmpty(criteria.ScheduledStationAET))
                query = query.Where(i => i.ScheduledStationAET == criteria.ScheduledStationAET);

            if (!string.IsNullOrEmpty(criteria.AccessionNumber))
                query = query.Where(i => i.AccessionNumber == criteria.AccessionNumber);

            if (!string.IsNullOrEmpty(criteria.ScheduledProcedureStepStartDate))
                query = query.Where(i => i.ScheduledProcedureStepStartDate == criteria.ScheduledProcedureStepStartDate);

            if (!string.IsNullOrEmpty(criteria.ScheduledProcedureStepStatus))
                query = query.Where(i => i.ScheduledProcedureStepStatus == criteria.ScheduledProcedureStepStatus);

            return Task.FromResult<IReadOnlyList<WorklistItem>>(
                query.Take(maxResults).ToList());
        }

        public Task<bool> UpsertAsync(WorklistItem item, CancellationToken ct = default)
        {
            var existing = _items.FindIndex(i => i.ScheduledProcedureStepID == item.ScheduledProcedureStepID);
            if (existing >= 0)
                _items[existing] = item;
            else
                _items.Add(item);
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
        {
            return Task.FromResult(_items.RemoveAll(i => i.ScheduledProcedureStepID == id) > 0);
        }

        public Task<WorklistItem?> GetByIdAsync(string id, CancellationToken ct = default)
        {
            return Task.FromResult(_items.Find(i => i.ScheduledProcedureStepID == id));
        }

        private void LoadCsv()
        {
            _items.Clear();
            _loaded = true;

            if (string.IsNullOrEmpty(_config.CsvFilePath) || !File.Exists(_config.CsvFilePath))
            {
                _logger.LogWarning("MWL CSV: File not found: {Path}", _config.CsvFilePath);
                return;
            }

            int count = 0;
            foreach (var line in File.ReadLines(_config.CsvFilePath).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var f = line.Split(',');
                if (f.Length < 6) continue;

                _items.Add(new WorklistItem
                {
                    PatientID = f[0].Trim(),
                    PatientName = f.Length > 1 ? f[1].Trim() : "",
                    PatientSex = f.Length > 2 ? f[2].Trim() : "",
                    ScheduledStationAET = f.Length > 3 ? f[3].Trim() : "",
                    ScheduledProcedureStepStartDate = f.Length > 4 ? f[4].Trim() : "",
                    ScheduledProcedureStepDescription = f.Length > 5 ? f[5].Trim() : "",
                    ScheduledProcedureStepStatus = f.Length > 6 ? f[6].Trim() : "SCHEDULED",
                    AccessionNumber = f.Length > 7 ? f[7].Trim() : "",
                    RequestedProcedureID = f.Length > 8 ? f[8].Trim() : "",
                    ScheduledProcedureStepID = f.Length > 9 ? f[9].Trim() : $"CSV_{count:D6}",
                    SourceAET = "CSV"
                });
                count++;
            }

            _logger.LogInformation("MWL CSV loaded: {Count} items from {Path}", count, _config.CsvFilePath);
        }
    }
}