using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DicomPrintServer.Services
{
    /// <summary>
    /// M5-A: قاعدة بيانات SQLite لتسجيل عمليات الطباعة.
    ///
    /// الجداول:
    ///   PrintOperations  — سجل كامل لكل عملية طباعة
    ///   DailyCounters    — عدادات يومية سريعة لكل AET
    ///
    /// يعمل بجانب PrintMonitor (ذاكرة) — يُضيف استمرارية بين التشغيلات.
    /// </summary>
    public class PrintRepository : IDisposable
    {
        private readonly string             _dbPath;
        private readonly ILogger<PrintRepository> _logger;
        private SqliteConnection?           _conn;
        private bool                        _disposed;

        public PrintRepository(ILogger<PrintRepository> logger, string? dbPath = null)
        {
            _logger = logger;
            _dbPath = dbPath
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "DicomPrintServer",
                    "printlog.db");
        }

        // ══════════════════════════════════════════════════════════════════════
        // التهيئة
        // ══════════════════════════════════════════════════════════════════════

        public void Initialize()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);

            _conn = new SqliteConnection($"Data Source={_dbPath}");
            _conn.Open();

            // Enable WAL mode for concurrent access
            using var walCmd = _conn.CreateCommand();
            walCmd.CommandText = "PRAGMA journal_mode=WAL;";
            walCmd.ExecuteNonQuery();

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS PrintOperations (
                    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp   TEXT    NOT NULL,
                    JobId       TEXT    NOT NULL,
                    AET         TEXT    NOT NULL,
                    PatientId   TEXT,
                    PatientName TEXT,
                    Port        INTEGER,
                    Status      TEXT    NOT NULL,
                    PageCount   INTEGER DEFAULT 0,
                    DurationMs  INTEGER DEFAULT 0,
                    OutputPath  TEXT,
                    ErrorMsg    TEXT
                );

                CREATE INDEX IF NOT EXISTS ix_ops_timestamp ON PrintOperations(Timestamp);
                CREATE INDEX IF NOT EXISTS ix_ops_aet       ON PrintOperations(AET);
                CREATE INDEX IF NOT EXISTS ix_ops_patient   ON PrintOperations(PatientId);

                CREATE TABLE IF NOT EXISTS DailyCounters (
                    Date        TEXT NOT NULL,
                    AET         TEXT NOT NULL,
                    PrintCount  INTEGER DEFAULT 0,
                    FailCount   INTEGER DEFAULT 0,
                    TotalPages  INTEGER DEFAULT 0,
                    PRIMARY KEY (Date, AET)
                );
                """;
            cmd.ExecuteNonQuery();

            _logger.LogInformation("SQLite DB initialized: {Path}", _dbPath);
        }

        // ══════════════════════════════════════════════════════════════════════
        // تسجيل العمليات
        // ══════════════════════════════════════════════════════════════════════

        public void RecordSuccess(
            string jobId, string aet, string? patientId, string? patientName,
            int pageCount, long durationMs, string? outputPath)
        {
            if (_conn == null) return;
            string today = DateTime.UtcNow.ToString("yyyy-MM-dd");

            using var tx = _conn.BeginTransaction();
            try
            {
                using var ins = _conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO PrintOperations
                        (Timestamp, JobId, AET, PatientId, PatientName, Status, PageCount, DurationMs, OutputPath)
                    VALUES
                        ($ts, $jobId, $aet, $pid, $pname, 'SUCCESS', $pages, $dur, $path);
                    """;
                ins.Parameters.AddWithValue("$ts",     DateTime.UtcNow.ToString("o"));
                ins.Parameters.AddWithValue("$jobId",  jobId);
                ins.Parameters.AddWithValue("$aet",    aet);
                ins.Parameters.AddWithValue("$pid",    (object?)patientId ?? DBNull.Value);
                ins.Parameters.AddWithValue("$pname",  (object?)patientName ?? DBNull.Value);
                ins.Parameters.AddWithValue("$pages",  pageCount);
                ins.Parameters.AddWithValue("$dur",    durationMs);
                ins.Parameters.AddWithValue("$path",   (object?)outputPath ?? DBNull.Value);
                ins.ExecuteNonQuery();

                UpsertDailyCounter(tx, today, aet, success: true, pageCount);
                tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();
                _logger.LogWarning(ex, "DB: RecordSuccess failed");
            }
        }

        public void RecordFailure(
            string jobId, string aet, string? errorMessage, int pageCount = 0)
        {
            if (_conn == null) return;
            string today = DateTime.UtcNow.ToString("yyyy-MM-dd");

            using var tx = _conn.BeginTransaction();
            try
            {
                using var ins = _conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO PrintOperations
                        (Timestamp, JobId, AET, Status, PageCount, ErrorMsg)
                    VALUES
                        ($ts, $jobId, $aet, 'FAILURE', $pages, $err);
                    """;
                ins.Parameters.AddWithValue("$ts",    DateTime.UtcNow.ToString("o"));
                ins.Parameters.AddWithValue("$jobId", jobId);
                ins.Parameters.AddWithValue("$aet",   aet);
                ins.Parameters.AddWithValue("$pages", pageCount);
                ins.Parameters.AddWithValue("$err",   (object?)errorMessage ?? DBNull.Value);
                ins.ExecuteNonQuery();

                UpsertDailyCounter(tx, today, aet, success: false, pageCount);
                tx.Commit();
            }
            catch (Exception ex)
            {
                tx.Rollback();
                _logger.LogWarning(ex, "DB: RecordFailure failed");
            }
        }

        private void UpsertDailyCounter(
            SqliteTransaction tx, string date, string aet, bool success, int pages)
        {
            using var upsert = _conn!.CreateCommand();
            upsert.Transaction = tx;

            if (success)
            {
                upsert.CommandText = """
                    INSERT INTO DailyCounters (Date, AET, PrintCount, FailCount, TotalPages)
                    VALUES ($date, $aet, 1, 0, $pages)
                    ON CONFLICT(Date, AET) DO UPDATE SET
                        PrintCount = PrintCount + 1,
                        TotalPages = TotalPages + $pages;
                    """;
            }
            else
            {
                upsert.CommandText = """
                    INSERT INTO DailyCounters (Date, AET, PrintCount, FailCount, TotalPages)
                    VALUES ($date, $aet, 0, 1, 0)
                    ON CONFLICT(Date, AET) DO UPDATE SET
                        FailCount = FailCount + 1;
                    """;
            }
            upsert.Parameters.AddWithValue("$date",  date);
            upsert.Parameters.AddWithValue("$aet",   aet);
            upsert.Parameters.AddWithValue("$pages", pages);
            upsert.ExecuteNonQuery();
        }

        // ══════════════════════════════════════════════════════════════════════
        // استعلامات
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>آخر N عملية (مرتّبة من الأحدث)</summary>
        public IReadOnlyList<DbPrintRecord> GetRecent(int count = 100)
        {
            if (_conn == null) return Array.Empty<DbPrintRecord>();
            var list = new List<DbPrintRecord>();

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, Timestamp, JobId, AET, PatientId, PatientName,
                       Status, PageCount, DurationMs, OutputPath, ErrorMsg
                FROM PrintOperations
                ORDER BY Id DESC
                LIMIT $n;
                """;
            cmd.Parameters.AddWithValue("$n", count);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadRecord(r));
            return list;
        }

        /// <summary>إحصائيات يومية خلال N يوماً الأخيرة</summary>
        public IReadOnlyList<DailyCounterRow> GetDailySummary(int days = 30)
        {
            if (_conn == null) return Array.Empty<DailyCounterRow>();
            var list = new List<DailyCounterRow>();
            string since = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd");

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT Date, AET, PrintCount, FailCount, TotalPages
                FROM DailyCounters
                WHERE Date >= $since
                ORDER BY Date ASC, AET ASC;
                """;
            cmd.Parameters.AddWithValue("$since", since);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new DailyCounterRow
                {
                    Date       = r.GetString(0),
                    AET        = r.GetString(1),
                    PrintCount = r.GetInt32(2),
                    FailCount  = r.GetInt32(3),
                    TotalPages = r.GetInt32(4)
                });
            }
            return list;
        }

        /// <summary>إجماليات عامة</summary>
        public DbGlobalTotals GetTotals()
        {
            if (_conn == null) return new DbGlobalTotals();

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    SUM(CASE WHEN Status='SUCCESS' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN Status='FAILURE' THEN 1 ELSE 0 END),
                    SUM(PageCount),
                    COUNT(*)
                FROM PrintOperations;
                """;
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return new DbGlobalTotals();

            return new DbGlobalTotals
            {
                TotalSuccess   = r.IsDBNull(0) ? 0 : r.GetInt64(0),
                TotalFailed    = r.IsDBNull(1) ? 0 : r.GetInt64(1),
                TotalPages     = r.IsDBNull(2) ? 0 : r.GetInt64(2),
                TotalOperations= r.IsDBNull(3) ? 0 : r.GetInt64(3)
            };
        }

        /// <summary>عدد العمليات لـ AET معين خلال اليوم الحالي</summary>
        public int GetTodayCount(string aet)
        {
            if (_conn == null) return 0;
            string today = DateTime.UtcNow.ToString("yyyy-MM-dd");

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT COALESCE(PrintCount, 0)
                FROM DailyCounters
                WHERE Date = $date AND AET = $aet;
                """;
            cmd.Parameters.AddWithValue("$date", today);
            cmd.Parameters.AddWithValue("$aet",  aet);
            var result = cmd.ExecuteScalar();
            return result is long l ? (int)l : 0;
        }

        /// <summary>تصدير CSV لآخر N عملية</summary>
        public string ExportCsv(int count = 500)
        {
            var records = GetRecent(count);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Id,Timestamp,JobId,AET,PatientId,PatientName,Status,PageCount,DurationMs,OutputPath,ErrorMsg");

            foreach (var r in records)
            {
                sb.AppendLine(string.Join(",",
                    r.Id,
                    CsvEsc(r.Timestamp),
                    CsvEsc(r.JobId),
                    CsvEsc(r.AET),
                    CsvEsc(r.PatientId ?? ""),
                    CsvEsc(r.PatientName ?? ""),
                    CsvEsc(r.Status),
                    r.PageCount,
                    r.DurationMs,
                    CsvEsc(r.OutputPath ?? ""),
                    CsvEsc(r.ErrorMsg ?? "")));
            }
            return sb.ToString();
        }

        private static string CsvEsc(string s)
        {
            if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private static DbPrintRecord ReadRecord(SqliteDataReader r) => new()
        {
            Id          = r.GetInt64(0),
            Timestamp   = r.GetString(1),
            JobId       = r.GetString(2),
            AET         = r.GetString(3),
            PatientId   = r.IsDBNull(4) ? null : r.GetString(4),
            PatientName = r.IsDBNull(5) ? null : r.GetString(5),
            Status      = r.GetString(6),
            PageCount   = r.GetInt32(7),
            DurationMs  = r.GetInt64(8),
            OutputPath  = r.IsDBNull(9) ? null : r.GetString(9),
            ErrorMsg    = r.IsDBNull(10) ? null : r.GetString(10)
        };

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _conn?.Dispose();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // DTOs
    // ──────────────────────────────────────────────────────────────────────────

    public class DbPrintRecord
    {
        public long    Id          { get; set; }
        public string  Timestamp   { get; set; } = "";
        public string  JobId       { get; set; } = "";
        public string  AET         { get; set; } = "";
        public string? PatientId   { get; set; }
        public string? PatientName { get; set; }
        public string  Status      { get; set; } = "";
        public int     PageCount   { get; set; }
        public long    DurationMs  { get; set; }
        public string? OutputPath  { get; set; }
        public string? ErrorMsg    { get; set; }
    }

    public class DailyCounterRow
    {
        public string Date       { get; set; } = "";
        public string AET        { get; set; } = "";
        public int    PrintCount { get; set; }
        public int    FailCount  { get; set; }
        public int    TotalPages { get; set; }
    }

    public class DbGlobalTotals
    {
        public long TotalSuccess    { get; set; }
        public long TotalFailed     { get; set; }
        public long TotalPages      { get; set; }
        public long TotalOperations { get; set; }
    }
}
