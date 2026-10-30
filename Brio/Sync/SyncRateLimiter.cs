using System;

namespace Brio.Sync;

// A token bucket for messages sent to the relay, shared by scene updates (framework thread)
// and file transfers (background), so together they stay under the relay's limit.
public class SyncRateLimiter
{
    private readonly object _lock = new();
    private double _tokens = SyncLimits.MessageBurst;
    private DateTime _lastRefill = DateTime.UtcNow;

    // Takes one message's worth if at least `keep` would still be left afterwards. File
    // transfers keep a few back so scene updates never wait behind them.
    public bool TryTake(int keep = 0)
    {
        lock(_lock)
        {
            var now = DateTime.UtcNow;
            _tokens = Math.Min(SyncLimits.MessageBurst, _tokens + (now - _lastRefill).TotalSeconds * SyncLimits.MessagesPerSecond);
            _lastRefill = now;

            if(_tokens < 1 + keep)
                return false;

            _tokens -= 1;
            return true;
        }
    }
}
