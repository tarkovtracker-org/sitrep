namespace Sitrep.Desktop;

internal interface IProcessIdentity : IDisposable
{
    string Name { get; }
    bool IsAlive { get; }
}

/// <summary>A name is reusable only while its owned process handle still identifies a live process.</summary>
internal sealed class ProcessIdentityCache(Func<uint, IProcessIdentity?> open, Func<long> milliseconds) : IDisposable
{
    private readonly object _gate = new();
    private uint _pid;
    private IProcessIdentity? _identity;
    private long _retryAfter;

    public string GetName(uint pid)
    {
        lock (_gate)
        {
            if (pid == 0) { return string.Empty; }
            if (_pid == pid && _identity is not null && _identity.IsAlive)
            {
                return _identity.Name;
            }
            // Failed lookups are throttled, but never return a previous process's name.
            if (_pid == pid && _identity is null && milliseconds() < _retryAfter)
            {
                return string.Empty;
            }
            _identity?.Dispose();
            _identity = null;
            _pid = pid;
            _retryAfter = milliseconds() + 1000;
            _identity = open(pid);
            if (_identity is not null && !_identity.IsAlive)
            {
                // Exited between open and check, or liveness cannot be confirmed on the granted handle:
                // treat like a denied lookup so the throttle applies instead of reopening at input rate.
                _identity.Dispose();
                _identity = null;
            }
            return _identity?.Name ?? string.Empty;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _identity?.Dispose();
            _identity = null;
            _pid = 0;
            _retryAfter = 0;
        }
    }
}
