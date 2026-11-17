using System.Diagnostics;

namespace VelocityDM.Core.Services;

/// <summary>
/// Thread-safe async token-bucket rate limiter shared across all downloads and segments.
/// Supports instant runtime reconfiguration (including 0 = unlimited).
/// </summary>
public sealed class TokenBucketRateLimiter
{
    private readonly object _sync = new();
    private long _bytesPerSecond;
    private double _availableTokens;
    private long _lastTimestamp;
    private double _burstCapacity;

    public TokenBucketRateLimiter(long bytesPerSecond = 0)
    {
        _lastTimestamp = Stopwatch.GetTimestamp();
        UpdateLimit(bytesPerSecond);
    }

    public long BytesPerSecond
    {
        get
        {
            lock (_sync)
            {
                return _bytesPerSecond;
            }
        }
    }

    public void UpdateLimit(long bytesPerSecond)
    {
        lock (_sync)
        {
            _bytesPerSecond = Math.Max(0, bytesPerSecond);
            // Allow up to 0.5s burst (minimum 16 KB) so small reads aren't stuttered
            _burstCapacity = _bytesPerSecond > 0
                ? Math.Max(16 * 1024, _bytesPerSecond * 0.5)
                : 0;
            _availableTokens = Math.Min(_availableTokens, _burstCapacity);
            if (_availableTokens <= 0 && _bytesPerSecond > 0)
            {
                _availableTokens = Math.Min(_burstCapacity, Math.Max(8192, _bytesPerSecond * 0.1));
            }
            _lastTimestamp = Stopwatch.GetTimestamp();
        }
    }

    public async ValueTask ThrottleAsync(int requestedBytes, CancellationToken cancellationToken)
    {
        if (requestedBytes <= 0)
            return;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int delayMs = 0;
            lock (_sync)
            {
                if (_bytesPerSecond <= 0)
                    return;

                RefillTokensLocked();

                if (_availableTokens >= requestedBytes)
                {
                    _availableTokens -= requestedBytes;
                    return;
                }

                // Consume partial tokens if burst cap is smaller than requestedBytes, wait only for deficit
                double deficit = requestedBytes - _availableTokens;
                delayMs = (int)Math.Ceiling(deficit / _bytesPerSecond * 1000.0);
                delayMs = Math.Clamp(delayMs, 5, 250);
            }

            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private void RefillTokensLocked()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsedSeconds = (double)(now - _lastTimestamp) / Stopwatch.Frequency;
        if (elapsedSeconds > 0)
        {
            _availableTokens = Math.Min(_burstCapacity, _availableTokens + (elapsedSeconds * _bytesPerSecond));
            _lastTimestamp = now;
        }
    }
}
