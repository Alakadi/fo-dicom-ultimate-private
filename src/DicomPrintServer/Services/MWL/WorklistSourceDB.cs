using DicomPrintServer.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DicomPrintServer.Services.MWL
{
    /// <summary>
    /// مصدر قائمة العمل من SQLite — يخزن العناصر في قاعدة بيانات محلية.
    ///
    /// الجدول: WorklistItems
    ///   - كل عنصر يمثل فحصاً مجدولاً (Scheduled Procedure Step)
    ///   - يدعم البحث بالاسم والرقم والتاريخ والجهاز
    ///   - متوافق مع DICOM Modality Worklist Information Model
    /// </summary>
    public class WorklistSourceDB : IWorklistSource
    {
        private readonly ILogger<WorklistSourceDB> _logger;
        private readonly string _dbPath;
        private SqliteConnection? _conn;

        public string SourceName => "Database";

        public WorklistSourceDB(ILogger<WorklistSourceDB> logger, string? dbPath = null)
        {
            _logger = logger;
            _dbPath = dbPath
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "DicomPrintServer",
                    "mwl.db");
        }

        public Task InitializeAsync(CancellationToken ct = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);

            _conn = new SqliteConnection($"Data Source={_dbPath}");
            _conn.Open();

            using var walCmd = _conn.CreateCommand();
            walCmd.CommandText = "PRAGMA journal_mode=WAL;";
            walCmd.ExecuteNonQuery();

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS WorklistItems (
                    Id                              INTEGER PRIMARY KEY AUTOINCREMENT,
                    ScheduledProcedureStepID        TEXT NOT NULL UNIQUE,
                    ScheduledStationAET             TEXT,
                    ScheduledProcedureStepStartDate TEXT,
                    ScheduledProcedureStepStartTime TEXT,
                    ScheduledProcedureStepEndDate   TEXT,
                    ScheduledProcedureStepEndTime   TEXT,
                    ScheduledPerformingPhysicianName TEXT,
                    ScheduledProcedureStepDescription TEXT,
                    ScheduledStationName            TEXT,
                    ScheduledProcedureStepLocation   TEXT,
                    ScheduledProcedureStepStatus     TEXT DEFAULT 'SCHEDULED',
                    RequestedProcedurePriority       TEXT,
                    PatientTransportArrangements     TEXT,
                    RequestedProcedureID             TEXT,
                    RequestedProcedureDescription    TEXT,
                    AccessionNumber                  TEXT,
                    ReferringPhysicianName           TEXT,
                    RequestingPhysician              TEXT,
                    RequestingService                TEXT,
                    ImagingServiceRequestComments    TEXT,
                    StudyInstanceUID                 TEXT,
                    PatientName                      TEXT,
                    PatientID                        TEXT,
                    IssuerOfPatientID                TEXT,
                    OtherPatientIDs                  TEXT,
                    PatientBirthDate                 TEXT,
                    PatientSex                       TEXT,
                    PatientWeight                    TEXT,
                    ConfidentialityConstraint        TEXT,
                    PatientAddress                   TEXT,
                    PatientTelephoneNumbers          TEXT,
                    PatientComments                  TEXT,
                    SourceAET                        TEXT,
                    CreatedAt                        TEXT NOT NULL,
                    UpdatedAt                        TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_wli_patient_id    ON WorklistItems(PatientID);
                CREATE INDEX IF NOT EXISTS ix_wli_patient_name  ON WorklistItems(PatientName);
                CREATE INDEX IF NOT EXISTS ix_wli_station_aet   ON WorklistItems(ScheduledStationAET);
                CREATE INDEX IF NOT EXISTS ix_wli_start_date    ON WorklistItems(ScheduledProcedureStepStartDate);
                CREATE INDEX IF NOT EXISTS ix_wli_status        ON WorklistItems(ScheduledProcedureStepStatus);
                CREATE INDEX IF NOT EXISTS ix_wli_accession     ON WorklistItems(AccessionNumber);
                CREATE INDEX IF NOT EXISTS ix_wli_requested_id  ON WorklistItems(RequestedProcedureID);
                """;
            cmd.ExecuteNonQuery();

            _logger.LogInformation("MWL DB initialized: {Path}", _dbPath);
            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<WorklistItem>> FindAsync(
            MWLQueryCriteria criteria,
            int maxResults,
            CancellationToken ct = default)
        {
            if (_conn == null)
            {
                await InitializeAsync(ct);
            }

            var items = new List<WorklistItem>();
            var conditions = new List<string>();
            var parameters = new Dictionary<string, string?>();

            // Patient Name (wildcard support)
            if (!string.IsNullOrEmpty(criteria.PatientName))
            {
                if (criteria.PatientName.Contains('*') || criteria.PatientName.Contains('?'))
                {
                    string pattern = criteria.PatientName.Replace('*', '%').Replace('?', '_');
                    conditions.Add("UPPER(PatientName) LIKE UPPER($pname)");
                    parameters["$pname"] = pattern;
                }
                else
                {
                    conditions.Add("UPPER(PatientName) LIKE UPPER($pname)");
                    parameters["$pname"] = $"%{criteria.PatientName}%";
                }
            }

            // Patient ID
            if (!string.IsNullOrEmpty(criteria.PatientID))
            {
                conditions.Add("PatientID = $pid");
                parameters["$pid"] = criteria.PatientID;
            }

            // Scheduled Station AET
            if (!string.IsNullOrEmpty(criteria.ScheduledStationAET))
            {
                conditions.Add("ScheduledStationAET = $station");
                parameters["$station"] = criteria.ScheduledStationAET;
            }

            // Scheduled Procedure Step Start Date (range)
            if (!string.IsNullOrEmpty(criteria.ScheduledProcedureStepStartDate))
            {
                conditions.Add("ScheduledProcedureStepStartDate >= $startDate");
                parameters["$startDate"] = criteria.ScheduledProcedureStepStartDate;
            }

            if (!string.IsNullOrEmpty(criteria.ScheduledProcedureStepEndDate))
            {
                conditions.Add("ScheduledProcedureStepEndDate <= $endDate");
                parameters["$endDate"] = criteria.ScheduledProcedureStepEndDate;
            }

            // Modality (extracted from description or stored separately)
            if (!string.IsNullOrEmpty(criteria.Modality))
            {
                conditions.Add("ScheduledProcedureStepDescription LIKE '%' || $mod || '%'");
                parameters["$mod"] = criteria.Modality;
            }

            // Accession Number
            if (!string.IsNullOrEmpty(criteria.AccessionNumber))
            {
                conditions.Add("AccessionNumber = $accession");
                parameters["$accession"] = criteria.AccessionNumber;
            }

            // Requested Procedure ID
            if (!string.IsNullOrEmpty(criteria.RequestedProcedureID))
            {
                conditions.Add("RequestedProcedureID = $reqProcId");
                parameters["$reqProcId"] = criteria.RequestedProcedureID;
            }

            // Study Instance UID
            if (!string.IsNullOrEmpty(criteria.StudyInstanceUID))
            {
                conditions.Add("StudyInstanceUID = $studyUid");
                parameters["$studyUid"] = criteria.StudyInstanceUID;
            }

            // Scheduled Procedure Step Status
            if (!string.IsNullOrEmpty(criteria.ScheduledProcedureStepStatus))
            {
                conditions.Add("ScheduledProcedureStepStatus = $stepStatus");
                parameters["$stepStatus"] = criteria.ScheduledProcedureStepStatus;
            }

            // Scheduled Performing Physician Name
            if (!string.IsNullOrEmpty(criteria.ScheduledPerformingPhysicianName))
            {
                conditions.Add("ScheduledPerformingPhysicianName LIKE '%' || $physician || '%'");
                parameters["$physician"] = criteria.ScheduledPerformingPhysicianName;
            }

            // Default: exclude completed/canceled unless explicitly requested
            if (string.IsNullOrEmpty(criteria.ScheduledProcedureStepStatus))
            {
                conditions.Add("ScheduledProcedureStepStatus NOT IN ('COMPLETED', 'CANCELED', 'DISCONTINUED')");
            }

            string whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
            string sql = $"""
                SELECT * FROM WorklistItems
                {whereClause}
                ORDER BY ScheduledProcedureStepStartDate ASC, ScheduledProcedureStepStartTime ASC
                LIMIT $limit;
                """;

            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$limit", maxResults);
            foreach (var (key, value) in parameters)
            {
                cmd.Parameters.AddWithValue(key, (object?)value ?? DBNull.Value);
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadItem(reader));
            }

            return items;
        }

        public async Task<bool> UpsertAsync(WorklistItem item, CancellationToken ct = default)
        {
            if (_conn == null)
                await InitializeAsync(ct);

            string now = DateTime.UtcNow.ToString("o");

            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO WorklistItems (
                    ScheduledProcedureStepID, ScheduledStationAET,
                    ScheduledProcedureStepStartDate, ScheduledProcedureStepStartTime,
                    ScheduledProcedureStepEndDate, ScheduledProcedureStepEndTime,
                    ScheduledPerformingPhysicianName, ScheduledProcedureStepDescription,
                    ScheduledStationName, ScheduledProcedureStepLocation,
                    ScheduledProcedureStepStatus, RequestedProcedurePriority,
                    PatientTransportArrangements,
                    RequestedProcedureID, RequestedProcedureDescription,
                    AccessionNumber, ReferringPhysicianName,
                    RequestingPhysician, RequestingService,
                    ImagingServiceRequestComments, StudyInstanceUID,
                    PatientName, PatientID, IssuerOfPatientID, OtherPatientIDs,
                    PatientBirthDate, PatientSex, PatientWeight,
                    ConfidentialityConstraint, PatientAddress,
                    PatientTelephoneNumbers, PatientComments,
                    SourceAET, CreatedAt, UpdatedAt
                ) VALUES (
                    $stepId, $stationAet,
                    $startDate, $startTime,
                    $endDate, $endTime,
                    $physician, $description,
                    $stationName, $location,
                    $status, $priority,
                    $transport,
                    $reqProcId, $reqProcDesc,
                    $accession, $referring,
                    $requesting, $requestingService,
                    $comments, $studyUid,
                    $patientName, $patientId, $issuerId, $otherIds,
                    $dob, $sex, $weight,
                    $confidentiality, $address,
                    $phone, $comments2,
                    $sourceAet, $createdAt, $updatedAt
                )
                ON CONFLICT(ScheduledProcedureStepID) DO UPDATE SET
                    ScheduledStationAET = $stationAet,
                    ScheduledProcedureStepStartDate = $startDate,
                    ScheduledProcedureStepStartTime = $startTime,
                    ScheduledProcedureStepEndDate = $endDate,
                    ScheduledProcedureStepEndTime = $endTime,
                    ScheduledPerformingPhysicianName = $physician,
                    ScheduledProcedureStepDescription = $description,
                    ScheduledStationName = $stationName,
                    ScheduledProcedureStepLocation = $location,
                    ScheduledProcedureStepStatus = $status,
                    RequestedProcedurePriority = $priority,
                    PatientTransportArrangements = $transport,
                    RequestedProcedureID = $reqProcId,
                    RequestedProcedureDescription = $reqProcDesc,
                    AccessionNumber = $accession,
                    ReferringPhysicianName = $referring,
                    RequestingPhysician = $requesting,
                    RequestingService = $requestingService,
                    ImagingServiceRequestComments = $comments,
                    StudyInstanceUID = $studyUid,
                    PatientName = $patientName,
                    PatientID = $patientId,
                    IssuerOfPatientID = $issuerId,
                    OtherPatientIDs = $otherIds,
                    PatientBirthDate = $dob,
                    PatientSex = $sex,
                    PatientWeight = $weight,
                    ConfidentialityConstraint = $confidentiality,
                    PatientAddress = $address,
                    PatientTelephoneNumbers = $phone,
                    PatientComments = $comments2,
                    SourceAET = $sourceAet,
                    UpdatedAt = $updatedAt;
                """;

            cmd.Parameters.AddWithValue("$stepId", item.ScheduledProcedureStepID);
            cmd.Parameters.AddWithValue("$stationAet", (object?)item.ScheduledStationAET ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$startDate", (object?)item.ScheduledProcedureStepStartDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$startTime", (object?)item.ScheduledProcedureStepStartTime ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$endDate", (object?)item.ScheduledProcedureStepEndDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$endTime", (object?)item.ScheduledProcedureStepEndTime ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$physician", (object?)item.ScheduledPerformingPhysicianName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$description", (object?)item.ScheduledProcedureStepDescription ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$stationName", (object?)item.ScheduledStationName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$location", (object?)item.ScheduledProcedureStepLocation ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status", (object?)item.ScheduledProcedureStepStatus ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$priority", (object?)item.RequestedProcedurePriority ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$transport", (object?)item.PatientTransportArrangements ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$reqProcId", (object?)item.RequestedProcedureID ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$reqProcDesc", (object?)item.RequestedProcedureDescription ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$accession", (object?)item.AccessionNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$referring", (object?)item.ReferringPhysicianName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$requesting", (object?)item.RequestingPhysician ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$requestingService", (object?)item.RequestingService ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$comments", (object?)item.ImagingServiceRequestComments ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$studyUid", (object?)item.StudyInstanceUID ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$patientName", (object?)item.PatientName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$patientId", (object?)item.PatientID ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$issuerId", (object?)item.IssuerOfPatientID ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$otherIds", (object?)item.OtherPatientIDs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$dob", (object?)item.PatientBirthDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sex", (object?)item.PatientSex ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$weight", (object?)item.PatientWeight ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$confidentiality", (object?)item.ConfidentialityConstraint ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$address", (object?)item.PatientAddress ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$phone", (object?)item.PatientTelephoneNumbers ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$comments2", (object?)item.PatientComments ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sourceAet", (object?)item.SourceAET ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$createdAt", item.CreatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("$updatedAt", now);

            int rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows > 0;
        }

        public async Task<bool> DeleteAsync(string scheduledProcedureStepID, CancellationToken ct = default)
        {
            if (_conn == null)
                await InitializeAsync(ct);

            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "DELETE FROM WorklistItems WHERE ScheduledProcedureStepID = $stepId;";
            cmd.Parameters.AddWithValue("$stepId", scheduledProcedureStepID);

            int rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows > 0;
        }

        public async Task<WorklistItem?> GetByIdAsync(string scheduledProcedureStepID, CancellationToken ct = default)
        {
            if (_conn == null)
                await InitializeAsync(ct);

            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "SELECT * FROM WorklistItems WHERE ScheduledProcedureStepID = $stepId;";
            cmd.Parameters.AddWithValue("$stepId", scheduledProcedureStepID);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                return ReadItem(reader);

            return null;
        }

        private static WorklistItem ReadItem(SqliteDataReader r) => new()
        {
            ScheduledProcedureStepID = r.IsDBNull(r.GetOrdinal("ScheduledProcedureStepID")) ? "" : r.GetString(r.GetOrdinal("ScheduledProcedureStepID")),
            ScheduledStationAET = r.IsDBNull(r.GetOrdinal("ScheduledStationAET")) ? "" : r.GetString(r.GetOrdinal("ScheduledStationAET")),
            ScheduledProcedureStepStartDate = r.IsDBNull(r.GetOrdinal("ScheduledProcedureStepStartDate")) ? "" : r.GetString(r.GetOrdinal("ScheduledProcedureStepStartDate")),
            ScheduledProcedureStepStartTime = r.IsDBNull(r.GetOrdinal("ScheduledProcedureStepStartTime")) ? "" : r.GetString(r.GetOrdinal("ScheduledProcedureStepStartTime")),
            ScheduledProcedureStepEndDate = r.IsDBNull(r.GetOrdinal("ScheduledProcedureStepEndDate")) ? "" : r.GetString(r.GetOrdinal("ScheduledProcedureStepEndDate")),
            ScheduledProcedureStepEndTime = r.IsDBNull(r.GetOrdinal("ScheduledProcedureStepEndTime")) ? "" : r.GetString(r.GetOrdinal("ScheduledProcedureStepEndTime")),
            ScheduledPerformingPhysicianName = r.IsDBNull(r.GetOrdinal("ScheduledPerformingPhysicianName")) ? "" : r.GetString(r.GetOrdinal("ScheduledPerformingPhysicianName")),
            ScheduledProcedureStepDescription = r.IsDBNull(r.GetOrdinal("ScheduledProcedureStepDescription")) ? "" : r.GetString(r.GetOrdinal("ScheduledProcedureStepDescription")),
            ScheduledStationName = r.IsDBNull(r.GetOrdinal("ScheduledStationName")) ? "" : r.GetString(r.GetOrdinal("ScheduledStationName")),
            ScheduledProcedureStepLocation = r.IsDBNull(r.GetOrdinal("ScheduledProcedureStepLocation")) ? "" : r.GetString(r.GetOrdinal("ScheduledProcedureStepLocation")),
            ScheduledProcedureStepStatus = r.IsDBNull(r.GetOrdinal("ScheduledProcedureStepStatus")) ? "" : r.GetString(r.GetOrdinal("ScheduledProcedureStepStatus")),
            RequestedProcedurePriority = r.IsDBNull(r.GetOrdinal("RequestedProcedurePriority")) ? "" : r.GetString(r.GetOrdinal("RequestedProcedurePriority")),
            PatientTransportArrangements = r.IsDBNull(r.GetOrdinal("PatientTransportArrangements")) ? "" : r.GetString(r.GetOrdinal("PatientTransportArrangements")),
            RequestedProcedureID = r.IsDBNull(r.GetOrdinal("RequestedProcedureID")) ? "" : r.GetString(r.GetOrdinal("RequestedProcedureID")),
            RequestedProcedureDescription = r.IsDBNull(r.GetOrdinal("RequestedProcedureDescription")) ? "" : r.GetString(r.GetOrdinal("RequestedProcedureDescription")),
            AccessionNumber = r.IsDBNull(r.GetOrdinal("AccessionNumber")) ? "" : r.GetString(r.GetOrdinal("AccessionNumber")),
            ReferringPhysicianName = r.IsDBNull(r.GetOrdinal("ReferringPhysicianName")) ? "" : r.GetString(r.GetOrdinal("ReferringPhysicianName")),
            RequestingPhysician = r.IsDBNull(r.GetOrdinal("RequestingPhysician")) ? "" : r.GetString(r.GetOrdinal("RequestingPhysician")),
            RequestingService = r.IsDBNull(r.GetOrdinal("RequestingService")) ? "" : r.GetString(r.GetOrdinal("RequestingService")),
            ImagingServiceRequestComments = r.IsDBNull(r.GetOrdinal("ImagingServiceRequestComments")) ? "" : r.GetString(r.GetOrdinal("ImagingServiceRequestComments")),
            StudyInstanceUID = r.IsDBNull(r.GetOrdinal("StudyInstanceUID")) ? "" : r.GetString(r.GetOrdinal("StudyInstanceUID")),
            PatientName = r.IsDBNull(r.GetOrdinal("PatientName")) ? "" : r.GetString(r.GetOrdinal("PatientName")),
            PatientID = r.IsDBNull(r.GetOrdinal("PatientID")) ? "" : r.GetString(r.GetOrdinal("PatientID")),
            IssuerOfPatientID = r.IsDBNull(r.GetOrdinal("IssuerOfPatientID")) ? "" : r.GetString(r.GetOrdinal("IssuerOfPatientID")),
            OtherPatientIDs = r.IsDBNull(r.GetOrdinal("OtherPatientIDs")) ? "" : r.GetString(r.GetOrdinal("OtherPatientIDs")),
            PatientBirthDate = r.IsDBNull(r.GetOrdinal("PatientBirthDate")) ? "" : r.GetString(r.GetOrdinal("PatientBirthDate")),
            PatientSex = r.IsDBNull(r.GetOrdinal("PatientSex")) ? "" : r.GetString(r.GetOrdinal("PatientSex")),
            PatientWeight = r.IsDBNull(r.GetOrdinal("PatientWeight")) ? "" : r.GetString(r.GetOrdinal("PatientWeight")),
            ConfidentialityConstraint = r.IsDBNull(r.GetOrdinal("ConfidentialityConstraint")) ? "" : r.GetString(r.GetOrdinal("ConfidentialityConstraint")),
            PatientAddress = r.IsDBNull(r.GetOrdinal("PatientAddress")) ? "" : r.GetString(r.GetOrdinal("PatientAddress")),
            PatientTelephoneNumbers = r.IsDBNull(r.GetOrdinal("PatientTelephoneNumbers")) ? "" : r.GetString(r.GetOrdinal("PatientTelephoneNumbers")),
            PatientComments = r.IsDBNull(r.GetOrdinal("PatientComments")) ? "" : r.GetString(r.GetOrdinal("PatientComments")),
            SourceAET = r.IsDBNull(r.GetOrdinal("SourceAET")) ? "" : r.GetString(r.GetOrdinal("SourceAET")),
            CreatedAt = DateTime.TryParse(r.IsDBNull(r.GetOrdinal("CreatedAt")) ? null : r.GetString(r.GetOrdinal("CreatedAt")), out var c) ? c : DateTime.UtcNow,
            UpdatedAt = DateTime.TryParse(r.IsDBNull(r.GetOrdinal("UpdatedAt")) ? null : r.GetString(r.GetOrdinal("UpdatedAt")), out var u) ? u : DateTime.UtcNow,
        };

        public void Dispose()
        {
            _conn?.Dispose();
        }
    }
}