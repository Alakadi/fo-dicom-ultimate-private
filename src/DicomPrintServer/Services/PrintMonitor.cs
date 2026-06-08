using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DicomPrintServer.Services
{
    /// <summary>
    /// M5: مراقبة الطباعة وإحصاءاتها وتقاريرها.
    ///
    /// يتتبع:
    ///   - عدد مهام الطباعة (إجمالي / ناجحة / فاشلة)
    ///   - إحصاءات لكل منفذ (AET)
    ///   - إحصاءات يومية وشهرية
    ///   - آخر N مهمة (سجل قابل للاستعلام)
    ///   - تصدير تقرير JSON / نص عادي
    ///
    /// thread-safe بالكامل (ConcurrentDictionary + Interlocked).
    /// </summary>
    public class PrintMonitor
    {
        private readonly ILogger<PrintMonitor> _logger;

        // ─── عدادات عامة ─────────────────────────────────────────────────────
        private long _totalJobsReceived;
        private long _totalJobsSuccess;
        private long _totalJobsFailed;
        private long _totalPagesSuccess;
        private long _totalPagesFailed;

        // ─── إحصاءات لكل AET ─────────────────────────────────────────────────
        private readonly ConcurrentDictionary<string, PortStats> _portStats = new();

        // ─── إحصاءات يومية (key = "yyyy-MM-dd") ─────────────────────────────
        private readonly ConcurrentDictionary<string, DailyStats> _dailyStats = new();

        // ─── سجل آخر N مهمة ──────────────────────────────────────────────────
        private readonly ConcurrentQueue<PrintJobRecord> _recentJobs = new();
        private const int MaxRecentJobs = 500;

        // ─── وقت البدء ───────────────────────────────────────────────────────
        public DateTime StartedAt { get; } = DateTime.UtcNow;

        public PrintMonitor(ILogger<PrintMonitor> logger)
        {
            _logger = logger;
        }

        // ══════════════════════════════════════════════════════════════════════
        // تسجيل الأحداث
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>يُسجَّل عند استقبال مهمة طباعة جديدة.</summary>
        public void RecordJobReceived(string aet, string jobId, string? patientName = null)
        {
            Interlocked.Increment(ref _totalJobsReceived);
            GetPortStats(aet).IncrementReceived();
            GetDailyStats().IncrementReceived();

            _logger.LogDebug("[Monitor] Job received — AET={AET} JobId={JobId}", aet, jobId);
        }

        /// <summary>يُسجَّل عند نجاح مهمة الطباعة.</summary>
        public void RecordJobSuccess(
            string aet,
            string jobId,
            int pageCount,
            TimeSpan duration,
            string? patientName = null,
            string? outputPath  = null)
        {
            Interlocked.Increment(ref _totalJobsSuccess);
            Interlocked.Add(ref _totalPagesSuccess, pageCount);
            GetPortStats(aet).IncrementSuccess(pageCount, duration);
            GetDailyStats().IncrementSuccess(pageCount);

            var record = new PrintJobRecord
            {
                JobId       = jobId,
                AET         = aet,
                PatientName = patientName,
                PageCount   = pageCount,
                Duration    = duration,
                OutputPath  = outputPath,
                Timestamp   = DateTime.UtcNow,
                Success     = true
            };
            EnqueueRecord(record);

            _logger.LogInformation(
                "[Monitor] ✅ Job success — AET={AET} Pages={Pages} Duration={Duration:N0}ms",
                aet, pageCount, duration.TotalMilliseconds);
        }

        /// <summary>يُسجَّل عند فشل مهمة الطباعة.</summary>
        public void RecordJobFailure(
            string aet,
            string jobId,
            string errorMessage,
            int pageCount = 0)
        {
            Interlocked.Increment(ref _totalJobsFailed);
            Interlocked.Add(ref _totalPagesFailed, pageCount);
            GetPortStats(aet).IncrementFailure();
            GetDailyStats().IncrementFailure();

            var record = new PrintJobRecord
            {
                JobId        = jobId,
                AET          = aet,
                PageCount    = pageCount,
                Timestamp    = DateTime.UtcNow,
                Success      = false,
                ErrorMessage = errorMessage
            };
            EnqueueRecord(record);

            _logger.LogWarning(
                "[Monitor] ❌ Job failure — AET={AET} Error={Error}", aet, errorMessage);
        }

        // ══════════════════════════════════════════════════════════════════════
        // استعلامات
        // ══════════════════════════════════════════════════════════════════════

        public GlobalStats GetGlobalStats() => new GlobalStats
        {
            TotalReceived    = Interlocked.Read(ref _totalJobsReceived),
            TotalSuccess     = Interlocked.Read(ref _totalJobsSuccess),
            TotalFailed      = Interlocked.Read(ref _totalJobsFailed),
            TotalPagesOK     = Interlocked.Read(ref _totalPagesSuccess),
            TotalPagesFailed = Interlocked.Read(ref _totalPagesFailed),
            UptimeSeconds    = (long)(DateTime.UtcNow - StartedAt).TotalSeconds
        };

        public IReadOnlyDictionary<string, PortStats> GetAllPortStats()
            => _portStats;

        public IReadOnlyDictionary<string, DailyStats> GetDailyReport()
            => _dailyStats;

        public IReadOnlyList<PrintJobRecord> GetRecentJobs(int count = 50)
            => _recentJobs.TakeLast(Math.Min(count, MaxRecentJobs)).ToList();

        // ══════════════════════════════════════════════════════════════════════
        // تصدير التقارير
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>يُصدّر تقريراً كاملاً بتنسيق JSON.</summary>
        public string ExportReportJson()
        {
            var report = new
            {
                GeneratedAt  = DateTime.UtcNow,
                StartedAt,
                Global       = GetGlobalStats(),
                ByPort       = GetAllPortStats(),
                ByDay        = GetDailyReport(),
                RecentJobs   = GetRecentJobs(100)
            };

            return JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>يُصدّر تقريراً مختصراً كنص عادي.</summary>
        public string ExportReportText()
        {
            var g  = GetGlobalStats();
            var sb = new StringBuilder();

            sb.AppendLine("══════════════════════════════════════");
            sb.AppendLine($"  DICOM Print Server — Status Report");
            sb.AppendLine($"  Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("══════════════════════════════════════");
            sb.AppendLine();
            sb.AppendLine($"Uptime:          {TimeSpan.FromSeconds(g.UptimeSeconds):d\\.hh\\:mm\\:ss}");
            sb.AppendLine($"Jobs Received:   {g.TotalReceived}");
            sb.AppendLine($"Jobs Success:    {g.TotalSuccess}");
            sb.AppendLine($"Jobs Failed:     {g.TotalFailed}");
            sb.AppendLine($"Pages OK:        {g.TotalPagesOK}");
            sb.AppendLine($"Pages Failed:    {g.TotalPagesFailed}");
            sb.AppendLine();
            sb.AppendLine("─── By Port/AET ───────────────────");

            foreach (var (aet, stats) in _portStats.OrderBy(k => k.Key))
            {
                sb.AppendLine($"  {aet,-20} Recv={stats.Received} " +
                              $"OK={stats.Success} Fail={stats.Failed} Pages={stats.TotalPages} " +
                              $"AvgMs={stats.AverageDurationMs:N0}");
            }

            sb.AppendLine();
            sb.AppendLine("─── Last 7 Days ────────────────────");

            var last7 = _dailyStats
                .OrderByDescending(k => k.Key).Take(7).OrderBy(k => k.Key);
            foreach (var (day, stats) in last7)
                sb.AppendLine($"  {day}  Recv={stats.Received} OK={stats.Success} " +
                              $"Fail={stats.Failed} Pages={stats.TotalPages}");

            return sb.ToString();
        }

        /// <summary>يحفظ التقرير إلى ملف.</summary>
        public string SaveReport(string outputFolder, bool json = true)
        {
            Directory.CreateDirectory(outputFolder);
            var ts   = DateTime.Now.ToString("yyyyMMdd_HHmm");
            var ext  = json ? "json" : "txt";
            var path = Path.Combine(outputFolder, $"PrintReport_{ts}.{ext}");
            var text = json ? ExportReportJson() : ExportReportText();
            File.WriteAllText(path, text, Encoding.UTF8);
            _logger.LogInformation("Report saved: {Path}", path);
            return path;
        }

        // ══════════════════════════════════════════════════════════════════════
        // مساعدات
        // ══════════════════════════════════════════════════════════════════════

        private PortStats GetPortStats(string aet)
            => _portStats.GetOrAdd(aet, _ => new PortStats());

        private DailyStats GetDailyStats()
            => _dailyStats.GetOrAdd(DateTime.UtcNow.ToString("yyyy-MM-dd"), _ => new DailyStats());

        private void EnqueueRecord(PrintJobRecord r)
        {
            _recentJobs.Enqueue(r);
            while (_recentJobs.Count > MaxRecentJobs)
                _recentJobs.TryDequeue(out _);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // DTOs
    // ════════════════════════════════════════════════════════════════════════

    public class GlobalStats
    {
        public long TotalReceived    { get; init; }
        public long TotalSuccess     { get; init; }
        public long TotalFailed      { get; init; }
        public long TotalPagesOK     { get; init; }
        public long TotalPagesFailed { get; init; }
        public long UptimeSeconds    { get; init; }
    }

    public class PortStats
    {
        private long _received, _success, _failed, _pages;
        private long _totalDurationMs, _sampleCount;

        public long Received  => Interlocked.Read(ref _received);
        public long Success   => Interlocked.Read(ref _success);
        public long Failed    => Interlocked.Read(ref _failed);
        public long TotalPages => Interlocked.Read(ref _pages);
        public double AverageDurationMs
        {
            get
            {
                long n = Interlocked.Read(ref _sampleCount);
                return n == 0 ? 0 : (double)Interlocked.Read(ref _totalDurationMs) / n;
            }
        }

        internal void IncrementReceived() => Interlocked.Increment(ref _received);
        internal void IncrementFailure()  => Interlocked.Increment(ref _failed);
        internal void IncrementSuccess(int pages, TimeSpan duration)
        {
            Interlocked.Increment(ref _success);
            Interlocked.Add(ref _pages, pages);
            Interlocked.Add(ref _totalDurationMs, (long)duration.TotalMilliseconds);
            Interlocked.Increment(ref _sampleCount);
        }
    }

    public class DailyStats
    {
        private long _received, _success, _failed, _pages;

        public long Received   => Interlocked.Read(ref _received);
        public long Success    => Interlocked.Read(ref _success);
        public long Failed     => Interlocked.Read(ref _failed);
        public long TotalPages => Interlocked.Read(ref _pages);

        internal void IncrementReceived()            => Interlocked.Increment(ref _received);
        internal void IncrementFailure()             => Interlocked.Increment(ref _failed);
        internal void IncrementSuccess(int pages)
        {
            Interlocked.Increment(ref _success);
            Interlocked.Add(ref _pages, pages);
        }
    }

    public class PrintJobRecord
    {
        public string    JobId        { get; init; } = "";
        public string    AET          { get; init; } = "";
        public string?   PatientName  { get; init; }
        public int       PageCount    { get; init; }
        public TimeSpan  Duration     { get; init; }
        public DateTime  Timestamp    { get; init; }
        public bool      Success      { get; init; }
        public string?   ErrorMessage { get; init; }
        public string?   OutputPath   { get; init; }
    }
}
