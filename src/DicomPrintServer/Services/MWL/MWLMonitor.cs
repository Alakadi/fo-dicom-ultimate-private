using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace DicomPrintServer.Services.MWL
{
    public class MWLMonitor
    {
        private readonly ILogger<MWLMonitor> _logger;

        private long _totalQueries;
        private long _totalResultsReturned;
        private long _totalQueryErrors;
        private long _totalAssociationsAccepted;
        private long _totalAssociationsRejected;
        private long _totalCFindPending;
        private long _totalCFindCompleted;

        private readonly ConcurrentDictionary<string, MWLQueryStats> _queryStatsByCallingAE = new();
        private readonly ConcurrentQueue<MWLQueryRecord> _recentQueries = new();
        private const int MaxRecentQueries = 200;

        public DateTime StartedAt { get; } = DateTime.UtcNow;

        public MWLMonitor(ILogger<MWLMonitor> logger)
        {
            _logger = logger;
        }

        public void RecordAssociationAccepted(string callingAE, string calledAE, string remoteHost)
        {
            Interlocked.Increment(ref _totalAssociationsAccepted);
            _logger.LogDebug("[MWL Monitor] Association accepted — {CallingAE} → {CalledAE} from {Host}",
                callingAE, calledAE, remoteHost);
        }

        public void RecordAssociationRejected(string callingAE, string calledAE, string reason)
        {
            Interlocked.Increment(ref _totalAssociationsRejected);
            _logger.LogWarning("[MWL Monitor] Association rejected — {CallingAE} → {CalledAE}: {Reason}",
                callingAE, calledAE, reason);
        }

        public void RecordQuery(string callingAE, string patientName, string patientID, int resultCount, TimeSpan duration)
        {
            Interlocked.Increment(ref _totalQueries);
            Interlocked.Add(ref _totalResultsReturned, resultCount);

            GetQueryStats(callingAE).IncrementQuery(resultCount, duration);

            var record = new MWLQueryRecord
            {
                CallingAE      = callingAE,
                PatientName     = patientName,
                PatientID       = patientID,
                ResultCount     = resultCount,
                Duration        = duration,
                Timestamp       = DateTime.UtcNow,
                Success         = true
            };
            EnqueueRecord(record);

            _logger.LogInformation(
                "[MWL Monitor] C-FIND completed — AE={CallingAE} Patient={PatientName}/{PatientID} Results={Count} Duration={Duration:N0}ms",
                callingAE, patientName, patientID, resultCount, duration.TotalMilliseconds);
        }

        public void RecordQueryError(string callingAE, string errorMessage)
        {
            Interlocked.Increment(ref _totalQueries);
            Interlocked.Increment(ref _totalQueryErrors);

            var record = new MWLQueryRecord
            {
                CallingAE   = callingAE,
                Timestamp   = DateTime.UtcNow,
                Success     = false,
                ErrorMessage = errorMessage
            };
            EnqueueRecord(record);

            _logger.LogWarning("[MWL Monitor] C-FIND error — AE={CallingAE}: {Error}", callingAE, errorMessage);
        }

        public void RecordCFindPending()
        {
            Interlocked.Increment(ref _totalCFindPending);
        }

        public void RecordCFindCompleted()
        {
            Interlocked.Increment(ref _totalCFindCompleted);
        }

        public MWLGlobalStats GetGlobalStats() => new MWLGlobalStats
        {
            TotalQueries              = Interlocked.Read(ref _totalQueries),
            TotalResultsReturned      = Interlocked.Read(ref _totalResultsReturned),
            TotalQueryErrors          = Interlocked.Read(ref _totalQueryErrors),
            TotalAssociationsAccepted = Interlocked.Read(ref _totalAssociationsAccepted),
            TotalAssociationsRejected = Interlocked.Read(ref _totalAssociationsRejected),
            TotalCFindPending         = Interlocked.Read(ref _totalCFindPending),
            TotalCFindCompleted       = Interlocked.Read(ref _totalCFindCompleted),
            UptimeSeconds             = (long)(DateTime.UtcNow - StartedAt).TotalSeconds
        };

        public IReadOnlyDictionary<string, MWLQueryStats> GetAllQueryStats()
            => _queryStatsByCallingAE;

        public IReadOnlyList<MWLQueryRecord> GetRecentQueries(int count = 50)
            => _recentQueries.TakeLast(Math.Min(count, MaxRecentQueries)).ToList();

        private MWLQueryStats GetQueryStats(string callingAE)
            => _queryStatsByCallingAE.GetOrAdd(callingAE, _ => new MWLQueryStats());

        private void EnqueueRecord(MWLQueryRecord r)
        {
            _recentQueries.Enqueue(r);
            while (_recentQueries.Count > MaxRecentQueries)
                _recentQueries.TryDequeue(out _);
        }
    }

    public class MWLGlobalStats
    {
        public long TotalQueries              { get; init; }
        public long TotalResultsReturned      { get; init; }
        public long TotalQueryErrors          { get; init; }
        public long TotalAssociationsAccepted { get; init; }
        public long TotalAssociationsRejected { get; init; }
        public long TotalCFindPending         { get; init; }
        public long TotalCFindCompleted       { get; init; }
        public long UptimeSeconds             { get; init; }
    }

    public class MWLQueryStats
    {
        private long _queryCount, _totalResults, _errorCount;
        private long _totalDurationMs, _sampleCount;

        public long QueryCount     => Interlocked.Read(ref _queryCount);
        public long TotalResults   => Interlocked.Read(ref _totalResults);
        public long ErrorCount     => Interlocked.Read(ref _errorCount);
        public double AverageDurationMs
        {
            get
            {
                long n = Interlocked.Read(ref _sampleCount);
                return n == 0 ? 0 : (double)Interlocked.Read(ref _totalDurationMs) / n;
            }
        }

        internal void IncrementQuery(int results, TimeSpan duration)
        {
            Interlocked.Increment(ref _queryCount);
            Interlocked.Add(ref _totalResults, results);
            Interlocked.Add(ref _totalDurationMs, (long)duration.TotalMilliseconds);
            Interlocked.Increment(ref _sampleCount);
        }

        internal void IncrementError()
        {
            Interlocked.Increment(ref _errorCount);
        }
    }

    public class MWLQueryRecord
    {
        public string    CallingAE    { get; init; } = "";
        public string    PatientName  { get; init; } = "";
        public string    PatientID    { get; init; } = "";
        public int       ResultCount  { get; init; }
        public TimeSpan  Duration     { get; init; }
        public DateTime  Timestamp    { get; init; }
        public bool      Success      { get; init; }
        public string?   ErrorMessage { get; init; }
    }
}
