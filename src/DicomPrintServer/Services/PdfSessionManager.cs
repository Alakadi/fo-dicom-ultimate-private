using System.Collections.Concurrent;
using DicomPrintServer.Configuration;
using FellowOakDicom;
using FellowOakDicom.Printing;
using Microsoft.Extensions.Logging;

namespace DicomPrintServer.Services
{
    /// <summary>
    /// M3-E: مدير جلسات PDF — يجمع FilmBoxes لنفس المريض في PDF واحد.
    ///
    /// السيناريو:
    ///   • يصل FilmBox للمريض A → تبدأ جلسة لـ A
    ///   • يصل FilmBox آخر لنفس المريض خلال SessionTimeoutMinutes → يُضاف للجلسة نفسها
    ///   • بعد انتهاء المهلة → يُغلق PDF ويُحفظ
    ///
    /// اسم ملف PDF: {PatientName}_{StudyDate}_{StudyID}.pdf
    /// </summary>
    public class PdfSessionManager : IAsyncDisposable
    {
        private readonly PdfExporter                                    _pdfExporter;
        private readonly ILogger<PdfSessionManager>                     _logger;
        private readonly ConcurrentDictionary<string, PdfSession>       _sessions = new();
        private readonly TimeSpan                                       _timeout;
        private readonly Timer                                          _sweepTimer;

        public PdfSessionManager(
            PdfExporter pdfExporter,
            ILogger<PdfSessionManager> logger,
            int sessionTimeoutMinutes = 5)
        {
            _pdfExporter = pdfExporter;
            _logger      = logger;
            _timeout     = TimeSpan.FromMinutes(sessionTimeoutMinutes);

            // كل 60 ثانية: تحقق من الجلسات المنتهية وأغلقها
            _sweepTimer = new Timer(
                callback: _ => SweepExpiredSessions(),
                state:    null,
                dueTime:  TimeSpan.FromSeconds(60),
                period:   TimeSpan.FromSeconds(60));
        }

        // ══════════════════════════════════════════════════════════════════════
        // إضافة FilmBox لجلسة
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// يُضيف FilmBox لجلسة المريض الحالية.
        /// إذا لم تكن جلسة → تُنشئ واحدة.
        /// </summary>
        public void AddFilmBox(
            FilmBox        filmBox,
            ListenerConfig listenerConfig)
        {
            string patientId = GetPatientId(filmBox);
            string sessionKey = $"{listenerConfig.AET}::{patientId}";

            var session = _sessions.GetOrAdd(sessionKey, _ =>
            {
                _logger.LogDebug("PdfSession: new session for PatientID={PID} AET={AET}",
                    patientId, listenerConfig.AET);
                return new PdfSession
                {
                    PatientId      = patientId,
                    PatientName    = GetPatientName(filmBox),
                    StudyId        = GetStudyId(filmBox),
                    StudyDate      = GetStudyDate(filmBox),
                    AET            = listenerConfig.AET,
                    OutputFolder   = listenerConfig.OutputFolder,
                    ListenerConfig = listenerConfig,
                    CreatedAt      = DateTime.UtcNow,
                    LastUpdated    = DateTime.UtcNow
                };
            });

            lock (session)
            {
                session.FilmBoxes.Add(filmBox);
                session.LastUpdated = DateTime.UtcNow;
                _logger.LogDebug(
                    "PdfSession: added FilmBox #{N} to session {Key}",
                    session.FilmBoxes.Count, sessionKey);
            }
        }

        /// <summary>يُغلق ويحفظ جلسة المريض يدوياً (عند اكتمال مهمة الطباعة).</summary>
        public async Task FlushSessionAsync(string aet, string patientId)
        {
            string sessionKey = $"{aet}::{patientId}";
            if (_sessions.TryRemove(sessionKey, out var session))
                await ClosePdfSessionAsync(session);
        }

        /// <summary>يُغلق جميع الجلسات المفتوحة.</summary>
        public async Task FlushAllAsync()
        {
            foreach (var key in _sessions.Keys.ToList())
            {
                if (_sessions.TryRemove(key, out var session))
                    await ClosePdfSessionAsync(session);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // إغلاق الجلسات المنتهية
        // ══════════════════════════════════════════════════════════════════════

        private void SweepExpiredSessions()
        {
            var cutoff = DateTime.UtcNow - _timeout;
            foreach (var (key, session) in _sessions)
            {
                if (session.LastUpdated < cutoff)
                {
                    if (_sessions.TryRemove(key, out _))
                    {
                        _logger.LogInformation(
                            "PdfSession: closing expired session for {PID}", session.PatientId);
                        _ = Task.Run(() => ClosePdfSessionAsync(session));
                    }
                }
            }
        }

        private async Task ClosePdfSessionAsync(PdfSession session)
        {
            List<FilmBox> boxes;
            lock (session)
            {
                boxes = session.FilmBoxes.ToList();
            }

            if (boxes.Count == 0)
            {
                _logger.LogDebug("PdfSession: empty session for {PID} — skipping", session.PatientId);
                return;
            }

            try
            {
                string folder = string.IsNullOrEmpty(session.OutputFolder)
                    ? Path.Combine(Environment.CurrentDirectory, "PrintOutput", "PDF")
                    : Path.Combine(session.OutputFolder, "PDF");

                Directory.CreateDirectory(folder);

                var ctx = new AnnotationContext
                {
                    PatientId   = session.PatientId,
                    PatientName = session.PatientName,
                    StudyId     = session.StudyId,
                    StudyDate   = session.StudyDate,
                    AET         = session.AET
                };

                string pdfPath = await Task.Run(() =>
                    _pdfExporter.ExportFilmBoxList(
                        boxes, folder, session.ListenerConfig, ctx));

                _logger.LogInformation(
                    "PdfSession: saved PDF for {PID} ({N} pages) → {Path}",
                    session.PatientId, boxes.Count, pdfPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PdfSession: failed to save PDF for {PID}", session.PatientId);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // مساعدات DICOM
        // ══════════════════════════════════════════════════════════════════════

        private static string GetPatientId(FilmBox fb)
            => fb.FilmSession.GetSingleValueOrDefault(DicomTag.PatientID, Guid.NewGuid().ToString("N")[..8]);

        private static string GetPatientName(FilmBox fb)
            => fb.FilmSession.GetSingleValueOrDefault(DicomTag.PatientName, "Unknown");

        private static string GetStudyId(FilmBox fb)
            => fb.FilmSession.GetSingleValueOrDefault(DicomTag.StudyID, "");

        private static string GetStudyDate(FilmBox fb)
            => fb.FilmSession.GetSingleValueOrDefault(DicomTag.StudyDate, DateTime.Now.ToString("yyyyMMdd"));

        public int ActiveSessionCount => _sessions.Count;

        public async ValueTask DisposeAsync()
        {
            await _sweepTimer.DisposeAsync();
            await FlushAllAsync();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // DTO داخلي
    // ──────────────────────────────────────────────────────────────────────────

    internal class PdfSession
    {
        public string         PatientId      { get; set; } = "";
        public string         PatientName    { get; set; } = "";
        public string         StudyId        { get; set; } = "";
        public string         StudyDate      { get; set; } = "";
        public string         AET            { get; set; } = "";
        public string         OutputFolder   { get; set; } = "";
        public ListenerConfig ListenerConfig { get; set; } = null!;
        public DateTime       CreatedAt      { get; set; }
        public DateTime       LastUpdated    { get; set; }
        public List<FilmBox>  FilmBoxes      { get; } = new();
    }
}
