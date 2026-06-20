using System.Collections.Concurrent;

namespace DicomPrintServer.Services
{
    /// <summary>
    /// Rate Limiter للـ Admin API — يمنع Brute-Force و DoS.
    ///
    /// الآلية: Token Bucket لكل IP:
    ///   - كل IP لديه دلو بسعة MaxBurst رمز
    ///   - يتجدد بمعدل RefillPerSecond رمز في الثانية
    ///   - عند نفاد الرموز → يُرفض الطلب بـ 429 Too Many Requests
    ///
    /// إعدادات افتراضية:
    ///   - 60 طلب/دقيقة للطلبات العادية
    ///   - 10 طلب/دقيقة لمحاولات المصادقة (أكثر صرامة)
    ///   - تنظيف الإدخالات القديمة كل 5 دقائق
    /// </summary>
    public class AdminRateLimiter
    {
        private readonly int _maxBurst;
        private readonly double _refillPerSecond;
        private readonly int _authMaxBurst;
        private readonly double _authRefillPerSecond;

        private readonly ConcurrentDictionary<string, BucketState> _buckets   = new();
        private readonly ConcurrentDictionary<string, BucketState> _authBuckets = new();
        private readonly Timer _cleanupTimer;

        public AdminRateLimiter(
            int maxBurst         = 30,   // دفعة أولى
            int requestsPerMin   = 60,   // طلبات عادية / دقيقة
            int authMaxBurst     = 5,    // دفعة أولى لمحاولات المصادقة
            int authPerMin       = 10)   // محاولات مصادقة / دقيقة
        {
            _maxBurst           = maxBurst;
            _refillPerSecond    = requestsPerMin  / 60.0;
            _authMaxBurst       = authMaxBurst;
            _authRefillPerSecond = authPerMin     / 60.0;

            // تنظيف كل 5 دقائق للذاكرة
            _cleanupTimer = new Timer(_ => Cleanup(), null,
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        /// <summary>يتحقق من الطلبات العامة — يُعيد true إذا مسموح.</summary>
        public bool IsAllowed(string clientIp)
            => Check(_buckets, clientIp, _maxBurst, _refillPerSecond);

        /// <summary>يتحقق من محاولات المصادقة — أكثر صرامة.</summary>
        public bool IsAuthAllowed(string clientIp)
            => Check(_authBuckets, clientIp, _authMaxBurst, _authRefillPerSecond);

        /// <summary>إحصائيات للـ Health Check.</summary>
        public (int TotalIPs, int BlockedIPs) GetStats()
        {
            int total   = _buckets.Count + _authBuckets.Count;
            int blocked = _buckets.Values.Count(b => b.Tokens < 1)
                        + _authBuckets.Values.Count(b => b.Tokens < 1);
            return (total, blocked);
        }

        // ── Token Bucket ─────────────────────────────────────────────────────

        private static bool Check(
            ConcurrentDictionary<string, BucketState> buckets,
            string ip, int maxBurst, double refillPerSec)
        {
            var now    = DateTime.UtcNow;
            var bucket = buckets.GetOrAdd(ip, _ => new BucketState(maxBurst, now));

            lock (bucket)
            {
                // تجديد الرموز بناءً على الوقت المنقضي
                double elapsed = (now - bucket.LastRefill).TotalSeconds;
                bucket.Tokens     = Math.Min(maxBurst, bucket.Tokens + elapsed * refillPerSec);
                bucket.LastRefill = now;

                if (bucket.Tokens < 1.0)
                    return false; // مرفوض

                bucket.Tokens -= 1.0;
                return true;
            }
        }

        private void Cleanup()
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-10);
            foreach (var key in _buckets.Keys.ToList())
                if (_buckets.TryGetValue(key, out var b) && b.LastRefill < cutoff)
                    _buckets.TryRemove(key, out _);

            foreach (var key in _authBuckets.Keys.ToList())
                if (_authBuckets.TryGetValue(key, out var b) && b.LastRefill < cutoff)
                    _authBuckets.TryRemove(key, out _);
        }

        private sealed class BucketState(double initialTokens, DateTime now)
        {
            public double   Tokens    = initialTokens;
            public DateTime LastRefill = now;
        }
    }
}
