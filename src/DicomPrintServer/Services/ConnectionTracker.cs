using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace DicomPrintServer.Services
{
    public class ConnectionInfo
    {
        public string CallingAE { get; set; } = "";
        public string CalledAE { get; set; } = "";
        public string RemoteHost { get; set; } = "";
        public int Port { get; set; }
        public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastActivityAt { get; set; }
        public string AssociationId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    }

    public interface IConnectionTracker
    {
        void RegisterConnection(string callingAE, string calledAE, string remoteHost, int port);
        void UnregisterConnection(string callingAE, string calledAE);
        void UpdateActivity(string callingAE, string calledAE);
        IReadOnlyList<ConnectionInfo> GetActiveConnections();
        int ActiveCount { get; }
    }

    public class ConnectionTracker : IConnectionTracker
    {
        private readonly ConcurrentDictionary<string, ConnectionInfo> _connections = new();
        private readonly ILogger<ConnectionTracker> _logger;

        public ConnectionTracker(ILogger<ConnectionTracker> logger)
        {
            _logger = logger;
        }

        public void RegisterConnection(string callingAE, string calledAE, string remoteHost, int port)
        {
            var key = $"{callingAE}::{calledAE}";
            var info = new ConnectionInfo
            {
                CallingAE = callingAE,
                CalledAE = calledAE,
                RemoteHost = remoteHost,
                Port = port,
                ConnectedAt = DateTime.UtcNow,
                LastActivityAt = DateTime.UtcNow
            };

            _connections[key] = info;
            _logger.LogInformation("Device connected: {CallingAE} -> {CalledAE} from {RemoteHost}:{Port}",
                callingAE, calledAE, remoteHost, port);
        }

        public void UnregisterConnection(string callingAE, string calledAE)
        {
            var key = $"{callingAE}::{calledAE}";
            if (_connections.TryRemove(key, out var info))
            {
                _logger.LogInformation("Device disconnected: {CallingAE} -> {CalledAE} (duration: {Duration})",
                    callingAE, calledAE, DateTime.UtcNow - info.ConnectedAt);
            }
        }

        public void UpdateActivity(string callingAE, string calledAE)
        {
            var key = $"{callingAE}::{calledAE}";
            if (_connections.TryGetValue(key, out var info))
            {
                info.LastActivityAt = DateTime.UtcNow;
            }
        }

        public IReadOnlyList<ConnectionInfo> GetActiveConnections()
        {
            return _connections.Values.OrderBy(c => c.ConnectedAt).ToList();
        }

        public int ActiveCount => _connections.Count;
    }
}