namespace Sitrep.Desktop;

/// <summary>UI-thread-owned shell registration; a failed add must never strand a hidden window.</summary>
internal sealed class TrayRegistration(Func<bool> add, Action delete) : IDisposable
{
    public bool IsRegistered { get; private set; }
    private bool _disposed;

    public bool EnsureRegistered()
    {
        if (!_disposed && !IsRegistered)
        {
            IsRegistered = add();
        }
        return IsRegistered;
    }

    public bool ShellRestarted()
    {
        // Explorer lost its registrations, so there is no old entry to delete.
        IsRegistered = false;
        return EnsureRegistered();
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        if (IsRegistered)
        {
            IsRegistered = false;
            delete();
        }
    }
}
